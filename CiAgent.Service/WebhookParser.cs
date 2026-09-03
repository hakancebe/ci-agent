using System.Text.Json;

namespace CiAgent.Service;

/// <summary>Kuyruğa girecek iş: hangi repo'nun hangi run'ı, hangi installation adına.</summary>
internal sealed record AnalysisJob(
    string DeliveryId,
    string Owner,
    string Repo,
    long RunId,
    long InstallationId)
{
    public override string ToString() => $"{Owner}/{Repo} run {RunId}";
}

/// <summary>Payload ayrıştırmanın sonucu — iş üretildiyse neden, üretilmediyse neden.</summary>
internal sealed record ParseOutcome(AnalysisJob? Job, string Reason)
{
    public static ParseOutcome Accepted(AnalysisJob job) => new(job, "kuyruğa alındı");
    public static ParseOutcome Ignored(string reason) => new(null, reason);
}

/// <summary>
/// Webhook payload'ından iş üretir. Bilerek "toleranslı": tanımadığı olayı ya da
/// beklemediği bir şekli HATA saymaz, sessizce yok sayar ve sebebini söyler —
/// GitHub abone olduğumuz olayların her çeşidini gönderiyor (ör. workflow_run'ın
/// requested/in_progress action'ları) ve bunların çoğu bizi ilgilendirmiyor.
/// </summary>
internal static class WebhookParser
{
    /// <param name="watchedWorkflows">
    /// İzlenecek workflow adları. Bu filtre eskiden ci-agent.yml'de
    /// `workflows: ["CI"]` olarak duruyordu — YAML'de saklı, test edilmeyen bir iş
    /// kuralıydı. Kaybolursa agent repodaki HER workflow hatasını (kendi deploy
    /// workflow'u dahil) analiz etmeye başlar; boş liste "hepsini izle" demektir.
    /// </param>
    public static ParseOutcome Parse(
        string eventName,
        string deliveryId,
        JsonDocument payload,
        IReadOnlyCollection<string> watchedWorkflows)
    {
        if (!string.Equals(eventName, "workflow_run", StringComparison.OrdinalIgnoreCase))
            return ParseOutcome.Ignored($"'{eventName}' olayı bu fazda işlenmiyor");

        var root = payload.RootElement;

        // workflow_run üç action ile geliyor: requested, in_progress, completed.
        // Yalnızca sonuncusunda bir sonuç (conclusion) var.
        var action = GetString(root, "action");
        if (action != "completed")
            return ParseOutcome.Ignored($"workflow_run action='{action}', 'completed' değil");

        if (!root.TryGetProperty("workflow_run", out var run))
            return ParseOutcome.Ignored("payload'da workflow_run yok");

        var conclusion = GetString(run, "conclusion");
        if (conclusion != "failure")
            return ParseOutcome.Ignored($"conclusion='{conclusion}', analiz edilecek bir hata yok");

        var workflowName = GetString(run, "name");
        if (watchedWorkflows.Count > 0
            && !watchedWorkflows.Contains(workflowName ?? "", StringComparer.OrdinalIgnoreCase))
        {
            return ParseOutcome.Ignored(
                $"'{workflowName}' izlenen workflow listesinde değil "
                + $"({string.Join(", ", watchedWorkflows)})");
        }

        if (!run.TryGetProperty("id", out var idElement) || !idElement.TryGetInt64(out var runId))
            return ParseOutcome.Ignored("workflow_run.id okunamadı");

        if (!root.TryGetProperty("repository", out var repository))
            return ParseOutcome.Ignored("payload'da repository yok");

        var repo = GetString(repository, "name");
        var owner = repository.TryGetProperty("owner", out var ownerElement)
            ? GetString(ownerElement, "login")
            : null;

        if (string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(repo))
            return ParseOutcome.Ignored("repository owner/name okunamadı");

        // Installation ID olmadan token üretilemez, yani API'ye hiç çıkamayız.
        // App olarak kurulmuş bir App'ten gelen her payload'da bu alan var; yoksa
        // istek büyük ihtimalle App'ten değil (ya da repo-level webhook kurulmuş).
        if (!root.TryGetProperty("installation", out var installation)
            || !installation.TryGetProperty("id", out var installationIdElement)
            || !installationIdElement.TryGetInt64(out var installationId))
        {
            return ParseOutcome.Ignored("payload'da installation.id yok (App webhook'u değil?)");
        }

        return ParseOutcome.Accepted(
            new AnalysisJob(deliveryId, owner!, repo!, runId, installationId));
    }

    private static string? GetString(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
