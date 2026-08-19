using System.Text;
using Octokit;

namespace CiAgent.Core;

/// <summary>
/// AnalysisResult'ı GitHub'a raporlar: önce PR yorumu, o yoksa commit yorumu,
/// her durumda ayrıca $GITHUB_STEP_SUMMARY dosyası.
///
/// Not: Constructor Octokit'in somut GitHubClient'ı yerine IGitHubClient
/// arayüzünü alıyor. GitHubClient zaten IGitHubClient'ı implemente ediyor,
/// yani çağıran taraf için hiçbir şey değişmiyor (GitHubService.Client
/// property'si aynen geçilebiliyor) — ama testlerde gerçek ağ çağrısı
/// yapmadan Moq ile sahte bir IGitHubClient verebiliyoruz.
/// </summary>
public class ReportService
{
    private readonly IGitHubClient _client;

    public ReportService(IGitHubClient client)
    {
        _client = client;
    }

    /// <summary>
    /// Ana giriş noktası. Adım 4 akışı:
    /// 1) headSha bir PR'a bağlıysa o PR'a yorum at/güncelle.
    /// 2) Değilse (örn. main'e direkt push) commit'e yorum at/güncelle.
    /// 3) Ne olursa olsun Job Summary'ye de yaz.
    /// PR/commit yorumu atarken oluşan hatalar (izin, erişim vb.) yutulur ve
    /// loglanır; süreç asla bu yüzden patlamaz, sadece Job Summary'ye düşer.
    /// </summary>
    public async Task ReportAsync(
        AnalysisResult result,
        ErrorContext context,
        string owner,
        string repo,
        string headSha,
        long runId)
    {
        var postedToGitHub = false;

        try
        {
            var prNumber = await FindPullRequestNumberAsync(owner, repo, headSha);

            if (prNumber is int number)
            {
                await UpsertPullRequestCommentAsync(owner, repo, number, runId, result, context);
            }
            else
            {
                await UpsertCommitCommentAsync(owner, repo, headSha, runId, result, context);
            }

            postedToGitHub = true;
        }
        catch (Exception ex)
        {
            // İzin yok / repo erişimi kısıtlı / ağ hatası — hiçbiri süreci durdurmamalı.
            Console.Error.WriteLine(
                $"[ReportService] PR/commit yorumu atılamadı, Job Summary'ye düşülüyor. Hata: {ex.Message}");
        }

        try
        {
            await WriteJobSummaryAsync(result, context, postedToGitHub);
        }
        catch (Exception ex)
        {
            // Job Summary bile yazılamazsa (örn. GITHUB_STEP_SUMMARY yok/izin yok)
            // yine de tüm agent süreci patlamamalı, sadece logla.
            Console.Error.WriteLine($"[ReportService] Job Summary yazılamadı. Hata: {ex.Message}");
        }
    }

    // --- PR bulma -----------------------------------------------------

    /// <summary>
    /// GET /repos/{owner}/{repo}/commits/{sha}/pulls (Octokit: Repository.Commit.PullRequests).
    /// workflow_run payload'ındaki pull_requests alanına bilerek bakmıyoruz: fork PR'larda
    /// GitHub bu alanı boş dönebiliyor, bu endpoint ise fork PR'lar dahil güvenilir çalışıyor.
    /// Birden fazla PR dönerse (rebase/force-push sonrası SHA birden fazla PR'da görünebilir)
    /// açık olanı tercih ediyoruz, yoksa ilkini.
    /// </summary>
    private async Task<int?> FindPullRequestNumberAsync(string owner, string repo, string headSha)
    {
        var pulls = await _client.Repository.Commit.PullRequests(owner, repo, headSha);

        if (pulls.Count == 0)
            return null;

        var open = pulls.FirstOrDefault(p => p.State == ItemState.Open);
        return null;
    }

    // --- PR yorumu (Issue Comment API) --------------------------------

    private async Task UpsertPullRequestCommentAsync(
        string owner, string repo, int prNumber, long runId,
        AnalysisResult result, ErrorContext context)
    {
        var marker = BuildMarker(runId);
        var body = BuildCommentBody(result, context, runId);

        // PR yorumları GitHub API'de "Issue Comment" olarak modellenir (PR = issue #).
        var existingComments = await _client.Issue.Comment.GetAllForIssue(owner, repo, prNumber);
        var match = FindByMarker(existingComments.Select(c => (c.Id, c.Body)), marker);

        if (match is long existingId)
            await _client.Issue.Comment.Update(owner, repo, existingId, body);
        else
            await _client.Issue.Comment.Create(owner, repo, prNumber, body);
    }

    // --- Commit yorumu (fallback) --------------------------------------

    private async Task UpsertCommitCommentAsync(
        string owner, string repo, string headSha, long runId,
        AnalysisResult result, ErrorContext context)
    {
        var marker = BuildMarker(runId);
        var body = BuildCommentBody(result, context, runId);

        var existingComments = await _client.Repository.Comment.GetAllForCommit(owner, repo, headSha);
        var match = FindByMarker(existingComments.Select(c => (c.Id, c.Body)), marker);

        if (match is long existingId)
            await _client.Repository.Comment.Update(owner, repo, existingId, body);
        else
            await _client.Repository.Comment.Create(owner, repo, headSha, new NewCommitComment(body));
    }

