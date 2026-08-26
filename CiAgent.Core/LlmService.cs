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
        Sen bir CI/CD hata analiz asistanısın. Sana bir GitHub Actions job'ının
        başarısız olan adımına ait log kesiti ve metadata verilecek.

        Kurallar:
        - Sadece verilen veriye dayan, tahmin uydurma.
        - Log yetersizse confidence alanını "low" yap ve bunu rootCause'da belirt.
        - suggestedFix somut ve uyanabilir olsun (hangi dosyada ne değişecek).
        - Kod kesiti verilmişse (>> işaretli satır), suggestedFix'te bu satıra somut ve
          uygulanabilir bir değişiklik öner, genel tavsiye verme.
        - Türkçe cevap ver.
        - Yanıtı yalnızca istenen JSON şemasında döndür.
        """;

    private const string JsonSchema = """
        {
          "type": "object",
          "properties": {
            "summary":      { "type": "string" },
            "rootCause":    { "type": "string" },
            "suggestedFix": { "type": "string" },
            "confidence":   { "type": "string", "enum": ["high", "medium", "low"] },
            "affectedFile": { "type": ["string", "null"] },
            "affectedLine": { "type": ["integer", "null"] }
          },
          "required": ["summary", "rootCause", "suggestedFix", "confidence", "affectedFile", "affectedLine"],
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

    public async Task<AnalysisResult?> AnalyzeAsync(ErrorContext context)
    {
        // Prompt BİR KEZ üretilip ölçülüyor; eşik aşılırsa Azure OpenAI'a hiç
        // istek atılmıyor. Dönen sonuç null değil "atlandı" durumu - null olsaydı
        // Program.cs erken return edip raporu hiç atmazdı, yani durum sessizce
        // kaybolurdu.
        var prompt = BuildPrompt(context);

        if (prompt.Length > MaxPromptChars)
            return AnalysisResult.ForSkipped(prompt.Length, MaxPromptChars);

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

        var json = await CompleteAsync(messages, options);

        //json to AnalysisResult type
        return JsonSerializer.Deserialize<AnalysisResult>(json);
    }

    // internal (private değil): AnalyzeAsync'in ölçtüğü uzunluk testlerden
    // doğrudan doğrulanabilsin diye (bkz. AssemblyInfo.cs -> InternalsVisibleTo).
    internal static string BuildPrompt(ErrorContext ctx)
    {
        var sb = new StringBuilder();

        sb.AppendLine($"Job adı: {ctx.JobName}");
        sb.AppendLine($"Başarısız adım: {ctx.FailedStepName}");

        if (ctx.ErrorMessage is not null)
            sb.AppendLine($"Ayrıştırılmış hata mesajı: {Masker.Mask(ctx.ErrorMessage)}");

        if (ctx.FilePath is not null)
            sb.AppendLine($"Dosya: {ctx.FilePath}:{ctx.LineNumber}");

        if (ctx.FilteredAnnotations.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Checks API annotation'ları:");
            foreach (var a in ctx.FilteredAnnotations)
                sb.AppendLine($"- {Masker.Mask(a)}");
        }

        // Tüm hataların dosya:satır konumu zaten kesinse (AllFailuresLocated),
        // ham log ekstra bilgi katmıyor - sadece ErrorMessage'ın tekrarı oluyor.
        // Konum belirsizse (ör. build-cs1002'deki gibi parser'ın telafi
        // edemediği durumlar), LLM'in ham veriden çıkarım yapabilmesi için TAM
        // hâliyle gönderiliyor - kırpma yok. Fazla büyükse AnalyzeAsync zaten
        // analizi tamamen atlıyor (MaxPromptChars).
        if (!string.IsNullOrWhiteSpace(ctx.RawStepLog) && !ctx.AllFailuresLocated)
        {
            sb.AppendLine();
            sb.AppendLine("Ham log kesiti:");
            sb.AppendLine("```");
            sb.AppendLine(Masker.Mask(ctx.RawStepLog));
            sb.AppendLine("```");
        }

        // Masker.Mask'tan bilinçli olarak GEÇİRİLMİYOR: bu kaynak kod, log değil.
        // Maskeleme kuralları (email, token regex'leri) kaynak kodda yanlış pozitif
        // üretebilir (örn. bir değişken adında "password" geçen kod satırı bozulur).
        if (!string.IsNullOrWhiteSpace(ctx.CodeSnippet))
        {
            sb.AppendLine();
            sb.AppendLine($"İlgili kod (satır {ctx.LineNumber} civarı, >> işaretli satır hatanın olduğu satır):");
            sb.AppendLine("```");
            sb.AppendLine(ctx.CodeSnippet);
            sb.AppendLine("```");
        }

        return sb.ToString();
    }

}
