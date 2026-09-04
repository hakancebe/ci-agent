using System.Text.Json;
using CiAgent.Core;
using OpenAI.Chat;

namespace CiAgent.Tests;

public sealed class FixPipelineTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "ciagent-fix-" + Guid.NewGuid().ToString("N"));

    public FixPipelineTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* temizlik hatası testi düşürmesin */ }
    }

    // --- Test double'ları -------------------------------------------------

    /// <summary>Sırayla verilen JSON yanıtlarını döndüren LLM.</summary>
    private sealed class ScriptedLlm : LlmService
    {
        private readonly Queue<string> _responses;
        public int CallCount { get; private set; }
        public List<string> Prompts { get; } = new();

        public ScriptedLlm(params string[] responses) => _responses = new Queue<string>(responses);

        internal override Task<string> CompleteAsync(
            List<ChatMessage> messages, ChatCompletionOptions options)
        {
            CallCount++;
            Prompts.Add(string.Join("\n", messages.SelectMany(m => m.Content).Select(c => c.Text)));
            return Task.FromResult(_responses.Dequeue());
        }
    }

    /// <summary>Sırayla verilen doğrulama sonuçlarını döndüren runner.</summary>
    private sealed class ScriptedVerifier : IVerificationRunner
    {
        private readonly Queue<VerificationResult> _results;
        public int CallCount { get; private set; }
        public string? LastWorkingDirectory { get; private set; }

        public ScriptedVerifier(params VerificationResult[] results)
            => _results = new Queue<VerificationResult>(results);

        public Task<VerificationResult> VerifyAsync(string workingDirectory)
        {
            CallCount++;
            LastWorkingDirectory = workingDirectory;
            return Task.FromResult(_results.Dequeue());
        }
    }

    // --- Kurulum ----------------------------------------------------------

    private string WriteFile(string relativePath, string content)
    {
        var full = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return full;
    }

    private static string Proposal(string summary, params (string File, string Old, string New)[] edits)
    {
        var payload = new
        {
            summary,
            edits = edits.Select(e => new { file = e.File, oldText = e.Old, newText = e.New, reason = "test" })
        };
        return JsonSerializer.Serialize(payload);
    }

    private static ErrorContext Context(string file = "src/Calc.cs", int line = 1) => new()
    {
        JobName = "build",
        FailedStepName = "Test",
        Failures =
        {
            new Failure
            {
                Kind = FailureKind.Test, Name = "CalcTests.Add", JobName = "build", StepName = "Test",
                FilePath = file, LineNumber = line, Message = "Assert.Equal() Failure: Values differ"
            }
        }
    };

    private static AnalysisResult Analysis(string? affectedFile = "src/Calc.cs") => new()
    {
        Summary = "Toplama yerine çıkarma yapılıyor.",
        Analyses =
        {
            new Analysis
            {
                Title = "Yanlış operatör", RootCause = "a - b yazılmış",
                SuggestedFix = "a + b olarak düzelt", Confidence = "high",
                AffectedFile = affectedFile, AffectedLine = 1
            }
        }
    };

    private static readonly VerificationResult Pass = new(true, "Passed! - Failed: 0");
    private static readonly VerificationResult Fail = new(false, "Failed! - Failed: 1, Assert.Equal() Values differ");

    // --- Mutlu yol --------------------------------------------------------

    [Fact]
    public async Task RunAsync_AppliesEditAndKeepsIt_WhenVerificationPasses()
    {
        var path = WriteFile("src/Calc.cs", "int Add(int a, int b) => a - b;");
        var llm = new ScriptedLlm(Proposal("Operatör düzeltildi", ("src/Calc.cs", "a - b", "a + b")));
        var verifier = new ScriptedVerifier(Pass);

        var outcome = await new FixPipeline(llm, verifier).RunAsync(Context(), Analysis(), _root);

        Assert.Equal(FixStatus.Fixed, outcome.Status);
        Assert.Equal(1, outcome.Attempts);
        Assert.Equal("int Add(int a, int b) => a + b;", await File.ReadAllTextAsync(path));
        Assert.Equal(_root, verifier.LastWorkingDirectory);
    }

    // --- En kritik garanti: başarısız doğrulama geri alınmalı --------------

    [Fact]
    public async Task RunAsync_RevertsEverything_WhenVerificationKeepsFailing()
    {
        const string original = "int Add(int a, int b) => a - b;";
        var path = WriteFile("src/Calc.cs", original);

        var llm = new ScriptedLlm(
            Proposal("1. deneme", ("src/Calc.cs", "a - b", "a * b")),
            Proposal("2. deneme", ("src/Calc.cs", "a - b", "a / b")));
        var verifier = new ScriptedVerifier(Fail, Fail);

        var outcome = await new FixPipeline(llm, verifier).RunAsync(Context(), Analysis(), _root);

        Assert.Equal(FixStatus.VerificationFailed, outcome.Status);
        Assert.Equal(FixPipeline.MaxAttempts, outcome.Attempts);
        // Dosya el değmemiş hâline dönmeli - PR yarım düzenlenmiş kalmamalı.
        Assert.Equal(original, await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task RunAsync_RetriesWithVerificationOutput_AndSucceedsOnSecondAttempt()
    {
        var path = WriteFile("src/Calc.cs", "int Add(int a, int b) => a - b;");

        var llm = new ScriptedLlm(
            Proposal("yanlış deneme", ("src/Calc.cs", "a - b", "a * b")),
            Proposal("doğru deneme", ("src/Calc.cs", "a - b", "a + b")));
        var verifier = new ScriptedVerifier(Fail, Pass);

        var outcome = await new FixPipeline(llm, verifier).RunAsync(Context(), Analysis(), _root);

        Assert.Equal(FixStatus.Fixed, outcome.Status);
        Assert.Equal(2, outcome.Attempts);
        Assert.Equal("int Add(int a, int b) => a + b;", await File.ReadAllTextAsync(path));

        // İkinci istem, ilk denemenin neden tuttuğunu modele söylemeli.
        Assert.Contains("ÖNCEKİ DENEME BAŞARISIZ OLDU", llm.Prompts[1]);
        Assert.Contains("Assert.Equal() Values differ", llm.Prompts[1]);
    }

    // --- Reddedilen öneriler ----------------------------------------------

    [Fact]
    public async Task RunAsync_RejectsTestFileEdit_AndNeverVerifies()
    {
        // LLM testi zayıflatarak "düzeltmeye" kalkarsa daha diske ulaşmadan durmalı.
        var testPath = WriteFile("CiAgent.Tests/CalcTests.cs", "Assert.Equal(5, sum);");
        WriteFile("src/Calc.cs", "int Add(int a, int b) => a - b;");

        var llm = new ScriptedLlm(
            Proposal("testi gevşet", ("CiAgent.Tests/CalcTests.cs", "Assert.Equal(5, sum);", "Assert.True(true);")),
            Proposal("yine testi gevşet", ("CiAgent.Tests/CalcTests.cs", "Assert.Equal(5, sum);", "// kaldırıldı")));
        var verifier = new ScriptedVerifier();

        var outcome = await new FixPipeline(llm, verifier).RunAsync(Context(), Analysis(), _root);

        Assert.Equal(FixStatus.EditsRejected, outcome.Status);
        Assert.Equal(0, verifier.CallCount);   // doğrulamaya hiç geçilmedi
        Assert.Equal("Assert.Equal(5, sum);", await File.ReadAllTextAsync(testPath));
    }

    [Fact]
    public async Task RunAsync_DoesNotApplyPartially_WhenOneEditOfSeveralFails()
    {
        // Bir kısmı tutup diğeri tutmazsa modelin öngörmediği bir ara durum oluşur.
        const string original = "int Add(int a, int b) => a - b;";
        var path = WriteFile("src/Calc.cs", original);

        var llm = new ScriptedLlm(
            Proposal("iki değişiklik",
                ("src/Calc.cs", "a - b", "a + b"),
                ("src/Calc.cs", "olmayan metin", "yeni")),
            Proposal("iki değişiklik yine",
                ("src/Calc.cs", "a - b", "a + b"),
                ("src/Calc.cs", "yine olmayan", "yeni")));
        var verifier = new ScriptedVerifier();

        var outcome = await new FixPipeline(llm, verifier).RunAsync(Context(), Analysis(), _root);

        Assert.Equal(FixStatus.EditsRejected, outcome.Status);
        Assert.Equal(0, verifier.CallCount);
        Assert.Equal(original, await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task RunAsync_ReportsNoProposal_WhenLlmDeclinesToGuess()
    {
        WriteFile("src/Calc.cs", "int Add(int a, int b) => a - b;");
        var llm = new ScriptedLlm(Proposal("Verilen bilgiyle güvenle düzeltemem."));
        var verifier = new ScriptedVerifier();

        var outcome = await new FixPipeline(llm, verifier).RunAsync(Context(), Analysis(), _root);

        // Boş öneri geçerli bir cevap; uydurma değişiklikten iyidir ve tekrar denenmez.
        Assert.Equal(FixStatus.NoProposal, outcome.Status);
        Assert.Equal(1, llm.CallCount);
        Assert.Equal(0, verifier.CallCount);
        Assert.Contains("güvenle düzeltemem", outcome.Summary);
    }

    [Fact]
    public async Task RunAsync_StopsEarly_WhenNoEditableSourceFileExists()
    {
        // Restore/deploy hatası: konum yok, düzenlenecek dosya da yok.
        var context = new ErrorContext
        {
            JobName = "deploy", FailedStepName = "Restore",
            Failures = { new Failure { Kind = FailureKind.Restore, Message = "NU1101: paket yok" } }
        };
        var llm = new ScriptedLlm();
        var verifier = new ScriptedVerifier();

        var outcome = await new FixPipeline(llm, verifier)
            .RunAsync(context, Analysis(affectedFile: null), _root);

        Assert.Equal(FixStatus.NoSourceFiles, outcome.Status);
        Assert.Equal(0, llm.CallCount);   // LLM'e hiç gidilmedi, para harcanmadı
    }

    [Fact]
    public async Task RunAsync_ReportsFilesRejected_WhenEveryCandidateIsPolicyBlocked()
    {
        // Hata gerçek bir test dosyasında: konum VAR, dosya diskte VAR, ama /fix'in
        // dokunması yasak. Bu "dosya yok"tan farklı bir durum - NoSourceFiles değil
        // FilesRejected dönmeli ki yorum "restore/deploy sorunu" demesin.
        WriteFile("CiAgent.Tests/CalcTests.cs", "Assert.Equal(5, sum);");

        var context = Context(file: "CiAgent.Tests/CalcTests.cs", line: 3);
        var analysis = Analysis(affectedFile: "CiAgent.Tests/CalcTests.cs");
        var llm = new ScriptedLlm();
        var verifier = new ScriptedVerifier();

        var outcome = await new FixPipeline(llm, verifier).RunAsync(context, analysis, _root);

        Assert.Equal(FixStatus.FilesRejected, outcome.Status);
        Assert.Equal(0, llm.CallCount);      // politikaya takıldı, LLM'e hiç gidilmedi
        var rejected = Assert.Single(outcome.RejectedPaths!);
        Assert.Equal("CiAgent.Tests/CalcTests.cs", rejected.Path);
        Assert.Contains("test", rejected.Reason, StringComparison.OrdinalIgnoreCase);
    }

    // --- Dry-run ----------------------------------------------------------

    [Fact]
    public async Task RunAsync_RevertsChanges_WhenDryRun_ButStillReportsSuccess()
    {
        const string original = "int Add(int a, int b) => a - b;";
        var path = WriteFile("src/Calc.cs", original);
        var llm = new ScriptedLlm(Proposal("düzeltildi", ("src/Calc.cs", "a - b", "a + b")));
        var verifier = new ScriptedVerifier(Pass);

        var outcome = await new FixPipeline(llm, verifier)
            .RunAsync(Context(), Analysis(), _root, dryRun: true);

        // Ne yapılacağı görülüyor ama diskte iz kalmıyor.
        Assert.Equal(FixStatus.Fixed, outcome.Status);
        Assert.Equal("src/Calc.cs", Assert.Single(outcome.AppliedEdits).File);
        Assert.Equal(original, await File.ReadAllTextAsync(path));
    }
}
