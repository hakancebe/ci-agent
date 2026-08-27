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
                    Message = "Assert.Equal() Failure: Expected 5, Actual 4"
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
}
