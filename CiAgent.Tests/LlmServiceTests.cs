using System.ClientModel;
using System.ClientModel.Primitives;
using System.Text;
using CiAgent.Core;
using OpenAI.Chat;

namespace CiAgent.Tests;

public class LlmServiceTests
{
    /// <summary>
    /// Gerçek ChatClient'ı hiç kurmayan test double'ı: LlmService'in internal test
    /// constructor'ını kullanır ve transport seam'ini (CompleteAsync) override eder.
    /// Böylece Azure OpenAI'a HİÇBİR istek çıkmaz ve çağrının gerçekten yapılıp
    /// yapılmadığı sayılabilir.
    /// </summary>
    private sealed class FakeLlmService : LlmService
    {
        private readonly string _json;
        public int CallCount { get; private set; }

        public FakeLlmService(string json) => _json = json;

        internal override Task<string> CompleteAsync(
            List<ChatMessage> messages, ChatCompletionOptions options)
        {
            CallCount++;
            return Task.FromResult(_json);
        }
    }

    /// <summary>
    /// Belirtilen sayıda kez hata fırlatıp sonra başarılı olan double. Gerçek bekleme
    /// yapmaz (DelayAsync override) - retry testleri anında koşar.
    /// </summary>
    private sealed class FlakyLlmService : LlmService
    {
        private readonly Queue<Exception> _failures;
        private readonly string _json;

        public int CallCount { get; private set; }
        public List<TimeSpan> Delays { get; } = new();

        public FlakyLlmService(string json, params Exception[] failures)
        {
            _json = json;
            _failures = new Queue<Exception>(failures);
        }

        internal override Task<string> CompleteAsync(
            List<ChatMessage> messages, ChatCompletionOptions options)
        {
            CallCount++;
            if (_failures.Count > 0)
                throw _failures.Dequeue();

            return Task.FromResult(_json);
        }

        internal override Task DelayAsync(TimeSpan duration)
        {
            Delays.Add(duration);
            return Task.CompletedTask;
        }
    }

    private static ClientResultException HttpStatus(int status) =>
        new(new FakeResponse(status));

    /// <summary>ClientResultException'ın Status'unu doldurabilmek için minimum PipelineResponse.</summary>
    private sealed class FakeResponse : PipelineResponse
    {
        public FakeResponse(int status) => Status = status;

        public override int Status { get; }
        public override string ReasonPhrase => "simulated";
        public override Stream? ContentStream { get; set; }
        public override BinaryData Content => BinaryData.FromString("");
        protected override PipelineResponseHeaders HeadersCore { get; } = new FakeHeaders();
        public override BinaryData BufferContent(CancellationToken ct = default) => Content;
        public override ValueTask<BinaryData> BufferContentAsync(CancellationToken ct = default)
            => ValueTask.FromResult(Content);
        public override void Dispose() { }

        private sealed class FakeHeaders : PipelineResponseHeaders
        {
            public override IEnumerator<KeyValuePair<string, string>> GetEnumerator()
                => Enumerable.Empty<KeyValuePair<string, string>>().GetEnumerator();
            public override bool TryGetValue(string name, out string? value) { value = null; return false; }
            public override bool TryGetValues(string name, out IEnumerable<string>? values) { values = null; return false; }
        }
    }

    private const string ValidJson = """
        {
          "summary": "Test başarısız oldu",
          "analyses": [
            {
              "title": "Calculator.Add yanlış operatör kullanıyor",
              "rootCause": "Beklenen değer farklı",
              "suggestedFix": "Calculator.Add metodunu düzelt",
              "confidence": "high",
              "affectedFile": "src/Calculator.cs",
              "affectedLine": 42
            }
          ]
        }
        """;

    // İki bağımsız kök neden dönen yanıt - LLM'in gruplamayı reddettiği durum.
    private const string TwoAnalysesJson = """
        {
          "summary": "İki bağımsız sorun var",
          "analyses": [
            {
              "title": "Eksik NuGet paketi",
              "rootCause": "Bu.Paket.Yok bulunamadı",
              "suggestedFix": "Paketi nuget.org'a ekle veya referansı kaldır",
              "confidence": "high",
              "affectedFile": "src/CiPilot.Core.csproj",
              "affectedLine": null
            },
            {
              "title": "Calculator.Add hatalı",
              "rootCause": "Toplama yerine çıkarma yapılıyor",
              "suggestedFix": "return a + b olarak düzelt",
              "confidence": "medium",
              "affectedFile": "src/Calculator.cs",
              "affectedLine": 42
            }
          ]
        }
        """;

