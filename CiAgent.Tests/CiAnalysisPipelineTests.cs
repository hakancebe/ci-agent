using CiAgent.Core;
using OpenAI.Chat;
using Octokit;

namespace CiAgent.Tests;

/// <summary>
/// Uçtan uca orkestrasyon testleri. Bu akış Program.cs'te top-level script olduğu
/// sürece hiç test edilemiyordu; CiAnalysisPipeline'a taşınmasının asıl kazancı bu.
/// Hiçbir test ağa çıkmıyor.
/// </summary>
public class CiAnalysisPipelineTests
{
    // --- Test double'ları ------------------------------------------------

    private sealed class FakeGateway : IGitHubGateway
    {
        public List<WorkflowJob> Jobs { get; init; } = new();
        public Dictionary<long, string> LogsByJobId { get; init; } = new();
        public Dictionary<string, string?> FilesByPath { get; init; } = new();
        public Exception? FileFetchException { get; set; }

        public List<long> AnnotationCalls { get; } = new();
        public List<long> LogCalls { get; } = new();
        public List<string> FileCalls { get; } = new();

        public Task<IReadOnlyList<WorkflowJob>> GetJobsAsync(string owner, string repo, long runId)
            => Task.FromResult<IReadOnlyList<WorkflowJob>>(Jobs);

        /// <summary>null bırakılırsa "workflow bilgisi alınamadı" yolu sınanır.</summary>
        public WorkflowInfo? Workflow { get; set; }

        public Task<WorkflowInfo?> GetWorkflowInfoAsync(string owner, string repo, long runId)
            => Task.FromResult(Workflow);

        public Task<IReadOnlyList<CheckRunAnnotation>> GetAnnotationsAsync(string owner, string repo, long jobId)
        {
            AnnotationCalls.Add(jobId);
            return Task.FromResult<IReadOnlyList<CheckRunAnnotation>>(Array.Empty<CheckRunAnnotation>());
        }

        public Task<string> DownloadJobLogAsync(string owner, string repo, long jobId)
        {
            LogCalls.Add(jobId);
            return Task.FromResult(LogsByJobId.TryGetValue(jobId, out var log) ? log : "");
        }

        public Task<string?> GetFileContentAsync(string owner, string repo, string path, string ref_)
        {
            FileCalls.Add(path);
            if (FileFetchException is not null) throw FileFetchException;
            return Task.FromResult(FilesByPath.TryGetValue(path, out var c) ? c : null);
        }

        /// <summary>Repodaki yollar. Varsayılan: FilesByPath'in anahtarları.</summary>
        public List<string>? RepoPaths { get; set; }
        public Exception? TreeException { get; set; }
        public int TreeCalls { get; private set; }

        public Task<IReadOnlyList<string>> ListFilePathsAsync(string owner, string repo, string ref_)
        {
            TreeCalls++;
            if (TreeException is not null) throw TreeException;
            return Task.FromResult<IReadOnlyList<string>>(
                RepoPaths ?? FilesByPath.Keys.ToList());
        }
    }

    private sealed class FakeLlm : LlmService
    {
        private readonly string? _json;
        private readonly Exception? _throw;

        public int CallCount { get; private set; }

        public FakeLlm(string? json = null, Exception? toThrow = null)
        {
            _json = json;
            _throw = toThrow;
        }

        internal override Task<string> CompleteAsync(
            List<ChatMessage> messages, ChatCompletionOptions options)
        {
            CallCount++;
            if (_throw is not null) throw _throw;
            return Task.FromResult(_json!);
        }
    }

    private sealed class RecordingReport : ReportService
    {
        public RecordingReport() : base(null!) { }

        public int CallCount { get; private set; }
        public AnalysisResult? Result { get; private set; }
        public ErrorContext? Context { get; private set; }
        public string? HeadSha { get; private set; }

        public override Task ReportAsync(
            AnalysisResult result, ErrorContext context,
            string owner, string repo, string headSha, long runId)
        {
            CallCount++;
            Result = result;
            Context = context;
            HeadSha = headSha;
            return Task.CompletedTask;
        }
    }

