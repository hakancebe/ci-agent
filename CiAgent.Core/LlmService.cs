using System.ClientModel;
using System.Text;
using System.Text.Json;
using OpenAI;
using OpenAI.Chat;

namespace CiAgent.Core;

public class LlmService
{
    // Test constructor'ında kurulmaz (bkz. aşağıdaki internal ctor); gerçek
    // kullanımda her zaman dolu.
    private readonly ChatClient? _chat;

    // Prompt üst sınırı. Ham log artık kırpılmıyor - ya tamamı gider ya da hiç
    // analiz yapılmaz. Ölçümle belirlendi; gerçek senaryolarda üretilen nihai
    // prompt boyutları:
    //
    //   senaryo                                    prompt
    //   build (CS1002, konum tam)                     129 kr
    //   test (8 fail, konum tam)                    1.015 kr
    //   restore (NU1101)                            3.572 kr
    //   deploy (generic + 60 annotation)            7.122 kr
    //   test (konum belirsiz, 48 KB ham log)       48.131 kr  <- en büyük
    //
    // En büyük gerçek senaryo 48.131'de, yani limitin %96'sında oturuyor -
    // pay bilinçli olarak dar. Ölçülen kırılma noktası: sanitize sonrası
    // RawStepLog ~49.870 kr (~59 KB ham log / ~325 farklı stack trace satırı).
    // Bunun altındaki her şey tam hâliyle LLM'e gider, üstündeki hiç gitmez.
    private const int MaxPromptChars = 50_000;

    private const string SystemPrompt = """
        Sen bir CI/CD hata analiz asistanısın. Sana bir GitHub Actions run'ının
        başarısız olan job/adımlarına ait hatalar, log kesiti ve metadata verilecek.

        Kurallar:
        - Sadece verilen veriye dayan, tahmin uydurma.
        - En önemlisi: KÖK NEDENE göre grupla. Birden fazla hata AYNI kök nedenden
          geliyorsa (ör. 5 test tek bir bozuk metot yüzünden patlıyorsa) bunları TEK
          bir analysis elemanında birleştir; hangi hataları kapsadığını rootCause'da
          söyle. Yalnızca gerçekten BAĞIMSIZ sorunlar için ayrı eleman üret.
        - analyses listesi hata sayısı kadar uzun OLMAK ZORUNDA DEĞİL; genelde çok
          daha kısadır.
        - summary tüm run'ı tek cümlede özetlesin.
        - Her analysis için title kısa bir başlık olsun (raporda bölüm adı olacak).
        - Log yetersizse o analysis'in confidence alanını "low" yap ve bunu
          rootCause'da belirt.
        - suggestedFix somut ve uygulanabilir olsun (hangi dosyada ne değişecek).
        - Kod kesiti verilmişse (>> işaretli satır), suggestedFix'te bu satıra somut ve
          uygulanabilir bir değişiklik öner, genel tavsiye verme.
        - Türkçe cevap ver.
        - Yanıtı yalnızca istenen JSON şemasında döndür.
        """;

    private const string JsonSchema = """
        {
          "type": "object",
          "properties": {
            "summary": { "type": "string" },
            "analyses": {
              "type": "array",
              "items": {
                "type": "object",
                "properties": {
                  "title":        { "type": "string" },
                  "rootCause":    { "type": "string" },
                  "suggestedFix": { "type": "string" },
                  "confidence":   { "type": "string", "enum": ["high", "medium", "low"] },
                  "affectedFile": { "type": ["string", "null"] },
                  "affectedLine": { "type": ["integer", "null"] }
                },
                "required": ["title", "rootCause", "suggestedFix", "confidence", "affectedFile", "affectedLine"],
                "additionalProperties": false
              }
            }
          },
          "required": ["summary", "analyses"],
          "additionalProperties": false
        }
        """;
    
    public LlmService(string endpoint, string apiKey, string deployment)
    {
        var client = new OpenAIClient(
            new ApiKeyCredential(apiKey),
            new OpenAIClientOptions { Endpoint = new Uri(endpoint) });

        _chat = client.GetChatClient(deployment);
    }

