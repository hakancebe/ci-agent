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

    private const string ValidJson = """
        {
          "summary": "Test başarısız oldu",
          "rootCause": "Beklenen değer farklı",
          "suggestedFix": "Calculator.Add metodunu düzelt",
          "confidence": "high",
          "affectedFile": "src/Calculator.cs",
          "affectedLine": 42
        }
        """;

    private static ErrorContext Context(string? rawStepLog = null, bool allLocated = false) =>
        new()
        {
            JobName = "build-test",
            FailedStepName = "Test",
            ErrorMessage = "Assert.Equal() Failure: Expected 5, Actual 4",
            RawStepLog = rawStepLog,
            AllFailuresLocated = allLocated
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
        Assert.Equal("high", result.Confidence);
    }

    [Fact]
    public async Task AnalyzeAsync_SkipsWithoutCallingLlm_WhenPromptOverLimit()
    {
        var llm = new FakeLlmService(ValidJson);
        // ~400 farklı stack trace satırı -> prompt 50.000'i aşıyor (ölçülen kırılma
        // noktası ~325 satır civarı).
        var context = Context(RawLog(400));

        var promptLength = LlmService.BuildPrompt(context).Length;
        Assert.True(promptLength > 50_000, $"kurgu bozuk: prompt {promptLength} kr, limitin altında");

        var result = await llm.AnalyzeAsync(context);

        // Asıl iddia: Azure OpenAI'a HİÇ gidilmedi.
        Assert.Equal(0, llm.CallCount);

        Assert.NotNull(result);
        Assert.True(result!.Skipped);
        Assert.Equal("low", result.Confidence);
        Assert.NotNull(result.SkipReason);
        Assert.Contains(promptLength.ToString("N0"), result.SkipReason);
        Assert.Contains(50_000.ToString("N0"), result.SkipReason);
        Assert.Contains("otomatik analiz limiti aştığı için yapılmadı", result.SkipReason);
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
}