    // --- Kurulum yardımcıları -------------------------------------------

    private const string ValidJson = """
        {
          "summary": "Analiz tamam",
          "analyses": [
            {
              "title": "Yanlış operatör", "rootCause": "a - b yazılmış",
              "suggestedFix": "a + b yapın", "confidence": "high",
              "affectedFile": "src/Calc.cs", "affectedLine": 12
            }
          ]
        }
        """;

    private static WorkflowJob Job(long id, string name, string conclusion, string stepName = "Test") =>
        new(
            id: id, runId: 1, runUrl: "", nodeId: "", headSha: "sha-abc", url: "", htmlUrl: "",
            status: WorkflowJobStatus.Completed,
            conclusion: conclusion == "failure"
                ? WorkflowJobConclusion.Failure
                : WorkflowJobConclusion.Success,
            createdAt: DateTimeOffset.UtcNow, startedAt: DateTimeOffset.UtcNow, completedAt: DateTimeOffset.UtcNow,
            name: name,
            steps: new List<WorkflowJobStep>
            {
                new(name: stepName,
                    status: WorkflowJobStatus.Completed,
                    conclusion: conclusion == "failure"
                        ? WorkflowJobConclusion.Failure
                        : WorkflowJobConclusion.Success,
                    number: 1, startedAt: DateTimeOffset.UtcNow, completedAt: DateTimeOffset.UtcNow)
            },
            checkRunUrl: "", labels: new List<string>());

    private static string TestLog(string testName, string file, int line) => $"""
    ##[group]Run dotnet test --no-build -c Release
    dotnet test --no-build -c Release
    ##[endgroup]
      Failed {testName} [28 ms]
      Error Message:
       Assert.Equal() Failure: Values differ
      Stack Trace:
         at {testName}() in /home/runner/work/ci-agent-pilot/ci-agent-pilot/{file}:line {line}

    Failed!  - Failed:     1, Passed:     1, Skipped:     0, Total:     2, Duration: 1 ms - Tests.dll (net8.0)
    """;

    private const string RestoreLog = """
    ##[group]Run dotnet restore
    dotnet restore
    ##[endgroup]
    /home/runner/work/ci-agent-pilot/ci-agent-pilot/src/Core.csproj : error NU1101: Unable to find package Yok. No packages exist [/home/runner/work/ci-agent-pilot/ci-agent-pilot/App.slnx]
    ##[error]Process completed with exit code 1.
    """;

    // --- Testler ---------------------------------------------------------

    // --- Workflow dosyası ------------------------------------------------

    [Fact]
    public async Task RunAsync_AttachesWorkflowInfo_SoTheModelNeedNotGuessTheFileName()
    {
        // Deploy/yapılandırma hatalarında düzeltilecek dosya workflow'un
        // kendisidir ve adı LOGDAN ÇIKARILAMAZ. Canlı ölçümde model bu yüzden
        // olmayan bir ad uydurmuştu (".github/workflows/deploy.yml").
        var gateway = new FakeGateway
        {
            Jobs = { Job(10, "deploy", "failure", stepName: "Smoke test") },
            LogsByJobId = { [10] = RestoreLog },
            Workflow = new WorkflowInfo("CD", ".github/workflows/cd.yml")
        };

        var outcome = await new CiAnalysisPipeline(gateway, new FakeLlm(ValidJson), new RecordingReport())
            .RunAsync("o", "r", 99);

        Assert.NotNull(outcome.Context);
        Assert.Equal(".github/workflows/cd.yml", outcome.Context!.Workflow?.Path);
        Assert.Equal("CD", outcome.Context.Workflow?.Name);
    }

    [Fact]
    public async Task RunAsync_StillReports_WhenWorkflowInfoIsUnavailable()
    {
        // Bu bilgi "olsa iyi olur" seviyesinde: alınamazsa analiz durmamalı.
        var gateway = new FakeGateway
        {
            Jobs = { Job(10, "deploy", "failure", stepName: "Smoke test") },
            LogsByJobId = { [10] = RestoreLog },
            Workflow = null
        };

        var outcome = await new CiAnalysisPipeline(gateway, new FakeLlm(ValidJson), new RecordingReport())
            .RunAsync("o", "r", 99);

        Assert.Equal(PipelineStatus.Reported, outcome.Status);
        Assert.Null(outcome.Context!.Workflow);
    }