    /// <summary>
    /// Idempotency çekirdeği: mevcut yorumlar arasında, gövdesi run_id'ye özel
    /// marker ile BAŞLAYAN yorumu arar. Bulursa Id'sini döner (update edilecek),
    /// bulamazsa null (yeni yorum açılacak). Aynı run tekrar tetiklenirse eski
    /// yorum güncellenir; farklı run'lar birbirinin yorumunu asla ezmez çünkü
    /// marker run_id içeriyor.
    /// </summary>
    internal static long? FindByMarker(IEnumerable<(long Id, string Body)> comments, string marker)
    {
        foreach (var (id, body) in comments)
        {
            if (body is not null && body.TrimStart().StartsWith(marker, StringComparison.Ordinal))
                return id;
        }

        return null;
    }

    internal static string BuildMarker(long runId) => $"<!-- ci-agent:{runId} -->";

    // --- Markdown üretimi (saf, test edilebilir) ------------------------

    /// <summary>
    /// PR yorumu ve commit yorumu AYNI formatı paylaşır — bu yüzden tek yerde.
    /// İlk satır her zaman gizli marker: dedup mantığı buna bakıyor.
    /// </summary>
    internal static string BuildCommentBody(AnalysisResult result, ErrorContext context, long runId)
    {
        var sb = new StringBuilder();

        sb.AppendLine(BuildMarker(runId));
        sb.AppendLine("## 🤖 CiAgent — Otomatik Hata Analizi");
        sb.AppendLine();
        sb.AppendLine($"**Job:** `{context.JobName}`  ");
        sb.AppendLine($"**Başarısız adım:** `{context.FailedStepName}`  ");

        if (!string.IsNullOrWhiteSpace(context.FilePath))
            sb.AppendLine($"**Konum:** `{context.FilePath}:{context.LineNumber}`  ");

        sb.AppendLine($"**Güven düzeyi:** {ConfidenceBadge(result.Confidence)}");
        sb.AppendLine();

        sb.AppendLine("### 📋 Özet");
        sb.AppendLine(result.Summary);
        sb.AppendLine();

        sb.AppendLine("### 🔍 Kök Neden");
        sb.AppendLine(result.RootCause);
        sb.AppendLine();

        sb.AppendLine("### 🛠️ Önerilen Çözüm");
        sb.AppendLine(result.SuggestedFix);

        if (!string.IsNullOrWhiteSpace(result.AffectedFile))
        {
            sb.AppendLine();
            sb.AppendLine($"**Etkilenen dosya:** `{result.AffectedFile}{(result.AffectedLine is int l ? $":{l}" : "")}`");
        }

        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine($"<sub>Run ID: {runId} · Bu yorum CiAgent tarafından otomatik oluşturuldu; aynı run tekrar analiz edilirse bu yorum güncellenir.</sub>");

        return sb.ToString();
    }

    private static string ConfidenceBadge(string confidence) => confidence.ToLowerInvariant() switch
    {
        "high" => "🟢 Yüksek",
        "medium" => "🟡 Orta",
        "low" => "🔴 Düşük",
        _ => confidence
    };

    // --- Job Summary ----------------------------------------------------

    /// <summary>
    /// $GITHUB_STEP_SUMMARY dosyasına markdown APPEND eder (GitHub Actions bu
    /// dosyayı otomatik olarak job'ın Summary sekmesinde render eder). PR/commit
    /// yorumundan tamamen bağımsız çalışır — biri patlasa bile bu adım denenir.
    /// </summary>
    private static async Task WriteJobSummaryAsync(AnalysisResult result, ErrorContext context, bool postedToGitHub)
    {
        var summaryPath = Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY");

        if (string.IsNullOrWhiteSpace(summaryPath))
        {
            // Lokal çalıştırmada (CI dışında) bu env var yoktur, bu normal — sessizce atla.
            Console.WriteLine("[ReportService] GITHUB_STEP_SUMMARY tanımlı değil, job summary yazımı atlandı.");
            return;
        }

        var body = BuildJobSummaryBody(result, context, postedToGitHub);
        await File.AppendAllTextAsync(summaryPath, body, Encoding.UTF8);
    }

    /// <summary>Job Summary için PR/commit yorumundan daha sade bir format.</summary>
    internal static string BuildJobSummaryBody(AnalysisResult result, ErrorContext context, bool postedToGitHub)
    {
        var sb = new StringBuilder();

        sb.AppendLine("## 🤖 CiAgent Analiz Özeti");
        sb.AppendLine();
        sb.AppendLine($"- **Job:** `{context.JobName}`");
        sb.AppendLine($"- **Başarısız adım:** `{context.FailedStepName}`");
        sb.AppendLine($"- **Güven düzeyi:** {ConfidenceBadge(result.Confidence)}");

        if (!postedToGitHub)
            sb.AppendLine("- ⚠️ PR/commit yorumu atılamadı (izin veya erişim kısıtlı olabilir) — tek çıktı bu özet.");

        sb.AppendLine();
        sb.AppendLine($"**Özet:** {result.Summary}");
        sb.AppendLine();
        sb.AppendLine($"**Kök Neden:** {result.RootCause}");
        sb.AppendLine();
        sb.AppendLine($"**Önerilen Çözüm:** {result.SuggestedFix}");
        sb.AppendLine();

        return sb.ToString();
    }
}
