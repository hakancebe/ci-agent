using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Octokit;

namespace CiAgent.Core;

public enum PipelineStatus
{
    /// <summary>Analiz yapıldı ve GitHub'a raporlandı (normal akış).</summary>
    Reported,

    /// <summary>Analiz yapıldı ama --dry-run olduğu için GitHub'a HİÇBİR ŞEY yazılmadı.</summary>
    DryRun,

    /// <summary>Run'da hiç başarısız job yok — analiz edilecek bir şey yok.</summary>
    NoFailedJobs,

    /// <summary>Başarısız job var ama içinde analiz edilebilir bir adım/hata bulunamadı.</summary>
    NoAnalyzableFailure
}

public sealed record PipelineOutcome(
    PipelineStatus Status,
    ErrorContext? Context = null,
    AnalysisResult? Result = null);

/// <summary>
/// Agent'ın uçtan uca akışı: başarısız job'ları çek → ErrorContext üret → konumlu
/// failure'lara kod kesiti ekle → LLM'e analiz ettir → GitHub'a raporla.
///
/// Bu mantık eskiden Program.cs'te top-level script olarak duruyordu ve hiç test
/// edilemiyordu; Core'a taşınmasının sebebi bu. Program.cs artık yalnızca
/// yapılandırma bağlıyor ve buradaki sonucu exit code'a çeviriyor.
/// </summary>
public sealed class CiAnalysisPipeline
{
    private readonly IGitHubGateway _github;
    private readonly LlmService _llm;
    private readonly ReportService _report;
    private readonly ILogger _log;

    public CiAnalysisPipeline(
        IGitHubGateway github,
        LlmService llm,
        ReportService report,
        ILogger<CiAnalysisPipeline>? logger = null)
    {
        _github = github;
        _llm = llm;
        _report = report;
        _log = logger ?? NullLogger<CiAnalysisPipeline>.Instance;
    }

    /// <param name="dryRun">
    /// true ise GitHub'a HİÇBİR yazma yapılmaz (PR yorumu, commit yorumu, Job Summary
    /// yok) — analiz yapılır ve raporun tam metni loglanır. Azure OpenAI çağrısı yine
    /// de gider, yani ücret oluşur; dry-run "yazma yapma" demek, "hiçbir şey yapma" değil.
    /// </param>
    public async Task<PipelineOutcome> RunAsync(
        string owner, string repo, long runId, bool dryRun = false)
    {
        _log.LogInformation("Hedef: {Owner}/{Repo} run {RunId}", owner, repo, runId);

        if (dryRun)
            _log.LogInformation(
                "DRY-RUN: GitHub'a hiçbir yorum/özet yazılmayacak. "
                + "(Azure OpenAI çağrısı yine de yapılacak.)");

        var failedJobs = await GetFailedJobsAsync(owner, repo, runId);
        if (failedJobs.Count == 0)
        {
            _log.LogWarning("{Owner}/{Repo} run {RunId} için başarısız job bulunamadı.", owner, repo, runId);
            return new PipelineOutcome(PipelineStatus.NoFailedJobs);
        }

        var jobLogs = await FetchJobLogsAsync(owner, repo, failedJobs);
        var context = LogParser.BuildErrorContext(jobLogs);

        if (context is null)
        {
            _log.LogWarning(
                "Başarısız job'larda ({Jobs}) analiz edilebilir bir adım bulunamadı, ErrorContext üretilemedi.",
                string.Join(", ", failedJobs.Select(j => j.Name)));
            return new PipelineOutcome(PipelineStatus.NoAnalyzableFailure);
        }

        LogContext(context);

        // Tüm başarısız job'lar aynı commit'te (run tek bir SHA'ya bağlı) — kod çekme
        // ve raporlama için herhangi birinin HeadSha'sı yeterli.
        var headSha = failedJobs[0].HeadSha;

        await EnrichWithCodeSnippetsAsync(context, owner, repo, headSha);

        var result = await AnalyzeAsync(context);
        LogResult(result);

        if (dryRun)
        {
            // Atlanan adımın çıktısını göstermek dry-run'ın asıl faydası: raporun
            // gerçekte nasıl görüneceği, hiçbir şey yazmadan görülebiliyor.
            _log.LogInformation(
                "DRY-RUN: raporlama atlandı. Yazılacak olan yorum gövdesi:\n{Body}",
                ReportService.BuildCommentBody(result, context, runId));

            return new PipelineOutcome(PipelineStatus.DryRun, context, result);
        }

        _log.LogInformation("GitHub'a raporlanıyor...");
        await _report.ReportAsync(result, context, owner, repo, headSha, runId);
        _log.LogInformation("Raporlama tamamlandı.");

        return new PipelineOutcome(PipelineStatus.Reported, context, result);
    }

    // --- Adım 1-2: job/annotation/log çekme -----------------------------

    private async Task<List<WorkflowJob>> GetFailedJobsAsync(string owner, string repo, long runId)
    {
        var jobs = await _github.GetJobsAsync(owner, repo, runId);

        // Bir run'da birden fazla job fail olabilir (matrix, paralel job'lar).
        // Hepsi toplanıyor; BuildErrorContext tümünü tek ErrorContext'te birleştiriyor.
        var failed = jobs.Where(j => j.Conclusion?.StringValue == "failure").ToList();

        if (failed.Count > 0)
            _log.LogInformation("Başarısız job sayısı: {Count} ({Names})",
                failed.Count, string.Join(", ", failed.Select(j => j.Name)));

        return failed;
    }