    [Fact]
    public async Task RunAsync_ReturnsNoFailedJobs_WhenEveryJobSucceeded()
    {
        var gateway = new FakeGateway { Jobs = { Job(1, "build", "success") } };
        var llm = new FakeLlm(ValidJson);
        var report = new RecordingReport();

        var outcome = await new CiAnalysisPipeline(gateway, llm, report).RunAsync("o", "r", 99);

        Assert.Equal(PipelineStatus.NoFailedJobs, outcome.Status);
        // Başarısız job yoksa ne log indirilmeli, ne LLM'e gidilmeli, ne raporlanmalı.
        Assert.Empty(gateway.LogCalls);
        Assert.Equal(0, llm.CallCount);
        Assert.Equal(0, report.CallCount);
    }

    [Fact]
    public async Task RunAsync_FetchesLogsForEveryFailedJob_NotJustTheFirst()
    {
        // Asıl regresyon koruması: eski Program.cs ilk başarısız job'da break ediyordu.
        var gateway = new FakeGateway
        {
            Jobs =
            {
                Job(10, "build (ubuntu)", "failure"),
                Job(11, "deploy", "failure", stepName: "Restore"),
                Job(12, "lint", "success")
            },
            LogsByJobId =
            {
                [10] = TestLog("CalcTests.Add", "src/Calc.cs", 12),
                [11] = RestoreLog
            }
        };

        var outcome = await new CiAnalysisPipeline(gateway, new FakeLlm(ValidJson), new RecordingReport())
            .RunAsync("o", "r", 99);

        Assert.Equal(PipelineStatus.Reported, outcome.Status);

        // Her İKİ başarısız job için log+annotation çekilmeli, başarılı olan atlanmalı.
        Assert.Equal(new[] { 10L, 11L }, gateway.LogCalls);
        Assert.Equal(new[] { 10L, 11L }, gateway.AnnotationCalls);

        // Her iki job'ın hatası da tek ErrorContext'te birleşmeli.
        Assert.Equal(2, outcome.Context!.Failures.Count);
        Assert.Contains(outcome.Context.Failures, f => f.JobName == "build (ubuntu)");
        Assert.Contains(outcome.Context.Failures, f => f.JobName == "deploy");
    }

    [Fact]
    public async Task RunAsync_ReturnsNoAnalyzableFailure_WhenFailedJobHasNoFailedStep()
    {
        // Job'ın conclusion'ı "failure" ama adımlarının hepsi success — GitHub'da
        // job seviyesinde bir sorun olduğunda (ör. runner çökmesi) görülebilen durum.
        var jobWithNoFailedStep = new WorkflowJob(
            id: 10, runId: 1, runUrl: "", nodeId: "", headSha: "sha", url: "", htmlUrl: "",
            status: WorkflowJobStatus.Completed, conclusion: WorkflowJobConclusion.Failure,
            createdAt: DateTimeOffset.UtcNow, startedAt: DateTimeOffset.UtcNow, completedAt: DateTimeOffset.UtcNow,
            name: "weird",
            steps: new List<WorkflowJobStep>
            {
                new(name: "Build", status: WorkflowJobStatus.Completed,
                    conclusion: WorkflowJobConclusion.Success,
                    number: 1, startedAt: DateTimeOffset.UtcNow, completedAt: DateTimeOffset.UtcNow)
            },
            checkRunUrl: "", labels: new List<string>());

        var gateway = new FakeGateway
        {
            Jobs = { jobWithNoFailedStep },
            LogsByJobId = { [10] = "irrelevant" }
        };

        var llm = new FakeLlm(ValidJson);
        var report = new RecordingReport();

        var outcome = await new CiAnalysisPipeline(gateway, llm, report).RunAsync("o", "r", 99);

        Assert.Equal(PipelineStatus.NoAnalyzableFailure, outcome.Status);
        Assert.Equal(0, llm.CallCount);
        Assert.Equal(0, report.CallCount);
    }