    // AllFailuresLocated artık Failures'tan türetiliyor, doğrudan set edilemiyor -
    // allLocated:true istendiğinde failure'a dosya:satır veriyoruz.
    private static ErrorContext Context(string? rawStepLog = null, bool allLocated = false) =>
        new()
        {
            JobName = "build-test",
            FailedStepName = "Test",
            RawStepLog = rawStepLog,
            Failures =
            {
                new Failure
                {
                    Kind = FailureKind.Test,
                    Name = "CalculatorTests.Add",
                    JobName = "build-test",
                    StepName = "Test",
                    FilePath = allLocated ? "src/Calculator.cs" : null,
                    LineNumber = allLocated ? 42 : null,
                    Message = "Assert.Equal() Failure: Expected 5, Actual 4",

                    // Konum ayrıştırılamamış olsa bile kanıt metninde duruyor —
                    // gerçek bir xUnit hatası da böyle görünüyor. StripUnfoundedLines
                    // satır numarasını ancak modele GÖSTERİLEN bir yerde bulursa
                    // koruyor; dayanaksız bir fixture guard'ı test etmez, sadece
                    // guard'ın kendisini kırar.
                    RawEvidence = allLocated
                        ? null
                        : "   at CalculatorTests.Add() in /home/runner/work/p/p/src/Calculator.cs:line 42"
                }
            }
        };

    private static ErrorContext ContextWithCodeSnippet(string codeSnippet) =>
        new()
        {
            JobName = "build-test",
            FailedStepName = "Test",
            Failures =
            {
                new Failure
                {
                    Kind = FailureKind.Test,
                    Name = "CalculatorTests.Add",
                    JobName = "build-test",
                    StepName = "Test",
                    FilePath = "src/Calculator.cs",
                    LineNumber = 42,
                    Message = "Assert.Equal() Failure: Expected 5, Actual 4",
                    CodeSnippet = codeSnippet
                }
            }
        };