    // Yalnızca testler için: gerçek ChatClient kurulmaz, dolayısıyla bu
    // constructor'la üretilen örnek ağa hiç çıkamaz. Testler CompleteAsync'i
    // override ederek çağrının gidip gitmediğini sayar.
    internal LlmService()
    {
        _chat = null;
    }

    // Transport seam'i: gerçek Azure OpenAI çağrısını tek bir yerde topluyor ki
    // testler bunu override edip OpenAI SDK'sının model tipleriyle uğraşmadan
    // sahte JSON dönebilsin.
    internal virtual async Task<string> CompleteAsync(
        List<ChatMessage> messages, ChatCompletionOptions options)
    {
        if (_chat is null)
            throw new InvalidOperationException(
                "LlmService test constructor'ıyla kuruldu; CompleteAsync override edilmeliydi.");

        ChatCompletion completion = await _chat.CompleteChatAsync(messages, options);
        return completion.Content[0].Text;
    }

    // Geçici hatalarda (429 rate limit, 500/502/503/504, ağ kesintisi) tek denemede
    // pes etmek yazık: CI'da paralel job'lar aynı anda Azure OpenAI'a vurduğunda 429
    // sıradan bir olay. Kalıcı hatalarda (401 yanlış key, 404 yanlış deployment adı)
    // beklemek anlamsız - onlar hemen yukarı fırlıyor.
    private const int MaxAttempts = 3;

    // Test override edebilsin diye virtual: gerçek bekleme yapmadan retry sayılabiliyor.
    internal virtual Task DelayAsync(TimeSpan duration) => Task.Delay(duration);