    [Fact]
    public async Task RunAsync_AttachesCodeSnippetToLocatedFailure()
    {
        var fileContent = string.Join("\n", Enumerable.Range(1, 30).Select(i => $"satır {i};"));
        var gateway = new FakeGateway
        {
            Jobs = { Job(10, "build", "failure") },
            LogsByJobId = { [10] = TestLog("CalcTests.Add", "src/Calc.cs", 12) },
            FilesByPath = { ["src/Calc.cs"] = fileContent }
        };

        var outcome = await new CiAnalysisPipeline(gateway, new FakeLlm(ValidJson), new RecordingReport())
            .RunAsync("o", "r", 99);

        var failure = Assert.Single(outcome.Context!.Failures);
        Assert.NotNull(failure.CodeSnippet);
        Assert.Contains(">> 12: satır 12;", failure.CodeSnippet);
        Assert.Equal(new[] { "src/Calc.cs" }, gateway.FileCalls);
    }

    [Fact]
    public async Task RunAsync_SkipsTheLlm_WhenAnalysisIsAlreadyKnown()
    {
        // /fix bu yolu kullanıyor: analiz yorumuna gömülü sonucu okuyup aynısını
        // geçiriyor. İkinci kez sormak hem para harcardı hem de — fixable kararı
        // kararsız olduğu için — rozetle /fix'in kararını ayrıştırırdı.
        var gateway = new FakeGateway
        {
            Jobs = { Job(10, "build", "failure") },
            LogsByJobId = { [10] = TestLog("A.CalcTests.Add", "tests/CalcTests.cs", 3) }
        };
        var llm = new FakeLlm(ValidJson);

        var stored = new AnalysisResult
        {
            Summary = "önceden yapılmış analiz",
            Analyses = { new Analysis
            {
                Title = "t", RootCause = "r", SuggestedFix = "s",
                Confidence = "high", Fixable = false
            }}
        };

        var outcome = await new CiAnalysisPipeline(gateway, llm, new RecordingReport())
            .RunAsync("o", "r", 99, dryRun: true, precomputed: stored);

        Assert.Equal(0, llm.CallCount);                                  // LLM'e hiç gidilmedi
        Assert.Equal("önceden yapılmış analiz", outcome.Result!.Summary); // aynı sonuç kullanıldı
        Assert.False(Assert.Single(outcome.Result.Analyses).Fixable);

        // Bağlam yine de kurulmalı: /fix'in düzeltme için buna ihtiyacı var.
        Assert.NotNull(outcome.Context);
    }

    // --- Test edilen kodu bağlama ekleme --------------------------------
    // Canlıda görülen eksik: bir test patladığında hata konumu TEST dosyasını
    // gösteriyor, dolayısıyla modele yalnızca test kodu gidiyor ve testin
    // çağırdığı bozuk metot hiç görünmüyordu. Model de "göremediğim şey
    // hakkında tahmin yürütmem" deyip düzeltmeyi reddediyordu.

    [Fact]
    public async Task RunAsync_AttachesImplementationSource_ForFailingTest()
    {
        var gateway = new FakeGateway
        {
            Jobs = { Job(10, "build", "failure") },
            LogsByJobId =
            {
                [10] = TestLog("CiPilot.Core.Tests.CalculatorTests.Add_ReturnsSum",
                               "tests/CiPilot.Core.Tests/CalculatorTests.cs", 12)
            },
            FilesByPath =
            {
                ["tests/CiPilot.Core.Tests/CalculatorTests.cs"] = "Assert.Equal(4, calc.Add(2, 2));",
                ["src/CiPilot.Core/Calculator.cs"] = "public int Add(int a, int b) => a - b;"
            }
        };

        var outcome = await new CiAnalysisPipeline(gateway, new FakeLlm(ValidJson), new RecordingReport())
            .RunAsync("o", "r", 99);

        // Asıl kazanç: bozuk metodun KENDİSİ artık bağlamda.
        var (path, content) = Assert.Single(outcome.Context!.RelatedSources);
        Assert.Equal("src/CiPilot.Core/Calculator.cs", path);
        Assert.Contains("a - b", content);
    }