    // Her satırı farklı, boşluklu (sanitizer'ın eleyemeyeceği "gerçek" içerik) ham log.
    private static string RawLog(int lineCount)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < lineCount; i++)
            sb.AppendLine(
                $"   at CiPilot.Core.Services.Module{i}.Handler{i}.ValidateAsync(Request r{i}, "
                + $"CancellationToken ct) in /home/runner/work/src/Module{i}/Handler{i}.cs:line {400 + i}");
        return sb.ToString();
    }

    [Fact]
    public async Task AnalyzeAsync_CallsLlmAndReturnsResult_WhenPromptUnderLimit()
    {
        var llm = new FakeLlmService(ValidJson);
        var context = Context(RawLog(50));

        // Varsayımı doğrula: bu prompt gerçekten limitin altında olmalı.
        Assert.True(LlmService.BuildPrompt(context).Length < 50_000);

        var result = await llm.AnalyzeAsync(context);

        Assert.Equal(1, llm.CallCount);
        Assert.NotNull(result);
        Assert.False(result!.Skipped);
        Assert.Null(result.SkipReason);
        Assert.Equal("Test başarısız oldu", result.Summary);

        var analysis = Assert.Single(result.Analyses);
        Assert.Equal("high", analysis.Confidence);
        Assert.Equal("Beklenen değer farklı", analysis.RootCause);
        Assert.Equal("src/Calculator.cs", analysis.AffectedFile);
        Assert.Equal(42, analysis.AffectedLine);
    }

    [Fact]
    public async Task AnalyzeAsync_PreservesAllAnalyses_WhenLlmReportsIndependentRootCauses()
    {
        var llm = new FakeLlmService(TwoAnalysesJson);

        var result = await llm.AnalyzeAsync(Context());

        Assert.NotNull(result);
        Assert.Equal(2, result!.Analyses.Count);
        Assert.Equal("Eksik NuGet paketi", result.Analyses[0].Title);
        Assert.Null(result.Analyses[0].AffectedLine);
        Assert.Equal("Calculator.Add hatalı", result.Analyses[1].Title);
        Assert.Equal(42, result.Analyses[1].AffectedLine);
    }

    [Fact]
    public async Task AnalyzeAsync_DropsRawLogAndStillAnalyzes_WhenFullPromptOverLimit()
    {
        var llm = new FakeLlmService(ValidJson);
        // ~400 farklı stack trace satırı -> tam prompt 50.000'i aşıyor (ölçülen
        // kırılma noktası ~325 satır civarı).
        var context = Context(RawLog(400));

        var fullLength = LlmService.BuildPrompt(context).Length;
        Assert.True(fullLength > 50_000, $"kurgu bozuk: tam prompt {fullLength} kr, limitin altında");

        var result = await llm.AnalyzeAsync(context);

        // Eskiden burada analiz tamamen atlanırdı. Artık ham log çıkarılıp analiz
        // yapılıyor - Azure OpenAI'a gidiliyor ve gerçek bir sonuç dönüyor.
        Assert.Equal(1, llm.CallCount);
        Assert.NotNull(result);
        Assert.False(result!.Skipped);
        Assert.Equal("Test başarısız oldu", result.Summary);

        // Neyin feda edildiği kaybolmamalı.
        Assert.NotNull(result.ReductionNote);
        Assert.Contains("ham log kesiti", result.ReductionNote);
        Assert.DoesNotContain("kod kesitleri", result.ReductionNote);
    }

    [Fact]
    public async Task AnalyzeAsync_SetsNoReductionNote_WhenFullPromptFits()
    {
        var llm = new FakeLlmService(ValidJson);

        var result = await llm.AnalyzeAsync(Context(RawLog(50)));

        Assert.Equal(1, llm.CallCount);
        Assert.NotNull(result);
        Assert.Null(result!.ReductionNote);
    }

    [Fact]
    public async Task AnalyzeAsync_SkipsOnlyWhenNothingFits_EvenAfterFullDegradation()
    {
        var llm = new FakeLlmService(ValidJson);
        // Ayrıştırılmış hata mesajının KENDİSİ limitin üstünde (patolojik ama mümkün:
        // devasa bir assert diff'i). Hiçbir kademe bunu kurtaramaz.
        var hugeMessage = new string('x', 60_000);
        var context = new ErrorContext
        {
            JobName = "build-test",
            FailedStepName = "Test",
            Failures =
            {
                new Failure { Kind = FailureKind.Test, Name = "Huge", Message = hugeMessage }
            }
        };

        var result = await llm.AnalyzeAsync(context);

        // Asıl iddia: Azure OpenAI'a HİÇ gidilmedi.
        Assert.Equal(0, llm.CallCount);
        Assert.NotNull(result);
        Assert.True(result!.Skipped);
        Assert.Contains("otomatik analiz limiti aştığı için yapılmadı", result.SkipReason);
        // Uydurma bir kök neden gösterilmemeli - analiz gerçekten yapılmadı.
        Assert.Empty(result.Analyses);
    }

    // --- Retry / geçici hata dayanıklılığı ------------------------------

    [Fact]
    public async Task AnalyzeAsync_RetriesAndSucceeds_WhenRateLimited()
    {
        // CI'da paralel job'lar aynı anda Azure OpenAI'a vurduğunda 429 sıradan bir olay.
        var llm = new FlakyLlmService(ValidJson, HttpStatus(429), HttpStatus(429));

        var result = await llm.AnalyzeAsync(Context());

        Assert.Equal(3, llm.CallCount);           // 2 başarısız + 1 başarılı
        Assert.NotNull(result);
        Assert.Equal("Test başarısız oldu", result!.Summary);
        // Üstel geri çekilme: 2sn, sonra 4sn.
        Assert.Equal(new[] { TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4) }, llm.Delays);
    }

    [Fact]
    public async Task AnalyzeAsync_GivesUpAfterMaxAttempts_WhenTransientErrorPersists()
    {
        var llm = new FlakyLlmService(ValidJson, HttpStatus(503), HttpStatus(503), HttpStatus(503));

        var ex = await Assert.ThrowsAsync<ClientResultException>(() => llm.AnalyzeAsync(Context()));

        Assert.Equal(503, ex.Status);
        Assert.Equal(3, llm.CallCount);   // sonsuza kadar denemiyor
    }

    [Fact]
    public async Task AnalyzeAsync_DoesNotRetry_OnPermanentError()
    {
        // 401 (yanlış key) / 404 (yanlış deployment adı) beklemekle düzelmez -
        // tekrar denemek sadece CI süresini uzatır.
        var llm = new FlakyLlmService(ValidJson, HttpStatus(401));

        await Assert.ThrowsAsync<ClientResultException>(() => llm.AnalyzeAsync(Context()));

        Assert.Equal(1, llm.CallCount);
        Assert.Empty(llm.Delays);
    }

    [Theory]
    [InlineData(408, true)]
    [InlineData(429, true)]
    [InlineData(500, true)]
    [InlineData(503, true)]
    [InlineData(400, false)]
    [InlineData(401, false)]
    [InlineData(404, false)]
    public void IsTransient_ClassifiesHttpStatusCodes(int status, bool expected)
    {
        Assert.Equal(expected, LlmService.IsTransient(HttpStatus(status)));
    }

    [Fact]
    public void IsTransient_TreatsNetworkFailuresAsRetryable()
    {
        Assert.True(LlmService.IsTransient(new HttpRequestException("bağlantı koptu")));
        Assert.True(LlmService.IsTransient(new TaskCanceledException("timeout")));
        Assert.False(LlmService.IsTransient(new InvalidOperationException("kod hatası")));
    }

    [Fact]
    public void FitPrompt_TruncatesFailureCount_AsLastResort()
    {
        // 20 hata × ~5.000 karakter mesaj: ham log ve kod kesiti olmasa bile
        // hepsi birden sığmıyor, kademe merdiveninin son basamağı devreye giriyor.
        var failures = Enumerable.Range(0, 20)
            .Select(i => new Failure
            {
                Kind = FailureKind.Test,
                Name = $"Test{i}",
                FilePath = $"src/File{i}.cs",
                LineNumber = i + 1,
                Message = new string('m', 5_000)
            })
            .ToList();

        var context = new ErrorContext
        {
            JobName = "build-test",
            FailedStepName = "Test",
            Failures = failures
        };

        var (prompt, budget) = LlmService.FitPrompt(context);

        Assert.NotNull(prompt);
        Assert.True(prompt!.Length <= 50_000);
        Assert.False(budget.IncludeRawLog);
        Assert.False(budget.IncludeCodeSnippets);
        Assert.NotNull(budget.MaxFailures);
        Assert.InRange(budget.MaxFailures!.Value, 1, 19);

        // Kaç hatanın gösterildiği ve kaçının çıkarıldığı prompt'ta açıkça yazmalı -
        // LLM "gördüğüm hepsi bu" sanmasın.
        Assert.Contains($"toplam 20 farklı hatadan ilk {budget.MaxFailures}'i", prompt);
        Assert.Contains("20 farklı hatadan yalnızca ilk", budget.Describe(context));
    }

    [Fact]
    public void FitPrompt_DropsCodeSnippetsBeforeTruncatingFailures()
    {
        // Kod kesitleri şişkin ama mesajlar küçük: kesitler atılınca sığmalı,
        // hata sayısına dokunulmamalı.
        var failures = Enumerable.Range(0, 30)
            .Select(i => new Failure
            {
                Kind = FailureKind.Test,
                Name = $"Test{i}",
                FilePath = $"src/File{i}.cs",
                LineNumber = i + 1,
                Message = "kısa mesaj",
                CodeSnippet = new string('c', 3_000)
            })
            .ToList();

        var context = new ErrorContext
        {
            JobName = "build-test",
            FailedStepName = "Test",
            Failures = failures
        };

        var (prompt, budget) = LlmService.FitPrompt(context);

        Assert.NotNull(prompt);
        Assert.False(budget.IncludeCodeSnippets);
        Assert.Null(budget.MaxFailures);   // hata sayısı korundu
        Assert.DoesNotContain("cccc", prompt);
    }

    [Fact]
    public void BuildPrompt_IncludesRawLogUntrimmed_WhenLocationUnknown()
    {
        // TrimLog kaldırıldı: ham log artık kesilmeden prompt'a giriyor.
        var raw = RawLog(200);
        var prompt = LlmService.BuildPrompt(Context(raw, allLocated: false));

        Assert.Contains("Ham log kesiti:", prompt);
        Assert.DoesNotContain("karakter kırpıldı] ...", prompt);
        // İlk ve SON satır birden içeride olmalı - head+tail kesme olsaydı
        // ortadaki satırlar kaybolurdu.
        Assert.Contains("Module0.Handler0", prompt);
        Assert.Contains("Module199.Handler199", prompt);
        Assert.Contains("Module100.Handler100", prompt);
    }

    [Fact]
    public void BuildPrompt_OmitsRawLog_WhenAllFailuresLocated()
    {
        var prompt = LlmService.BuildPrompt(Context(RawLog(200), allLocated: true));

        Assert.DoesNotContain("Ham log kesiti:", prompt);
        Assert.Contains("Assert.Equal() Failure", prompt);
    }

    [Fact]
    public void BuildPrompt_IncludesCodeSnippet_WhenPresent()
    {
        var snippet = "40: public int Add(int a, int b)\n>> 41: {\n42:     return a - b;\n43: }";
        var prompt = LlmService.BuildPrompt(ContextWithCodeSnippet(snippet));

        Assert.Contains(
            "İlgili kod (CalculatorTests.Add — src/Calculator.cs:42 civarı, >> işaretli satır hatanın olduğu satır):",
            prompt);
        Assert.Contains(snippet, prompt);
    }

    [Fact]
    public void BuildPrompt_OmitsCodeSnippetSection_WhenCodeSnippetIsNull()
    {
        var prompt = LlmService.BuildPrompt(Context());

        Assert.DoesNotContain("İlgili kod (satır", prompt);
    }

    // --- Workflow dosyası ------------------------------------------------
    // Canlı ölçümde model, deploy hatasında ".github/workflows/deploy.yml"
    // dedi; repoda öyle bir dosya yoktu (gerçeği cd.yml). Adı logdan çıkarmak
    // MÜMKÜN DEĞİL, o yüzden uydurdu. Bu testler doğru adın prompt'a
    // girdiğini sabitliyor.

    [Fact]
    public void BuildPrompt_NamesTheWorkflowFile_WhenKnown()
    {
        var ctx = Context();
        ctx.Workflow = new WorkflowInfo("CD", ".github/workflows/cd.yml");

        var prompt = LlmService.BuildPrompt(ctx);

        Assert.Contains(".github/workflows/cd.yml", prompt);
        Assert.Contains("workflow adı: CD", prompt);
    }

    [Fact]
    public void BuildPrompt_OmitsWorkflowLine_WhenUnknown()
    {
        // Workflow bilgisi alınamadıysa (API hatası) prompt'a boş/yanıltıcı
        // bir satır girmemeli — model "verilmediyse null bırak" kuralını
        // ancak satır hiç yoksa uygulayabilir.
        var prompt = LlmService.BuildPrompt(Context());

        Assert.DoesNotContain("Bu run'ı tanımlayan workflow dosyası", prompt);
    }

    [Fact]
    public void BuildPrompt_OmitsWorkflowLine_WhenPathIsEmpty()
    {
        var ctx = Context();
        ctx.Workflow = new WorkflowInfo("CD", "");

        var prompt = LlmService.BuildPrompt(ctx);

        Assert.DoesNotContain("Bu run'ı tanımlayan workflow dosyası", prompt);
    }

    [Fact]
    public void BuildPrompt_StillNamesTheFile_WhenWorkflowNameMissing()
    {
        var ctx = Context();
        ctx.Workflow = new WorkflowInfo(null, ".github/workflows/cd.yml");

        var prompt = LlmService.BuildPrompt(ctx);

        Assert.Contains(".github/workflows/cd.yml", prompt);
        Assert.DoesNotContain("workflow adı:", prompt);
    }

    [Fact]
    public void BuildPrompt_RendersPerFailureSnippets_WhenFailuresListPopulated()
    {
        var context = new ErrorContext
        {
            JobName = "build-test",
            FailedStepName = "Test",
            Failures =
            {
                new Failure
                {
                    Kind = FailureKind.Test, Name = "CalcTests.Add", FilePath = "src/Calc.cs", LineNumber = 12,
                    Message = "Values differ", CodeSnippet = "11: int Add(...)\n>> 12: return a - b;"
                },
                new Failure
                {
                    Kind = FailureKind.Test, Name = "CalcTests.Sub", FilePath = "src/Calc.cs", LineNumber = 20,
                    Message = "Values differ", CodeSnippet = "19: int Sub(...)\n>> 20: return a + b;"
                }
            }
        };

        var prompt = LlmService.BuildPrompt(context);

        Assert.Contains("CalcTests.Add — src/Calc.cs:12 civarı", prompt);
        Assert.Contains("return a - b;", prompt);
        Assert.Contains("CalcTests.Sub — src/Calc.cs:20 civarı", prompt);
        Assert.Contains("return a + b;", prompt);
    }

    [Fact]
    public void BuildPrompt_DoesNotMaskCodeSnippet_UnlikeRawLogAndErrorMessage()
    {
        // Kaynak kodda "password" gibi bir kelime geçen bir değişken adı Masker'dan
        // geçseydi "password=***" hâline gelip kodu bozardı - CodeSnippet bilinçli
        // olarak Masker.Mask'tan geçirilmiyor.
        var snippet = ">> 10:     var password = ComputeHash(rawPassword);";
        var prompt = LlmService.BuildPrompt(ContextWithCodeSnippet(snippet));

        Assert.Contains(snippet, prompt);
        Assert.DoesNotContain("password=***", prompt);
    }
    // --- Dayanaksız satır numarası ----------------------------------------
    // Canlıda ölçüldü (pilot CD run 34132375477): gerçek bir Azure kimlik
    // hatasında teşhis ve dosya DOĞRUYDU ama ".github/workflows/cd.yml:66"
    // denildi; 66. satır bir yorum satırıydı ve logda o dosyanın hiçbir satır
    // numarası geçmiyordu. İnsan bağlantıya tıklayıp alakasız bir satıra
    // düşüyor ve sayının uydurma olduğunu anlamıyor.

    private static AnalysisResult ResultWith(string? file, int? line) =>
        new()
        {
            Summary = "özet",
            Analyses =
            {
                new Analysis
                {
                    Title = "t", RootCause = "r", SuggestedFix = "f",
                    Confidence = "high", AffectedFile = file, AffectedLine = line
                }
            }
        };

    private static ErrorContext ContextWithFailure(
        string? path, int? line, string? snippet = null) =>
        new()
        {
            JobName = "deploy",
            FailedStepName = "Adım",
            Failures =
            {
                new Failure
                {
                    Kind = FailureKind.Generic,
                    JobName = "deploy",
                    StepName = "Adım",
                    FilePath = path,
                    LineNumber = line,
                    Message = "Process completed with exit code 1.",
                    CodeSnippet = snippet
                }
            }
        };

    [Fact]
    public void StripUnfoundedLines_DropsLine_WhenNothingInTheContextSupportsIt()
    {
        // Ölçülen vaka: workflow dosyası, hiçbir satır bilgisi yok.
        var result = ResultWith(".github/workflows/cd.yml", 66);
        var context = ContextWithFailure(path: null, line: null);

        LlmService.StripUnfoundedLines(result, context);

        var analysis = Assert.Single(result.Analyses);
        Assert.Null(analysis.AffectedLine);
        // Dosya adı korunuyor: o kısım doğruydu, atılmamalı.
        Assert.Equal(".github/workflows/cd.yml", analysis.AffectedFile);
    }

    [Fact]
    public void StripUnfoundedLines_KeepsLine_WhenTheParserFoundTheSameLine()
    {
        // Derleyici hatası / stack trace: satır ayrıştırıcıdan geliyor.
        var result = ResultWith("src/Calculator.cs", 42);
        var context = ContextWithFailure("src/Calculator.cs", 42);

        LlmService.StripUnfoundedLines(result, context);

        Assert.Equal(42, Assert.Single(result.Analyses).AffectedLine);
    }

    [Fact]
    public void StripUnfoundedLines_KeepsLine_WhenANumberedSnippetWasShown()
    {
        // Kesit satır numaralı gösteriliyor; model komşu bir satırı da
        // gösterebilir ve bu meşru.
        var result = ResultWith("src/Calculator.cs", 41);
        var context = ContextWithFailure(
            "src/Calculator.cs", 42, snippet: "41: {\n>> 42:     return a - b;");

        LlmService.StripUnfoundedLines(result, context);

        Assert.Equal(41, Assert.Single(result.Analyses).AffectedLine);
    }

    [Fact]
    public void StripUnfoundedLines_KeepsLine_WhenTheWholeFileWasShown()
    {
        var result = ResultWith("src/Calculator.cs", 7);
        var context = ContextWithFailure(path: null, line: null);
        context.RelatedSources["src/Calculator.cs"] = "class Calculator { }";

        LlmService.StripUnfoundedLines(result, context);

        Assert.Equal(7, Assert.Single(result.Analyses).AffectedLine);
    }

    [Fact]
    public void StripUnfoundedLines_ToleratesPathStyleDifferences()
    {
        // Model bazen "./src/x.cs" yazıyor; amaç sayıyı doğrulamak, yolu değil.
        var result = ResultWith("./src/Calculator.cs", 42);
        var context = ContextWithFailure("src/Calculator.cs", 42);

        LlmService.StripUnfoundedLines(result, context);

        Assert.Equal(42, Assert.Single(result.Analyses).AffectedLine);
    }

    // --- Ham metinden doğrulama --------------------------------------------
    // Guard'ın en kritik kaynağı bu. Olmasa doğru sayıları da atardı:
    // .NET dışı dillerde ayrıştırıcı konumu çözemiyor, sayı yalnızca ham
    // logda duruyor (ölçüldü, pilot CI run 34132572126 — Node/TAP).

    [Fact]
    public void StripUnfoundedLines_KeepsLine_WhenOnlyTheRawLogCarriesIt()
    {
        var result = ResultWith("node-app/calculator.test.js", 6);
        var context = ContextWithFailure(path: null, line: null);
        context.RawStepLog =
            "not ok 1 - add iki sayiyi toplar\n"
            + "  at Object.<anonymous> (/home/runner/work/p/p/node-app/calculator.test.js:6:10)";

        LlmService.StripUnfoundedLines(result, context);

        Assert.Equal(6, Assert.Single(result.Analyses).AffectedLine);
    }

    [Fact]
    public void StripUnfoundedLines_KeepsLine_WhenAnAnnotationCarriesIt()
    {
        var result = ResultWith("src/Calculator.cs", 42);
        var context = ContextWithFailure(path: null, line: null);
        context.FilteredAnnotations.Add("src/Calculator.cs(42,5): error CS0029: ...");

        LlmService.StripUnfoundedLines(result, context);

        Assert.Equal(42, Assert.Single(result.Analyses).AffectedLine);
    }

    [Fact]
    public void StripUnfoundedLines_DropsLine_WhenTheRawLogMentionsADifferentFile()
    {
        // Sayı logda geçiyor ama BAŞKA bir dosyanın yanında. Model iki bilgiyi
        // birleştirmiş olabilir; bu dayanak değil.
        var result = ResultWith("src/Calculator.cs", 42);
        var context = ContextWithFailure(path: null, line: null);
        context.RawStepLog = "   at Other.Run() in /home/runner/work/p/p/src/Other.cs:line 42";

        LlmService.StripUnfoundedLines(result, context);

        Assert.Null(Assert.Single(result.Analyses).AffectedLine);
    }

    [Theory]
    [InlineData(420)]  // "…:line 42" 420'yi doğrulamaz
    [InlineData(4)]    // 42'nin ilk hanesi de dayanak değil
    public void StripUnfoundedLines_DropsLine_WhenTheNumberOnlyOverlapsAnotherNumber(int claimed)
    {
        var result = ResultWith("src/Calculator.cs", claimed);
        var context = ContextWithFailure(path: null, line: null);
        context.RawStepLog = "   at X.Y() in /home/runner/work/p/p/src/Calculator.cs:line 42";

        LlmService.StripUnfoundedLines(result, context);

        Assert.Null(Assert.Single(result.Analyses).AffectedLine);
    }

    [Fact]
    public void StripUnfoundedLines_DropsLine_WhenTheNumberIsTooFarFromTheFileName()
    {
        // Aynı satırda geçiyor olması yetmez; dosya adıyla sayı arasında
        // sayfalarca metin varsa bu tesadüf olabilir.
        var result = ResultWith("src/Calculator.cs", 42);
        var context = ContextWithFailure(path: null, line: null);
        context.RawStepLog = "src/Calculator.cs derlendi, sonra tamamen ilgisiz bir yerde 42 geçti";

        LlmService.StripUnfoundedLines(result, context);

        Assert.Null(Assert.Single(result.Analyses).AffectedLine);
    }

    // --- Workflow dosyası gösterildiğinde ----------------------------------
    // Guard tek başına yetmedi. Canlıda ölçüldü (pilot CD run 34194933554):
    // yapılandırılmış alan temizlendi ama model bu kez düz metne kaçtı —
    // "Workflow dosyasının 66. satırındaki tenant parametresini kontrol edin".
    // 66. satır yine bir yorum satırıydı. Guard düz metni denetleyemez, o yüzden
    // asıl çözüm dosyayı GÖSTERMEK: model artık sayıyı okuyor, uydurmuyor.

    private static ErrorContext ContextWithWorkflowFile(string content) =>
        new()
        {
            JobName = "deploy",
            FailedStepName = "Azure'a giriş yap",
            Workflow = new WorkflowInfo("CD", ".github/workflows/cd.yml"),
            WorkflowFileContent = content,
            Failures =
            {
                new Failure
                {
                    Kind = FailureKind.Generic,
                    JobName = "deploy",
                    StepName = "Azure'a giriş yap",
                    Message = "Process completed with exit code 1."
                }
            }
        };

    [Fact]
    public void StripUnfoundedLines_KeepsLine_WhenTheWorkflowFileWasShown()
    {
        var result = ResultWith(".github/workflows/cd.yml", 3);
        var context = ContextWithWorkflowFile("name: CD\non:\n  push:\njobs:\n  deploy:\n");

        LlmService.StripUnfoundedLines(result, context);

        Assert.Equal(3, Assert.Single(result.Analyses).AffectedLine);
    }

    [Fact]
    public void StripUnfoundedLines_DropsLine_WhenItIsPastTheEndOfTheWorkflowFile()
    {
        // Dosya gösterilmiş olması her sayıyı meşru kılmaz; 5 satırlık bir
        // dosyada 66. satır yok.
        var result = ResultWith(".github/workflows/cd.yml", 66);
        var context = ContextWithWorkflowFile("name: CD\non:\n  push:\njobs:\n  deploy:\n");

        LlmService.StripUnfoundedLines(result, context);

        Assert.Null(Assert.Single(result.Analyses).AffectedLine);
    }

    [Fact]
    public void BuildPrompt_ShowsWorkflowFileWithLineNumbers()
    {
        // Numarasız verilseydi model satırları saymak zorunda kalırdı.
        var context = ContextWithWorkflowFile("name: CD\non:\n  push:\n");

        var prompt = LlmService.BuildPrompt(context);

        Assert.Contains(".github/workflows/cd.yml", prompt);
        Assert.Contains("   1: name: CD", prompt);
        Assert.Contains("   3:   push:", prompt);
    }

    // Ayrıştırıcı yalnızca .NET biçimlerini tanıyor. .NET dışı dillerde konum
    // SADECE ham logda duruyor, yani guard'ın tek dayanağı bu eşleştirme.
    // Her dil konumu farklı yazıyor; hangilerinin geçtiği tahmin değil ölçüm.
    [Theory]
    [InlineData("Node/Jest", "  at Object.<anonymous> (/w/node-app/calculator.test.js:6:10)", "node-app/calculator.test.js", 6)]
    [InlineData("Python", "  File \"/w/app/calculator.py\", line 42, in add", "app/calculator.py", 42)]
    [InlineData("Java", "\tat com.x.Calculator.add(Calculator.java:42)", "src/Calculator.java", 42)]
    [InlineData("Go", "    calculator_test.go:42: got 5, want 4", "calculator_test.go", 42)]
    [InlineData("Rust", "  --> src/lib.rs:42:9", "src/lib.rs", 42)]
    [InlineData("Ruby", "  /w/app/calculator.rb:42:in `add'", "app/calculator.rb", 42)]
    [InlineData("PHP/PHPUnit", "/w/src/Calculator.php:42", "src/Calculator.php", 42)]
    [InlineData("TS/tsc", "src/calc.ts(42,5): error TS2322: Type mismatch", "src/calc.ts", 42)]
    public void StripUnfoundedLines_KeepsLine_ForCommonLanguageLogFormats(
        string language, string logLine, string file, int line)
    {
        var result = ResultWith(file, line);
        var context = ContextWithFailure(path: null, line: null);
        context.RawStepLog = logLine;

        LlmService.StripUnfoundedLines(result, context);

        Assert.True(
            Assert.Single(result.Analyses).AffectedLine == line,
            $"{language} biçimindeki konum tanınmadı: {logLine}");
    }

    [Fact]
    public void BuildPrompt_OmitsWorkflowFile_WhenItWasNotFetched()
    {
        var prompt = LlmService.BuildPrompt(ContextWithFailure(path: null, line: null));

        Assert.DoesNotContain("satır numaralarıyla", prompt);
    }

}
