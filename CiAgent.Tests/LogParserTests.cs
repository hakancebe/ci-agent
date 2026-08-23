using CiAgent.Core;

namespace CiAgent.Tests;

public class LogParserTests
{
    [Fact]
    public void StripTimestamp_RemovesTimestampPrefix()
    {
        var input = "2026-08-04T13:56:19.3360749Z ##[group]Runner Image Provisioner";
        var result = LogParser.StripTimestamp(input);
        Assert.Equal("##[group]Runner Image Provisioner", result);
    }

    [Fact]
    public void StripTimestamp_LeavesLineWithoutTimestampUnchanged()
    {
        var input = "Expected: 5";
        var result = LogParser.StripTimestamp(input);
        Assert.Equal("Expected: 5", result);
    }

    [Fact]
    public void ExtractStepBlocks_SplitsLogIntoCorrectNumberOfBlocks()
    {
        var log = """
        2026-08-04T13:55:29.6133445Z ##[group]Run actions/checkout@v4
        2026-08-04T13:55:29.6134727Z with:
        2026-08-04T13:55:29.6159054Z ##[endgroup]
        2026-08-04T13:55:31.4585735Z ##[group]Run dotnet restore
        2026-08-04T13:55:31.4586073Z dotnet restore
        2026-08-04T13:55:41.3253307Z Restored project.csproj
        """;

        var blocks = LogParser.ExtractStepBlocks(log);

        Assert.Equal(2, blocks.Count);
        Assert.Contains("checkout", blocks[0]);
        Assert.Contains("dotnet restore", blocks[1]);
    }

    [Fact]
    public void ExtractStepBlocks_KeepsContentAfterEndgroup()
    {
        var log = """
        2026-08-04T13:55:48.1732271Z ##[group]Run dotnet test --no-build -c Release
        2026-08-04T13:55:48.1775268Z shell: /usr/bin/bash -e {0}
        2026-08-04T13:55:48.1776035Z ##[endgroup]
        2026-08-04T13:55:50.8272063Z Failed!  - Failed:     1, Passed:     1
        """;

        var blocks = LogParser.ExtractStepBlocks(log);

        Assert.Single(blocks);
        Assert.Contains("Failed!", blocks[0]);
    }

    [Fact]
    public void ExtractTestFailure_ParsesFilePathLineNumberAndMessage()
    {
        var stepBlock = """
        ##[group]Run dotnet test --no-build -c Release
          Failed CiPilot.Core.Tests.CalculatorTests.Add_ReturnsSum [2 ms]
          Error Message:
           Assert.Equal() Failure: Values differ
        Expected: 5
        Actual:   4
          Stack Trace:
             at CiPilot.Core.Tests.CalculatorTests.Add_ReturnsSum() in /home/runner/work/ci-agent-pilot/ci-agent-pilot/tests/CiPilot.Core.Tests/CalculatorTests.cs:line 12
        """;

        var (filePath, lineNumber, errorMessage) = LogParser.ExtractTestFailure(stepBlock);

        Assert.Equal("CalculatorTests.cs", filePath);
        Assert.Equal(12, lineNumber);
        Assert.Contains("Values differ", errorMessage);
    }

    [Fact]
    public void ExtractTestFailure_ReturnsNullsWhenNoFailureBlockPresent()
    {
        var stepBlock = """
        ##[group]Run dotnet restore
        dotnet restore
        Restored project.csproj
        """;

        var (filePath, lineNumber, errorMessage) = LogParser.ExtractTestFailure(stepBlock);

        Assert.Null(filePath);
        Assert.Null(lineNumber);
        Assert.Null(errorMessage);
    }
    [Fact]
    public void ExtractGenericError_ParsesNU1101RestoreError()
    {
        var stepBlock = """
    ##[group]Run dotnet restore
    dotnet restore
    shell: /usr/bin/bash -e {0}
    ##[endgroup]
      Determining projects to restore...
    /home/runner/work/ci-agent-pilot/ci-agent-pilot/src/CiPilot.Core/CiPilot.Core.csproj : error NU1101: Unable to find package Bu.Paket.Kesinlikle.Yok. No packages exist with this id in source(s): nuget.org [/home/runner/work/ci-agent-pilot/ci-agent-pilot/CiPilot.slnx]
      Failed to restore /home/runner/work/ci-agent-pilot/ci-agent-pilot/src/CiPilot.Core/CiPilot.Core.csproj (in 983 ms).
    ##[error]Process completed with exit code 1.
    """;

        var (filePath, lineNumber, errorMessage) = LogParser.ExtractGenericError(stepBlock);

        Assert.Equal("CiPilot.Core.csproj", Path.GetFileName(filePath));
        Assert.Null(lineNumber);
        Assert.Contains("NU1101", errorMessage);
        Assert.Contains("Bu.Paket.Kesinlikle.Yok", errorMessage);
    }