    [Fact]
    public async Task RunAsync_DoesNotLookForImplementation_WhenFailureIsNotATest()
    {
        // Derleme hatasında konum zaten bozuk dosyayı gösteriyor; ağaç çekmek
        // gereksiz bir API çağrısı olurdu.
        var gateway = new FakeGateway
        {
            Jobs = { Job(10, "build", "failure") },
            LogsByJobId = { [10] = RestoreLog },
            FilesByPath = { ["src/Core.csproj"] = "kod" }
        };

        await new CiAnalysisPipeline(gateway, new FakeLlm(ValidJson), new RecordingReport())
            .RunAsync("o", "r", 99);

        Assert.Equal(0, gateway.TreeCalls);
    }

    [Fact]
    public async Task RunAsync_ContinuesWithoutImplementation_WhenRepoTreeCannotBeRead()
    {
        // Zenginleştirme bir kolaylık, ön koşul değil: ağaç çekilemezse analiz
        // eskisi gibi (eksik bağlamla) devam etmeli, patlamamalı.
        var gateway = new FakeGateway
        {
            Jobs = { Job(10, "build", "failure") },
            LogsByJobId =
            {
                [10] = TestLog("A.CalculatorTests.Add", "tests/CalculatorTests.cs", 3)
            },
            FilesByPath = { ["tests/CalculatorTests.cs"] = "test kodu" },
            TreeException = new InvalidOperationException("ağaç yok")
        };

        var outcome = await new CiAnalysisPipeline(gateway, new FakeLlm(ValidJson), new RecordingReport())
            .RunAsync("o", "r", 99);

        Assert.Equal(PipelineStatus.Reported, outcome.Status);
        Assert.Empty(outcome.Context!.RelatedSources);
    }

    [Fact]
    public async Task RunAsync_DownloadsEachFileOnce_WhenSeveralFailuresShareIt()
    {
        // Matrix build'de aynı dosya defalarca istenir; cache olmasa her failure
        // için ayrı bir Contents API çağrısı giderdi.
        var log = """
    ##[group]Run dotnet test --no-build -c Release
    dotnet test --no-build -c Release
    ##[endgroup]
      Failed CalcTests.Add [1 ms]
      Error Message:
       Values differ
      Stack Trace:
         at CalcTests.Add() in /home/runner/work/ci-agent-pilot/ci-agent-pilot/src/Calc.cs:line 12
      Failed CalcTests.Sub [1 ms]
      Error Message:
       Values differ
      Stack Trace:
         at CalcTests.Sub() in /home/runner/work/ci-agent-pilot/ci-agent-pilot/src/Calc.cs:line 20

    Failed!  - Failed:     2, Passed:     0, Skipped:     0, Total:     2, Duration: 2 ms - Tests.dll (net8.0)
    """;

        var gateway = new FakeGateway
        {
            Jobs = { Job(10, "build", "failure") },
            LogsByJobId = { [10] = log },
            FilesByPath = { ["src/Calc.cs"] = string.Join("\n", Enumerable.Range(1, 30).Select(i => $"satır {i};")) }
        };

        var outcome = await new CiAnalysisPipeline(gateway, new FakeLlm(ValidJson), new RecordingReport())
            .RunAsync("o", "r", 99);

        Assert.Equal(2, outcome.Context!.Failures.Count);
        Assert.All(outcome.Context.Failures, f => Assert.NotNull(f.CodeSnippet));
        // İki failure, aynı dosya -> TEK indirme.
        Assert.Equal(new[] { "src/Calc.cs" }, gateway.FileCalls);
    }