    private async Task<string> CompleteWithRetryAsync(
        List<ChatMessage> messages, ChatCompletionOptions options)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await CompleteAsync(messages, options);
            }
            catch (Exception ex) when (attempt < MaxAttempts && IsTransient(ex))
            {
                // 2sn, 4sn: CI job'ının toplam süresini anlamlı ölçüde uzatmayan,
                // ama kısa rate-limit penceresini atlatmaya yeten bir bekleme.
                var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt));
                Console.Error.WriteLine(
                    $"[LlmService] Deneme {attempt}/{MaxAttempts} geçici hatayla başarısız "
                    + $"({ex.GetType().Name}: {ex.Message}). {delay.TotalSeconds:0}sn sonra tekrar denenecek.");
                await DelayAsync(delay);
            }
        }
    }

    /// <summary>
    /// Tekrar denemeye değer mi? Status kodu okunabiliyorsa ona bakıyoruz;
    /// okunamıyorsa (saf ağ/timeout hatası) geçici sayıyoruz.
    /// </summary>
    internal static bool IsTransient(Exception ex) => ex switch
    {
        ClientResultException cre => cre.Status is 408 or 429 or 500 or 502 or 503 or 504,
        HttpRequestException => true,
        TaskCanceledException => true,   // HttpClient timeout'u bu şekilde yüzeye çıkıyor
        _ => false
    };

    public async Task<AnalysisResult?> AnalyzeAsync(ErrorContext context)
    {
        // Limit aşıldığında eskiden analiz TAMAMEN atlanıyordu ("ya hep ya hiç").
        // Artık kademeli düşüş: en zengin prompt'tan başlayıp sığana kadar bilgi
        // katmanlarını en az değerliden başlayarak çıkarıyoruz. Kullanıcı için en
        // kötü sonuç "büyük log geldi, hiçbir şey söylemedim" - bu merdiven onu
        // yalnızca gerçekten hiçbir şeyin sığmadığı durumla sınırlıyor.
        var (prompt, budget) = FitPrompt(context);

        if (prompt is null)
        {
            // Hiçbir kademe sığmadı: hata mesajının kendisi tek başına limitin
            // üstünde (patolojik ama mümkün - ör. devasa bir assert diff'i).
            return AnalysisResult.ForSkipped(BuildPrompt(context).Length, MaxPromptChars);
        }

        var options = new ChatCompletionOptions
        {
            //Kendince bir şey eklmemesi için 0.2f belirliyoruz
            Temperature = 0.2f,
            ResponseFormat = ChatResponseFormat.CreateJsonSchemaFormat(
                jsonSchemaFormatName: "analysis_result",
                jsonSchema: BinaryData.FromString(JsonSchema),
                jsonSchemaIsStrict: true)
        };

        List<ChatMessage> messages =
        [
            new SystemChatMessage(SystemPrompt),
            new UserChatMessage(prompt)
        ];

        var json = await CompleteWithRetryAsync(messages, options);

        //json to AnalysisResult type
        var result = JsonSerializer.Deserialize<AnalysisResult>(json);

        // Bir şey feda edildiyse bunu sonuca iliştiriyoruz ki rapor "analiz eksik
        // veriyle yapıldı" diyebilsin - sessizce kaybolmasın.
        if (result is not null && budget.Describe(context) is string note)
            result.ReductionNote = note;

        return result;
    }

    /// <summary>
    /// Prompt bütçesi: hangi bilgi katmanlarının prompt'a dahil edileceği.
    /// Varsayılan (<see cref="Full"/>) hiçbir şey feda etmez.
    /// </summary>
    internal sealed record PromptBudget(
        bool IncludeRawLog = true,
        bool IncludeCodeSnippets = true,
        int? MaxFailures = null)
    {
        public static readonly PromptBudget Full = new();

        public bool IsFull => IncludeRawLog && IncludeCodeSnippets && MaxFailures is null;

        /// <summary>Feda edilenlerin insan tarafından okunabilir özeti; tam bütçede null.</summary>
        public string? Describe(ErrorContext ctx)
        {
            if (IsFull) return null;

            var dropped = new List<string>();
            if (!IncludeRawLog) dropped.Add("ham log kesiti");
            if (!IncludeCodeSnippets) dropped.Add("kod kesitleri");
            if (MaxFailures is int n)
                dropped.Add($"{FailureGrouper.Group(ctx.Failures).Count} farklı hatadan yalnızca ilk {n}'i");

            return $"Prompt {MaxPromptChars:N0} karakter limitine sığması için şunlar çıkarıldı: "
                 + string.Join(", ", dropped) + ".";
        }
    }

    /// <summary>
    /// Sığana kadar bütçe kademelerini sırayla dener. Sıra, bilginin analiz değerine
    /// göre: ham log önce gider (ayrıştırılmış mesajın büyük ölçüde tekrarı), sonra
    /// kod kesitleri, en son hata sayısı kırpılır. Annotation'lar hiç kırpılmıyor -
    /// ölçülen en büyük annotation yükü ~7 KB, yani limiti asla tek başına zorlamıyor,
    /// buna karşılık GitHub'ın yapılandırılmış hata verisi olarak değeri yüksek.
    /// Hiçbiri sığmazsa (null, _) döner.
    /// </summary>
    internal static (string? Prompt, PromptBudget Budget) FitPrompt(ErrorContext ctx)
    {
        var ladder = new List<PromptBudget>
        {
            PromptBudget.Full,
            new(IncludeRawLog: false),
            new(IncludeRawLog: false, IncludeCodeSnippets: false),
        };

        // Son çare: gösterilen hata GRUBU sayısını azalt. Yalnızca Failures listesi
        // doluysa anlamlı - kırpma hem kod kesitlerini hem ayrıştırılmış mesajı küçültür.
        // Tekrarlar zaten gruplandığı için buradaki kırpma gerçekten farklı hataları
        // eler, aynı hatanın kopyalarını değil.
        for (var n = FailureGrouper.Group(ctx.Failures).Count - 1; n >= 1; n--)
            ladder.Add(new PromptBudget(IncludeRawLog: false, IncludeCodeSnippets: false, MaxFailures: n));

        foreach (var budget in ladder)
        {
            var prompt = BuildPrompt(ctx, budget);
            if (prompt.Length <= MaxPromptChars)
                return (prompt, budget);
        }

        return (null, PromptBudget.Full);
    }

    // ---------------------------------------------------------------------
    // /fix: kod düzeltme önerisi üretme
    // ---------------------------------------------------------------------

    private const string FixSystemPrompt = """
        Sen bir CI/CD hata düzeltme asistanısın. Sana başarısız bir CI run'ının
        analizi ve ilgili kaynak dosyaların İÇERİĞİ verilecek. Görevin hatayı
        gideren en küçük kod değişikliğini önermek.

        Değişiklikleri "bul ve değiştir" biçiminde ver:
        - oldText: dosyada ŞU AN BİREBİR var olan metin. Kopyaladığın metin
          dosyadaki hâliyle harfi harfine aynı olmalı (girinti dahil).
        - oldText o dosyada YALNIZCA BİR KEZ geçecek kadar uzun olsun. Kısa ve
          birden çok yerde geçen bir metin verirsen değişiklik reddedilir; şüphedeysen
          çevresindeki birkaç satırı da ekleyerek benzersiz hale getir.
        - newText: yerine gelecek metin.

        Kesin kurallar:
        - EN KÜÇÜK değişikliği yap. Alakasız yeniden düzenleme, biçimlendirme,
          yorum ekleme YOK.
        - Testleri DEĞİŞTİRME, silme, zayıflatma. Test dosyalarına dokunma.
          Görevin testi geçirmek değil, testin yakaladığı hatayı düzeltmek.
        - Sadece verilen dosya içeriklerine dayan. Görmediğin bir dosyayı düzenleme.
        - Hatayı verilen bilgiyle güvenle düzeltemiyorsan edits listesini BOŞ bırak
          ve summary'de nedenini yaz. Uydurma bir değişiklik yapmaktan iyidir.
        - Türkçe cevap ver (kod hariç).
        """;

    private const string FixJsonSchema = """
        {
          "type": "object",
          "properties": {
            "summary": { "type": "string" },
            "edits": {
              "type": "array",
              "items": {
                "type": "object",
                "properties": {
                  "file":    { "type": "string" },
                  "oldText": { "type": "string" },
                  "newText": { "type": "string" },
                  "reason":  { "type": "string" }
                },
                "required": ["file", "oldText", "newText", "reason"],
                "additionalProperties": false
              }
            }
          },
          "required": ["summary", "edits"],
          "additionalProperties": false
        }
        """;

    /// <summary>
    /// Analiz + dosya içerikleri -> somut kod değişikliği önerisi.
    /// </summary>
    /// <param name="files">Yol -> dosya içeriği. Modele yalnızca bunlar gösterilir.</param>
    /// <param name="previousAttempt">
    /// Önceki deneme başarısız olduysa, o denemede ne yapıldığı ve doğrulamanın ne
    /// hata verdiği. Modelin aynı yanlışı tekrarlamaması için prompt'a ekleniyor.
    /// </param>
    public async Task<FixProposal?> ProposeFixAsync(
        ErrorContext context,
        AnalysisResult analysis,
        IReadOnlyDictionary<string, string> files,
        string? previousAttempt = null)
    {
        var prompt = BuildFixPrompt(context, analysis, files, previousAttempt);

        // Analiz tarafındaki kademeli düşüş burada yok: düzeltme için dosya
        // içeriği ZORUNLU, kırpılırsa model olmayan bir metni "birebir" sanıp
        // uydurur. Sığmıyorsa düzeltmeyi hiç denememek doğru.
        if (prompt.Length > MaxPromptChars)
            return null;

        var options = new ChatCompletionOptions
        {
            Temperature = 0.1f,   // kod üretiminde analizden de az serbestlik
            ResponseFormat = ChatResponseFormat.CreateJsonSchemaFormat(
                jsonSchemaFormatName: "fix_proposal",
                jsonSchema: BinaryData.FromString(FixJsonSchema),
                jsonSchemaIsStrict: true)
        };

        List<ChatMessage> messages =
        [
            new SystemChatMessage(FixSystemPrompt),
            new UserChatMessage(prompt)
        ];

        var json = await CompleteWithRetryAsync(messages, options);
        return JsonSerializer.Deserialize<FixProposal>(json);
    }

    internal static string BuildFixPrompt(
        ErrorContext context,
        AnalysisResult analysis,
        IReadOnlyDictionary<string, string> files,
        string? previousAttempt)
    {
        var sb = new StringBuilder();

        sb.AppendLine($"Başarısız job: {context.JobName} / {context.FailedStepName}");
        sb.AppendLine();

        sb.AppendLine("Analiz özeti:");
        sb.AppendLine(analysis.Summary);
        sb.AppendLine();

        for (var i = 0; i < analysis.Analyses.Count; i++)
        {
            var a = analysis.Analyses[i];
            sb.AppendLine($"{i + 1}) {a.Title}");
            sb.AppendLine($"   Kök neden: {a.RootCause}");
            sb.AppendLine($"   Önerilen çözüm: {a.SuggestedFix}");
            if (a.AffectedFile is not null)
                sb.AppendLine($"   Konum: {a.AffectedFile}{(a.AffectedLine is int l ? $":{l}" : "")}");
            sb.AppendLine();
        }

        sb.AppendLine("Hata mesajları:");
        foreach (var g in FailureGrouper.Group(context.Failures))
        {
            var f = g.Representative;
            var loc = f.FilePath is not null
                ? $" ({f.FilePath}{(f.LineNumber is int ln ? $":{ln}" : "")})"
                : "";
            sb.AppendLine($"- {f.Name ?? f.Kind.ToString()}{loc}: {Masker.Mask(f.Message)}");
        }
        sb.AppendLine();

        // Önceki deneme neden tutmadı - modelin aynı duvara tekrar toslamaması için.
        if (!string.IsNullOrWhiteSpace(previousAttempt))
        {
            sb.AppendLine("ÖNCEKİ DENEME BAŞARISIZ OLDU:");
            sb.AppendLine(previousAttempt);
            sb.AppendLine("Bu sefer farklı bir yaklaşım dene.");
            sb.AppendLine();
        }

        // Dosya içerikleri Masker'dan geçirilmiyor: bu kaynak kod, log değil.
        // Maskeleme kuralları koddaki "password" gibi değişken adlarını bozar ve
        // model bozulmuş metni "birebir" sanıp eşleşmeyen oldText üretirdi.
        // İçerik bilinçli olarak SATIR NUMARASIZ veriliyor: oldText dosyadaki
        // metinle birebir eşleşmek zorunda ve numaralı gösterim modelin "42: "
        // önekini de kopyalamasına yol açıyor - eşleşme tutmuyor. Hatanın hangi
        // satırda olduğu zaten yukarıdaki analiz bölümünde yazıyor.
        sb.AppendLine("Dosya içerikleri (oldText bu metinle BİREBİR eşleşmeli):");

        foreach (var (path, content) in files)
        {
            sb.AppendLine();
            sb.AppendLine($"--- {path} ---");
            sb.AppendLine("```");
            sb.AppendLine(content.TrimEnd());
            sb.AppendLine("```");
        }

        return sb.ToString();
    }

    // internal (private değil): AnalyzeAsync'in ölçtüğü uzunluk testlerden
    // doğrudan doğrulanabilsin diye (bkz. AssemblyInfo.cs -> InternalsVisibleTo).
    // Bütçesiz overload = hiçbir şey feda edilmemiş tam prompt.
    internal static string BuildPrompt(ErrorContext ctx) => BuildPrompt(ctx, PromptBudget.Full);

    internal static string BuildPrompt(ErrorContext ctx, PromptBudget budget)
    {
        var sb = new StringBuilder();

        // Tekrarları (matrix build'de aynı test N job'da) tek başlıkta topluyoruz;
        // hata sayısı kırpıldıysa ilk N GRUP gösteriliyor.
        var allGroups = FailureGrouper.Group(ctx.Failures);
        var shownGroups = budget.MaxFailures is int max ? allGroups.Take(max).ToList() : allGroups;

        sb.AppendLine($"Job adı: {ctx.JobName}");
        sb.AppendLine($"Başarısız adım: {ctx.FailedStepName}");

        if (shownGroups.Count > 0)
        {
            var header = shownGroups.Count < allGroups.Count
                ? $"Tespit edilen hatalar (toplam {allGroups.Count} farklı hatadan ilk "
                  + $"{shownGroups.Count}'i — gerisi prompt limiti nedeniyle çıkarıldı):"
                : $"Tespit edilen hatalar ({allGroups.Count} farklı):";

            sb.AppendLine();
            sb.AppendLine(header);

            for (var i = 0; i < shownGroups.Count; i++)
            {
                var g = shownGroups[i];
                var f = g.Representative;

                var label = g.Names.Count > 0 ? string.Join(", ", g.Names) : f.Kind.ToString();
                var location = f.FilePath is not null
                    ? $" ({f.FilePath}{(f.LineNumber is int ln ? $":{ln}" : "")})"
                    : "";
                // Tekrar sayısı LLM için sinyal: 5 job'da aynı hata = ortam değil kod sorunu.
                var repeat = g.Occurrences > 1
                    ? $" [aynı hata {g.Occurrences} kez — job'lar: {string.Join(", ", g.JobNames)}]"
                    : "";

                sb.AppendLine();
                sb.AppendLine($"{i + 1}) {label}{location}{repeat}");
                sb.AppendLine($"   Tip: {f.Kind}, Job: {f.JobName}, Adım: {f.StepName}");
                sb.AppendLine($"   {Masker.Mask(f.Message)}");
            }
            sb.AppendLine();
        }

        if (ctx.FilteredAnnotations.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Checks API annotation'ları:");
            foreach (var a in ctx.FilteredAnnotations)
                sb.AppendLine($"- {Masker.Mask(a)}");
        }

        // Tüm hataların dosya:satır konumu zaten kesinse (AllFailuresLocated),
        // ham log ekstra bilgi katmıyor - yukarıdaki hata listesinin tekrarı oluyor.
        // Konum belirsizse (ör. build-cs1002'deki gibi parser'ın telafi
        // edemediği durumlar), LLM'in ham veriden çıkarım yapabilmesi için TAM
        // hâliyle gönderiliyor - kırpma yok. Sığmazsa FitPrompt bu bloğu komple
        // çıkaran bir alt kademeye geçiyor (kör char-kesme yerine).
        if (budget.IncludeRawLog && !string.IsNullOrWhiteSpace(ctx.RawStepLog) && !ctx.AllFailuresLocated)
        {
            sb.AppendLine();
            sb.AppendLine("Ham log kesiti:");
            sb.AppendLine("```");
            sb.AppendLine(Masker.Mask(ctx.RawStepLog));
            sb.AppendLine("```");
        }

        // Kod kesitleri Masker.Mask'tan bilinçli olarak GEÇİRİLMİYOR: bu kaynak kod,
        // log değil. Maskeleme kuralları (email, token regex'leri) kaynak kodda yanlış
        // pozitif üretebilir (örn. bir değişken adında "password" geçen kod satırı bozulur).
        //
        // Grup temsilcisinin kesiti yeterli: aynı gruptaki failure'lar zaten aynı
        // dosya:satır'da, kesitleri de birebir aynı olurdu.
        if (budget.IncludeCodeSnippets)
        {
            foreach (var f in shownGroups.Select(g => g.Representative)
                                         .Where(f => !string.IsNullOrWhiteSpace(f.CodeSnippet)))
            {
                var label = f.Name is not null ? $"{f.Name} — " : "";
                sb.AppendLine();
                sb.AppendLine($"İlgili kod ({label}{f.FilePath}:{f.LineNumber} civarı, >> işaretli satır hatanın olduğu satır):");
                sb.AppendLine("```");
                sb.AppendLine(f.CodeSnippet);
                sb.AppendLine("```");
            }
        }

        return sb.ToString();
    }

}