    private async Task<List<LogParser.JobLog>> FetchJobLogsAsync(
        string owner, string repo, List<WorkflowJob> failedJobs)
    {
        var jobLogs = new List<LogParser.JobLog>();

        foreach (var job in failedJobs)
        {
            var annotations = await _github.GetAnnotationsAsync(owner, repo, job.Id);
            var log = await _github.DownloadJobLogAsync(owner, repo, job.Id);
            jobLogs.Add(new LogParser.JobLog(job, annotations, log));
        }

        return jobLogs;
    }

    // --- "Koda bakma" ---------------------------------------------------

    /// <summary>
    /// Konumu (dosya:satır) bilinen HER failure için ilgili dosyanın ±30 satırlık
    /// kesitini çekip o failure'a iliştirir. Konumsuz failure'lar (restore/deploy)
    /// atlanır. Aynı dosya birden fazla failure'da geçebildiği için indirilen
    /// içerik path bazında cache'lenir — matrix build'de aynı dosya defalarca istenir.
    /// </summary>
    private async Task EnrichWithCodeSnippetsAsync(
        ErrorContext context, string owner, string repo, string headSha)
    {
        var located = context.Failures.Where(f => f.IsLocated).ToList();
        if (located.Count == 0)
            return;

        _log.LogInformation("İlgili kod dosyaları çekiliyor ({Count} konumlu failure)...", located.Count);

        var cache = new Dictionary<string, string?>();

        foreach (var failure in located)
        {
            var path = failure.FilePath!;
            var line = failure.LineNumber!.Value;

            try
            {
                if (!cache.TryGetValue(path, out var content))
                {
                    content = await _github.GetFileContentAsync(owner, repo, path, headSha);
                    cache[path] = content;
                }

                if (content is not null)
                    failure.CodeSnippet = CodeSnippetExtractor.ExtractSnippet(content, line);
                else
                    _log.LogWarning("'{Path}' bulunamadı, bu failure kod kesiti olmadan gidecek.", path);
            }
            catch (Exception ex)
            {
                // Kod çekme başarısız olsa bile agent LLM analizine kod olmadan devam etmeli.
                _log.LogError(ex, "Kod çekilirken hata ({Path}:{Line}), kod kesiti olmadan devam ediliyor.", path, line);
            }
        }
    }

    // --- Adım 3: LLM analizi --------------------------------------------

    private async Task<AnalysisResult> AnalyzeAsync(ErrorContext context)
    {
        _log.LogInformation("Azure OpenAI'a istek atılıyor...");

        try
        {
            var result = await _llm.AnalyzeAsync(context);

            if (result is not null)
                return result;

            _log.LogError("LLM'den null döndü (deserialize başarısız olmuş olabilir).");
            return AnalysisResult.ForLlmFailure(
                new InvalidOperationException("Deserialize başarısız oldu ya da LLM boş içerik döndürdü."));
        }
        catch (Exception ex)
        {
            // LLM katmanındaki HERHANGİ bir hata (ağ, deployment adı yanlış, rate limit,
            // JSON schema uyuşmazlığı) süreci durdurmamalı — rapor akışı normal işlesin
            // ki durum sessizce kaybolmasın.
            _log.LogError(ex, "LLM analizi başarısız oldu, kök neden olmadan raporlanacak.");
            return AnalysisResult.ForLlmFailure(ex);
        }
    }

    // --- Teşhis çıktısı -------------------------------------------------

    private void LogContext(ErrorContext context)
    {
        var groups = FailureGrouper.Group(context.Failures);

        _log.LogInformation(
            "ErrorContext: job={Job}, adım={Step}, hata={Total} ({Distinct} farklı), "
            + "annotation={Annotations}, tümüKonumlu={AllLocated}",
            context.JobName, context.FailedStepName, context.Failures.Count, groups.Count,
            context.FilteredAnnotations.Count, context.AllFailuresLocated);

        foreach (var g in groups)
        {
            var f = g.Representative;
            var location = f.FilePath is not null
                ? $" @ {f.FilePath}{(f.LineNumber is int ln ? $":{ln}" : "")}"
                : "";
            var repeat = g.Occurrences > 1 ? $" [x{g.Occurrences}]" : "";
            _log.LogInformation("  - [{Kind}] {Label}{Location}{Repeat}",
                f.Kind, f.Name ?? f.JobName, location, repeat);
        }
    }

    private void LogResult(AnalysisResult result)
    {
        if (result.Skipped)
        {
            _log.LogWarning("Analiz ATLANDI: {Reason}", result.SkipReason);
            return;
        }

        if (result.ReductionNote is not null)
            _log.LogWarning("{Note}", result.ReductionNote);

        _log.LogInformation("Özet: {Summary}", result.Summary);
        _log.LogInformation("Kök neden sayısı: {Count}", result.Analyses.Count);

        for (var i = 0; i < result.Analyses.Count; i++)
        {
            var a = result.Analyses[i];
            _log.LogInformation("  [{Index}] {Title} ({Confidence}) — {RootCause} → {Fix}",
                i + 1, a.Title, a.Confidence, a.RootCause, a.SuggestedFix);
        }
    }
}
