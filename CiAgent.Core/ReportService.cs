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
    // virtual: CiAnalysisPipeline testleri GitHub'a hiç dokunmadan "raporlandı mı"
    // sorusunu doğrulayabilsin diye override edilebiliyor.
    public virtual async Task ReportAsync(
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
    ///
    /// Bu endpoint headSha'yı sadece PR'ın HEAD'i olduğu için değil, PR'ın commit
    /// geçmişinde bir ATA (ancestor) olarak geçtiği için de eşleştirebiliyor (örn.
    /// stacked PR'lar, ya da rebase/force-push sonrası eski SHA'nın hâlâ başka bir
    /// branch/PR'ın geçmişinde yer alması). Bu yüzden önce headSha'nın gerçekten HEAD'i
    /// olduğu PR'ları tercih ediyoruz; hiçbiri tam eşleşmiyorsa tüm adaylara bakıyoruz.
    /// Birden fazla açık aday kalırsa (örn. iki branch aynı commit'i paylaşıyor), en son
    /// güncellenen PR'ı seçerek deterministik bir sonuç garanti ediyoruz — API'nin
    /// döndürdüğü sıraya (garantisi olmayan) güvenmiyoruz.
    /// </summary>
    private async Task<int?> FindPullRequestNumberAsync(string owner, string repo, string headSha)
    {
        var pulls = await _client.Repository.Commit.PullRequests(owner, repo, headSha);

        if (pulls.Count == 0)
            return null;

        var exactHeadMatches = pulls.Where(p => p.Head?.Sha == headSha).ToList();
        var candidates = exactHeadMatches.Count > 0 ? exactHeadMatches : pulls;

        var open = candidates
            .Where(p => p.State == ItemState.Open)
            .OrderByDescending(p => p.UpdatedAt)
            .FirstOrDefault();

        return open?.Number;
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

        if (result.Skipped)
        {
            sb.AppendLine("## ⏭️ CiAgent — Otomatik Analiz Atlandı");
            sb.AppendLine();
            sb.AppendLine($"**Job:** `{context.JobName}`  ");
            sb.AppendLine($"**Başarısız adım:** `{context.FailedStepName}`  ");
            sb.AppendLine();
            sb.AppendLine($"> {result.SkipReason}");
            sb.AppendLine();
            sb.AppendLine("**Elle inceleme gerekiyor** — bu run için LLM analizi çalıştırılmadı, ");
            sb.AppendLine("aşağıdaki yorumlar otomatik bir kök neden tespiti içermiyor.");
            sb.AppendLine();
            sb.AppendLine("---");
            sb.AppendLine($"<sub>Run ID: {runId} · Bu yorum CiAgent tarafından otomatik oluşturuldu; aynı run tekrar analiz edilirse bu yorum güncellenir.</sub>");

            return sb.ToString();
        }

        sb.AppendLine("## 🤖 CiAgent — Otomatik Hata Analizi");
        sb.AppendLine();
        sb.AppendLine($"**Job:** `{context.JobName}`  ");
        sb.AppendLine($"**Başarısız adım:** `{context.FailedStepName}`  ");

        var groups = FailureGrouper.Group(context.Failures);
        if (groups.Count > 0)
            sb.AppendLine($"**Tespit edilen hata:** {FailureCountLabel(groups)}  ");

        sb.AppendLine();

        // Analiz eksik veriyle yapıldıysa bunu okuyucuya en başta söylüyoruz -
        // aşağıdaki kök nedenler "tüm kanıta bakılarak" bulunmuş izlenimi vermesin.
        if (!string.IsNullOrWhiteSpace(result.ReductionNote))
        {
            sb.AppendLine($"> ⚠️ {result.ReductionNote}");
            sb.AppendLine();
        }

        sb.AppendLine("### 📋 Özet");
        sb.AppendLine(result.Summary);
        sb.AppendLine();

        // Kök nedenler: tek analiz varsa başlıksız tek bölüm, birden fazlaysa
        // numaralı bölümler. LLM 5 hatayı tek kök nedene bağladıysa okuyucu tek
        // bölüm görür - bu, gruplamanın işe yaradığının da göstergesi.
        for (var i = 0; i < result.Analyses.Count; i++)
        {
            var a = result.Analyses[i];
            var heading = result.Analyses.Count == 1
                ? "### 🔍 Kök Neden"
                : $"### 🔍 Kök Neden {i + 1}/{result.Analyses.Count} — {a.Title}";

            sb.AppendLine(heading);
            sb.AppendLine($"**Güven düzeyi:** {ConfidenceBadge(a.Confidence)}");
            sb.AppendLine();
            sb.AppendLine(a.RootCause);
            sb.AppendLine();

            sb.AppendLine("**🛠️ Önerilen Çözüm**");
            sb.AppendLine(a.SuggestedFix);

            // fixable=false ise bunu BURADA söylüyoruz: bilgi zaten üretiliyordu
            // ama yalnızca /fix çalıştırıldığında ortaya çıkıyordu. Okuyucu boşuna
            // /fix yazmadan önce bilmeli. Güven düzeyinden ayrı bir bilgi:
            // teşhis kesin olabilir (🟢) ama düzeltme yine de çıkarılamayabilir.
            if (!a.Fixable)
            {
                sb.AppendLine();
                sb.AppendLine("> 🔒 **Otomatik düzeltilemez** — doğru düzeltme koddan "
                            + "belirlenemiyor, `/fix` bu hatayı denemez. Elle bakılması gerekiyor.");
            }

            if (!string.IsNullOrWhiteSpace(a.AffectedFile))
            {
                sb.AppendLine();
                sb.AppendLine($"**Etkilenen dosya:** `{a.AffectedFile}{(a.AffectedLine is int l ? $":{l}" : "")}`");
            }

            sb.AppendLine();
        }

        // Hataların tam listesi katlanmış halde: analiz kısa kalsın ama hiçbir
        // failure raporda tamamen görünmez olmasın.
        if (groups.Count > 0)
            sb.Append(BuildFailureDetails(groups));

        sb.AppendLine("---");
        sb.AppendLine($"<sub>Run ID: {runId} · Bu yorum CiAgent tarafından otomatik oluşturuldu; aynı run tekrar analiz edilirse bu yorum güncellenir.</sub>");

        return sb.ToString();
    }

    internal static string FailureCountLabel(List<FailureGroup> groups)
    {
        var total = groups.Sum(g => g.Occurrences);
        return total == groups.Count
            ? $"{groups.Count} hata"
            : $"{groups.Count} farklı hata ({total} tekrar)";
    }

    /// <summary>
    /// Tüm hata gruplarını &lt;details&gt; içinde listeler. Yorum gövdesi şişmesin diye
    /// katlanmış; ama LLM bir hatayı analizde atlasa bile ham kayıt raporda kalır.
    /// </summary>
    private static string BuildFailureDetails(List<FailureGroup> groups)
    {
        var sb = new StringBuilder();

        sb.AppendLine("<details>");
        sb.AppendLine($"<summary>Tespit edilen tüm hatalar ({groups.Count})</summary>");
        sb.AppendLine();

        foreach (var g in groups)
        {
            var f = g.Representative;
            var label = g.Names.Count > 0 ? string.Join(", ", g.Names) : f.Kind.ToString();
            // Restore hatalarında satır no yok - "csproj:" gibi sarkan iki nokta olmasın.
            var location = f.FilePath is not null
                ? $" — `{f.FilePath}{(f.LineNumber is int ln ? $":{ln}" : "")}`"
                : "";
            var repeat = g.Occurrences > 1
                ? $" _(aynı hata {g.Occurrences} kez: {string.Join(", ", g.JobNames)})_"
                : "";

            sb.AppendLine($"- **{label}**{location}{repeat}");
            // Mesaj tek satıra sıkıştırılıyor: çok satırlı assert çıktıları liste
            // yapısını bozmasın.
            sb.AppendLine($"  `{OneLine(f.Message)}`");
        }

        sb.AppendLine();
        sb.AppendLine("</details>");
        sb.AppendLine();

        return sb.ToString();
    }

    private static string OneLine(string message)
    {
        var collapsed = string.Join(" ", message.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                                                .Select(l => l.Trim()));
        return collapsed.Length > 300 ? collapsed[..300] + "…" : collapsed;
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

        if (result.Skipped)
        {
            sb.AppendLine("## ⏭️ CiAgent — Otomatik Analiz Atlandı");
            sb.AppendLine();
            sb.AppendLine($"- **Job:** `{context.JobName}`");
            sb.AppendLine($"- **Başarısız adım:** `{context.FailedStepName}`");
            sb.AppendLine($"- **Sebep:** {result.SkipReason}");

            if (!postedToGitHub)
                sb.AppendLine("- ⚠️ PR/commit yorumu atılamadı (izin veya erişim kısıtlı olabilir) — tek çıktı bu özet.");

            sb.AppendLine();
            sb.AppendLine("**Elle inceleme gerekiyor** — bu run için LLM analizi çalıştırılmadı.");
            sb.AppendLine();

            return sb.ToString();
        }

        var groups = FailureGrouper.Group(context.Failures);

        sb.AppendLine("## 🤖 CiAgent Analiz Özeti");
        sb.AppendLine();
        sb.AppendLine($"- **Job:** `{context.JobName}`");
        sb.AppendLine($"- **Başarısız adım:** `{context.FailedStepName}`");

        if (groups.Count > 0)
            sb.AppendLine($"- **Tespit edilen hata:** {FailureCountLabel(groups)}");

        if (!postedToGitHub)
            sb.AppendLine("- ⚠️ PR/commit yorumu atılamadı (izin veya erişim kısıtlı olabilir) — tek çıktı bu özet.");

        if (!string.IsNullOrWhiteSpace(result.ReductionNote))
            sb.AppendLine($"- ⚠️ {result.ReductionNote}");

        sb.AppendLine();
        sb.AppendLine($"**Özet:** {result.Summary}");
        sb.AppendLine();

        foreach (var a in result.Analyses)
        {
            var heading = result.Analyses.Count == 1 ? "Kök Neden" : $"Kök Neden — {a.Title}";
            sb.AppendLine($"**{heading}** ({ConfidenceBadge(a.Confidence)}): {a.RootCause}");
            sb.AppendLine();
            sb.AppendLine($"**Önerilen Çözüm:** {a.SuggestedFix}");

            if (!a.Fixable)
                sb.AppendLine("> 🔒 **Otomatik düzeltilemez** — `/fix` bu hatayı denemez.");

            sb.AppendLine();
        }

        return sb.ToString();
    }
}