    [Fact]
    public void ExtractGenericError_FallsBackToGitHubErrorAnnotation_WhenNoMsBuildErrorPresent()
    {
        var stepBlock = """
    ##[group]Run ./scripts/deploy.sh
    ./scripts/deploy.sh
    ##[endgroup]
    Deploying...
    ##[error]Process completed with exit code 127.
    """;

        var (filePath, lineNumber, errorMessage) = LogParser.ExtractGenericError(stepBlock);

        Assert.Null(filePath);
        Assert.Null(lineNumber);
        Assert.Equal("Process completed with exit code 127.", errorMessage);
    }

    [Fact]
    public void ExtractGenericError_ReturnsNullsWhenNoErrorPresent()
    {
        var stepBlock = """
    ##[group]Run dotnet restore
    dotnet restore
    Restored project.csproj
    """;

        var (filePath, lineNumber, errorMessage) = LogParser.ExtractGenericError(stepBlock);

        Assert.Null(filePath);
        Assert.Null(lineNumber);
        Assert.Null(errorMessage);
    }

    [Fact]
    public void ExtractGenericError_ParsesCSharpCompilerError()
    {
        var stepBlock = """
    ##[group]Run dotnet build --no-restore -c Release
    dotnet build --no-restore -c Release
    ##[endgroup]
    ##[error]/home/runner/work/ci-agent-pilot/ci-agent-pilot/src/CiPilot.Core/Calculator.cs(5,42): error CS1002: ; expected [/home/runner/work/ci-agent-pilot/ci-agent-pilot/src/CiPilot.Core/CiPilot.Core.csproj]

    Build FAILED.
    """;

        var (filePath, lineNumber, errorMessage) = LogParser.ExtractGenericError(stepBlock);

        Assert.Equal("Calculator.cs", Path.GetFileName(filePath));
        Assert.Equal(5, lineNumber);
        Assert.Contains("CS1002", errorMessage);
        Assert.Contains("; expected", errorMessage);
    }

    [Fact]
    public void BuildErrorContext_CombinesAllFailingTests_WhenMultipleTestsFailInSameStep()
    {
        var job = new Octokit.WorkflowJob(
            id: 1, runId: 1, runUrl: "", nodeId: "", headSha: "sha", url: "", htmlUrl: "",
            status: Octokit.WorkflowJobStatus.Completed,
            conclusion: Octokit.WorkflowJobConclusion.Failure,
            createdAt: DateTimeOffset.UtcNow,
            startedAt: DateTimeOffset.UtcNow,
            completedAt: DateTimeOffset.UtcNow,
            name: "build-test",
            steps: new List<Octokit.WorkflowJobStep>
            {
            new(name: "Test",
                status: Octokit.WorkflowJobStatus.Completed,
                conclusion: Octokit.WorkflowJobConclusion.Failure,
                number: 4,
                startedAt: DateTimeOffset.UtcNow,
                completedAt: DateTimeOffset.UtcNow)
            },
            checkRunUrl: "",
            labels: new List<string>());

        var log = """
    ##[group]Run dotnet test --no-build -c Release
    dotnet test --no-build -c Release
    ##[endgroup]
      Failed CiPilot.Core.Tests.CalculatorTests.Add_ReturnsSum [28 ms]
      Error Message:
       Assert.Equal() Failure: Values differ
    Expected: 350
    Actual:   4
      Stack Trace:
         at CiPilot.Core.Tests.CalculatorTests.Add_ReturnsSum() in /home/runner/work/ci-agent-pilot/ci-agent-pilot/tests/CiPilot.Core.Tests/CalculatorTests.cs:line 12
      Failed CiPilot.Core.Tests.CalculatorTests.ThrowsUnexpectedException [< 1 ms]
      Error Message:
       System.InvalidOperationException : Beklenmeyen hata
      Stack Trace:
         at CiPilot.Core.Tests.CalculatorTests.ThrowsUnexpectedException() in /home/runner/work/ci-agent-pilot/ci-agent-pilot/tests/CiPilot.Core.Tests/CalculatorTests.cs:line 25

    Failed!  - Failed:     2, Passed:     1, Skipped:     0, Total:     3, Duration: 122 ms - CiPilot.Core.Tests.dll (net8.0)
    Post job cleanup.
    [command]/usr/bin/git version
    """;

        var context = LogParser.BuildErrorContext(job, Array.Empty<Octokit.CheckRunAnnotation>(), log);

        Assert.NotNull(context);
        // Her iki testin adı ve mesajı da ErrorMessage'da yer almalı - hiçbiri gizlenmemeli.
        Assert.Contains("Add_ReturnsSum", context!.ErrorMessage);
        Assert.Contains("ThrowsUnexpectedException", context.ErrorMessage);
        Assert.Contains("InvalidOperationException", context.ErrorMessage);
        Assert.Contains("Values differ", context.ErrorMessage);
        // Post job cleanup gürültüsü RawStepLog'a sızmamalı.
        Assert.DoesNotContain("Post job cleanup", context.RawStepLog);
    }

