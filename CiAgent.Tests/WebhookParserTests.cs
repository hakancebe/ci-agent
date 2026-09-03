using System.Text.Json;
using CiAgent.Service;

namespace CiAgent.Tests;

/// <summary>
/// Bu testlerin asıl değeri şu: buradaki kuralların hepsi eskiden
/// .github/workflows/ci-agent.yml içinde yaşıyordu — `workflows: ["CI"]`,
/// `if: conclusion == 'failure'`. YAML'de hiçbiri test edilemiyordu; bir yazım
/// hatası ancak canlıda, yanlış repoya yorum atıldığında fark edilirdi.
/// </summary>
public class WebhookParserTests
{
    private static readonly string[] WatchCi = { "CI" };

    private static JsonDocument WorkflowRunPayload(
        string action = "completed",
        string conclusion = "failure",
        string workflowName = "CI",
        long runId = 12345,
        string owner = "hakancebe",
        string repo = "ci-agent-pilot",
        long? installationId = 999)
    {
        var installation = installationId is null
            ? ""
            : $$""", "installation": { "id": {{installationId}} }""";

        return JsonDocument.Parse($$"""
        {
          "action": "{{action}}",
          "workflow_run": {
            "id": {{runId}},
            "name": "{{workflowName}}",
            "conclusion": "{{conclusion}}"
          },
          "repository": {
            "name": "{{repo}}",
            "owner": { "login": "{{owner}}" }
          }{{installation}}
        }
        """);
    }

    [Fact]
    public void Parse_AcceptsFailedWatchedWorkflow()
    {
        using var payload = WorkflowRunPayload();

        var outcome = WebhookParser.Parse("workflow_run", "delivery-1", payload, WatchCi);

        Assert.NotNull(outcome.Job);
        Assert.Equal("hakancebe", outcome.Job!.Owner);
        Assert.Equal("ci-agent-pilot", outcome.Job.Repo);
        Assert.Equal(12345, outcome.Job.RunId);
        Assert.Equal(999, outcome.Job.InstallationId);
        Assert.Equal("delivery-1", outcome.Job.DeliveryId);
    }

    [Fact]
    public void Parse_IgnoresSuccessfulRun()
    {
        using var payload = WorkflowRunPayload(conclusion: "success");

        var outcome = WebhookParser.Parse("workflow_run", "d", payload, WatchCi);

        Assert.Null(outcome.Job);
        Assert.Contains("success", outcome.Reason);
    }

    [Theory]
    [InlineData("cancelled")]
    [InlineData("skipped")]
    [InlineData("timed_out")]
    public void Parse_IgnoresNonFailureConclusions(string conclusion)
    {
        // Yalnızca "failure" analiz edilmeli. İptal edilen ya da atlanan bir run'da
        // analiz edilecek gerçek bir hata yok; LLM'e göndermek boşa maliyet olurdu.
        using var payload = WorkflowRunPayload(conclusion: conclusion);

        var outcome = WebhookParser.Parse("workflow_run", "d", payload, WatchCi);

        Assert.Null(outcome.Job);
    }

    [Theory]
    [InlineData("requested")]
    [InlineData("in_progress")]
    public void Parse_IgnoresNonCompletedActions(string action)
    {
        // workflow_run üç action ile geliyor; sadece completed'da sonuç var.
        using var payload = WorkflowRunPayload(action: action);

        var outcome = WebhookParser.Parse("workflow_run", "d", payload, WatchCi);

        Assert.Null(outcome.Job);
    }

    [Fact]
    public void Parse_IgnoresUnwatchedWorkflow()
    {
        // Bu, eski YAML'deki `workflows: ["CI"]` filtresinin karşılığı. Kaybolursa
        // agent kendi deploy workflow'unun hatasını bile analiz etmeye kalkar.
        using var payload = WorkflowRunPayload(workflowName: "Deploy");

        var outcome = WebhookParser.Parse("workflow_run", "d", payload, WatchCi);

        Assert.Null(outcome.Job);
        Assert.Contains("Deploy", outcome.Reason);
    }

    [Fact]
    public void Parse_WorkflowNameMatchIsCaseInsensitive()
    {
        using var payload = WorkflowRunPayload(workflowName: "ci");

        var outcome = WebhookParser.Parse("workflow_run", "d", payload, WatchCi);

        Assert.NotNull(outcome.Job);
    }

    [Fact]
    public void Parse_EmptyWatchListMeansWatchEverything()
    {
        using var payload = WorkflowRunPayload(workflowName: "Nightly Build");

        var outcome = WebhookParser.Parse("workflow_run", "d", payload, Array.Empty<string>());

        Assert.NotNull(outcome.Job);
    }

    [Fact]
    public void Parse_IgnoresOtherEventTypes()
    {
        // issue_comment Faz 2'de işlenecek; şimdilik sessizce yok sayılmalı,
        // HATA olarak değil - yoksa GitHub teslimatı başarısız sayıp tekrar gönderir.
        using var payload = WorkflowRunPayload();

        var outcome = WebhookParser.Parse("issue_comment", "d", payload, WatchCi);

        Assert.Null(outcome.Job);
        Assert.Contains("issue_comment", outcome.Reason);
    }

    [Fact]
    public void Parse_IgnoresPayloadWithoutInstallationId()
    {
        // Installation ID olmadan token üretilemez, yani API'ye hiç çıkamayız.
        using var payload = WorkflowRunPayload(installationId: null);

        var outcome = WebhookParser.Parse("workflow_run", "d", payload, WatchCi);

        Assert.Null(outcome.Job);
        Assert.Contains("installation", outcome.Reason);
    }

    [Fact]
    public void Parse_IgnoresMalformedPayload()
    {
        // Beklenen alanları olmayan payload patlamamalı, yok sayılmalı.
        using var payload = JsonDocument.Parse("""{ "action": "completed" }""");

        var outcome = WebhookParser.Parse("workflow_run", "d", payload, WatchCi);

        Assert.Null(outcome.Job);
    }
}