    [Fact]
    public async Task RunAsync_StillAnalyzesAndReports_WhenCodeFetchThrows()
    {
        var gateway = new FakeGateway
        {
            Jobs = { Job(10, "build", "failure") },
            LogsByJobId = { [10] = TestLog("CalcTests.Add", "src/Calc.cs", 12) },
            FileFetchException = new HttpRequestException("izin yok")
        };
        var llm = new FakeLlm(ValidJson);
        var report = new RecordingReport();

        var outcome = await new CiAnalysisPipeline(gateway, llm, report).RunAsync("o", "r", 99);

        // Kod çekilemedi ama akış durmadı: analiz yapıldı, rapor atıldı.
        Assert.Equal(PipelineStatus.Reported, outcome.Status);
        Assert.Null(Assert.Single(outcome.Context!.Failures).CodeSnippet);
        Assert.Equal(1, llm.CallCount);
        Assert.Equal(1, report.CallCount);
    }

    [Fact]
    public async Task RunAsync_ReportsLlmFailure_InsteadOfCrashing()
    {
        var gateway = new FakeGateway
        {
            Jobs = { Job(10, "build", "failure") },
            LogsByJobId = { [10] = TestLog("CalcTests.Add", "src/Calc.cs", 12) }
        };
        var llm = new FakeLlm(toThrow: new InvalidOperationException("deployment bulunamadı"));
        var report = new RecordingReport();

        var outcome = await new CiAnalysisPipeline(gateway, llm, report).RunAsync("o", "r", 99);

        // Durum sessizce kaybolmamalı: yine raporlanmalı, ama kök neden "analiz başarısız".
        Assert.Equal(PipelineStatus.Reported, outcome.Status);
        Assert.Equal(1, report.CallCount);

        var analysis = Assert.Single(report.Result!.Analyses);
        Assert.Equal("low", analysis.Confidence);
        Assert.Contains("deployment bulunamadı", analysis.RootCause);
    }

    [Fact]
    public async Task RunAsync_WritesNothingToGitHub_WhenDryRun()
    {
        var gateway = new FakeGateway
        {
            Jobs = { Job(10, "build", "failure") },
            LogsByJobId = { [10] = TestLog("CalcTests.Add", "src/Calc.cs", 12) }
        };
        var llm = new FakeLlm(ValidJson);
        var report = new RecordingReport();

        var outcome = await new CiAnalysisPipeline(gateway, llm, report)
            .RunAsync("o", "r", 99, dryRun: true);

        // Asıl garanti: raporlama HİÇ çağrılmadı.
        Assert.Equal(0, report.CallCount);
        Assert.Equal(PipelineStatus.DryRun, outcome.Status);

        // Ama analiz yapıldı - dry-run'ın faydası sonucu görebilmek.
        Assert.Equal(1, llm.CallCount);
        Assert.Equal("Analiz tamam", outcome.Result!.Summary);
        Assert.Single(outcome.Result.Analyses);
    }

    [Fact]
    public async Task RunAsync_ReportsNormally_WhenDryRunIsFalse()
    {
        // Karşıt test: dry-run varsayılanı kapalı, normal akış yazmaya devam ediyor.
        var gateway = new FakeGateway
        {
            Jobs = { Job(10, "build", "failure") },
            LogsByJobId = { [10] = TestLog("CalcTests.Add", "src/Calc.cs", 12) }
        };
        var report = new RecordingReport();

        var outcome = await new CiAnalysisPipeline(gateway, new FakeLlm(ValidJson), report)
            .RunAsync("o", "r", 99);

        Assert.Equal(PipelineStatus.Reported, outcome.Status);
        Assert.Equal(1, report.CallCount);
    }

    [Fact]
    public async Task RunAsync_PassesHeadShaFromFailedJob_ToReport()
    {
        var gateway = new FakeGateway
        {
            Jobs = { Job(10, "build", "failure") },
            LogsByJobId = { [10] = TestLog("CalcTests.Add", "src/Calc.cs", 12) }
        };
        var report = new RecordingReport();

        await new CiAnalysisPipeline(gateway, new FakeLlm(ValidJson), report).RunAsync("o", "r", 99);

        Assert.Equal("sha-abc", report.HeadSha);
    }
}