    [Fact]
    public void BuildErrorContext_TrimsPostJobCleanupNoise_FromGenericErrorBlock()
    {
        var job = new Octokit.WorkflowJob(
            id: 1, runId: 1, runUrl: "", nodeId: "", headSha: "sha", url: "", htmlUrl: "",
            status: Octokit.WorkflowJobStatus.Completed,
            conclusion: Octokit.WorkflowJobConclusion.Failure,
            createdAt: DateTimeOffset.UtcNow,
            startedAt: DateTimeOffset.UtcNow,
            completedAt: DateTimeOffset.UtcNow,
            name: "deploy",
            steps: new List<Octokit.WorkflowJobStep>
            {
            new(name: "Fake deploy",
                status: Octokit.WorkflowJobStatus.Completed,
                conclusion: Octokit.WorkflowJobConclusion.Failure,
                number: 2,
                startedAt: DateTimeOffset.UtcNow,
                completedAt: DateTimeOffset.UtcNow)
            },
            checkRunUrl: "",
            labels: new List<string>());

        var log = """
    ##[group]Run echo "Deploying..."
    echo "Deploying..."
    ./scripts/deploy.sh
    ##[endgroup]
    Deploying...
    ##[error]Process completed with exit code 1.
    Node 20 is being deprecated.
    Post job cleanup.
    [command]/usr/bin/git version
    Cleaning up orphan processes
    """;

        var context = LogParser.BuildErrorContext(job, Array.Empty<Octokit.CheckRunAnnotation>(), log);

        Assert.NotNull(context);
        Assert.DoesNotContain("Post job cleanup", context!.RawStepLog);
        Assert.DoesNotContain("orphan processes", context.RawStepLog);
        Assert.Contains("exit code 1", context.RawStepLog);
    }

    [Fact]
    public void BuildErrorContext_FallsBackToGenericError_WhenTestFailureFormatDoesNotMatch()
    {
        var job = new Octokit.WorkflowJob(
            id: 1, runId: 1, runUrl: "", nodeId: "", headSha: "sha", url: "", htmlUrl: "",
            status: Octokit.WorkflowJobStatus.Completed,
            conclusion: Octokit.WorkflowJobConclusion.Failure,
            createdAt: DateTimeOffset.UtcNow,
            startedAt: DateTimeOffset.UtcNow,
            completedAt: DateTimeOffset.UtcNow,
            name: "build-test",
            steps: new List<Octokit.WorkflowJobStep>
            {
            new(name: "Restore",
                status: Octokit.WorkflowJobStatus.Completed,
                conclusion: Octokit.WorkflowJobConclusion.Failure,
                number: 3,
                startedAt: DateTimeOffset.UtcNow,
                completedAt: DateTimeOffset.UtcNow)
            },
            checkRunUrl: "",
            labels: new List<string>());

        var log = """
    ##[group]Run dotnet restore
    dotnet restore
    ##[endgroup]
    /home/runner/work/ci-agent-pilot/ci-agent-pilot/src/CiPilot.Core/CiPilot.Core.csproj : error NU1101: Unable to find package Bu.Paket.Kesinlikle.Yok. No packages exist with this id in source(s): nuget.org [/home/runner/work/ci-agent-pilot/ci-agent-pilot/CiPilot.slnx]
    ##[error]Process completed with exit code 1.
    """;

        var context = LogParser.BuildErrorContext(job, Array.Empty<Octokit.CheckRunAnnotation>(), log);

        Assert.NotNull(context);
        Assert.NotNull(context!.RawStepLog);
        Assert.Contains("NU1101", context.ErrorMessage);
    }
}