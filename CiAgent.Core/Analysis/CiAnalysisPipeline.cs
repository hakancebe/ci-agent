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
    /// <summary>Test edilen koddan bağlama en fazla kaç dosya eklenir.</summary>
    private const int MaxTestSubjectFiles = 3;

    /// <summary>Bu dosyaların toplam boyut sınırı (karakter).</summary>
    private const int MaxTestSubjectChars = 12_000;

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
    /// <param name="precomputed">
    /// Daha önce yapılmış analiz sonucu. Verilirse LLM'e HİÇ gidilmez, bu sonuç
    /// kullanılır. /fix bunu analiz yorumuna gömülü veriden okuyor: aynı soruyu
    /// ikinci kez sormak hem para harcıyor hem de — fixable kararı kararsız
    /// olduğu için — rozetle /fix'in kararının ayrışmasına yol açıyordu.
    /// </param>
    public async Task<PipelineOutcome> RunAsync(
        string owner, string repo, long runId, bool dryRun = false,
        AnalysisResult? precomputed = null)
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

        // Hangi workflow dosyasından geldiği: yapılandırma/deploy hatalarında
        // modelin işaret edeceği dosya bu. Tek ekstra API çağrısı; başarısız
        // olursa null kalır ve analiz eskisi gibi devam eder.
        context.Workflow = await _github.GetWorkflowInfoAsync(owner, repo, runId);

        LogContext(context);

        // Tüm başarısız job'lar aynı commit'te (run tek bir SHA'ya bağlı) — kod çekme
        // ve raporlama için herhangi birinin HeadSha'sı yeterli.
        //
        // AMA: workflow_run ile tetiklenen çalışmalarda run'ın head_sha'sı
        // varsayılan dalı gösteriyor, job gerçekte başka bir commit'i derlemiş
        // olsa bile. Log'dan okunabiliyorsa gerçek commit'i tercih ediyoruz;
        // aksi halde rapor alakasız bir commit'e düşüyor (bkz.
        // LogParser.ExtractCheckedOutSha).
        var reportedSha = failedJobs[0].HeadSha;
        var headSha = LogParser.ExtractCheckedOutSha(jobLogs[0].RawLog) ?? reportedSha;

        await AddWorkflowFileAsync(context, owner, repo, reportedSha);

        if (!string.Equals(headSha, reportedSha, StringComparison.OrdinalIgnoreCase))
            _log.LogInformation(
                "Run'ın bildirdiği commit {Reported} ama job {Actual} commit'ini checkout etmiş; "
                + "rapor gerçek commit'e yazılacak.",
                reportedSha, headSha);

        // Önbellek İKİ adım arasında paylaşılıyor: kesit çıkarma ile "test edilen
        // kodu getir" adımı sık sık aynı dosyayı ister ve iki kez indirmek boşuna
        // API çağrısı olurdu.
        var contentCache = new Dictionary<string, string?>();
        await EnrichWithCodeSnippetsAsync(context, owner, repo, headSha, contentCache);
        await EnrichWithTestSubjectsAsync(context, owner, repo, headSha, contentCache);

        AnalysisResult result;
        if (precomputed is not null)
        {
            _log.LogInformation("Hazır analiz sonucu kullanılıyor, LLM'e gidilmiyor.");
            result = precomputed;
        }
        else
        {
            result = await AnalyzeAsync(context);
        }

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

    /// <summary>
    /// Workflow dosyasının içeriğini prompt'a hazırlar — yalnızca hiçbir failure'ın
    /// konumu bilinmiyorsa.
    ///
    /// Konumsuz hata demek, pratikte "yapılandırma/ortam hatası" demek: derleyici de
    /// test de dosya:satır verir, `az login` vermez. O sınıfta model elindeki tek
    /// somut dosya olarak workflow dosyasını gösteriyor, ama içeriğini görmediği için
    /// satır numarasını uyduruyordu (ölçüldü, pilot CD run 34194933554).
    ///
    /// Commit olarak run'ın bildirdiği SHA kullanılıyor, log'dan okunan değil:
    /// workflow dosyası her zaman run'ı BAŞLATAN commit'ten gelir.
    ///
    /// Başarısız olursa sessizce atlanıyor; analiz eskisi gibi devam eder.
    /// </summary>
    private async Task AddWorkflowFileAsync(
        ErrorContext context, string owner, string repo, string? sha)
    {
        if (context.AllFailuresLocated || sha is null)
            return;

        if (context.Workflow?.Path is not { Length: > 0 } path)
            return;

        try
        {
            context.WorkflowFileContent = await _github.GetFileContentAsync(owner, repo, path, sha);

            if (context.WorkflowFileContent is null)
                _log.LogWarning("Workflow dosyası '{Path}' çekilemedi, prompt'a eklenmeyecek.", path);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Workflow dosyası çekilirken hata ({Path}), onsuz devam ediliyor.", path);
        }
    }

    private async Task<List<WorkflowJob>> GetFailedJobsAsync(string owner, string repo, long runId)
    {
        var jobs = await _github.GetJobsAsync(owner, repo, runId);

        // Bir run'da birden fazla job fail olabilir (matrix, paralel job'lar).
        // Hepsi toplanıyor; BuildErrorContext tümünü tek ErrorContext'te birleştiriyor.
        var failed = jobs.Where(j => j.Conclusion?.StringValue == "failure").ToList();

        // Zaman aşımına uğrayan job "failure" değil "cancelled" dönüyor —
        // canlıda ölçüldü (pilot CD run 34127272978): timeout-minutes ile
        // biten job conclusion=cancelled aldı ve yukarıdaki filtreye hiç
        // düşmedi. Takılan bir deploy agent'ın gözünde YOK demekti.
        //
        // Ama her cancelled job analiz edilmemeli: matrix'te bir job patlayınca
        // kardeşleri de iptal ediliyor ve onlar sebep değil, SONUÇ. Bu yüzden
        // iptal edilenlere yalnızca ORTADA BAŞKA BAŞARISIZ JOB YOKKEN
        // bakılıyor — yani "tek olan biten şey bir takılmaydı" durumunda.
        //
        // Kullanıcının elle iptal ettiği run bu yola hiç girmiyor: o run
        // conclusion=cancelled ile biter, agent yalnızca failure run'larda
        // uyanır (bkz. WebhookParser).
        if (failed.Count == 0)
        {
            failed = jobs
                .Where(j => j.Conclusion?.StringValue is "cancelled" or "timed_out")
                .ToList();

            if (failed.Count > 0)
                _log.LogInformation(
                    "Başarısız job yok ama {Count} job iptal/zaman aşımı ile bitmiş; "
                    + "takılma ihtimaline karşı bunlar analiz edilecek ({Names}).",
                    failed.Count, string.Join(", ", failed.Select(j => j.Name)));
        }

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
        ErrorContext context, string owner, string repo, string headSha,
        Dictionary<string, string?> cache)
    {
        var located = context.Failures.Where(f => f.IsLocated).ToList();
        if (located.Count == 0)
            return;

        _log.LogInformation("İlgili kod dosyaları çekiliyor ({Count} konumlu failure)...", located.Count);

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

    /// <summary>
    /// Patlayan testin test ETTİĞİ uygulama dosyasını bulup bağlama ekler.
    ///
    /// Bu adım olmadan test hataları pratikte düzeltilemiyordu: hata konumu testi
    /// gösterdiği için modele yalnızca test kodu gidiyor, bozuk metot hiç
    /// görünmüyordu. Model de dürüstçe "göremediğim şey hakkında tahmin
    /// yürütmem" deyip düzeltmeyi reddediyordu.
    ///
    /// Başarısızlık sessiz: dosya bulunamazsa ya da ağaç çekilemezse analiz
    /// eskisi gibi (eksik bağlamla) devam eder — bu bir zenginleştirme, ön koşul
    /// değil.
    /// </summary>
    private async Task EnrichWithTestSubjectsAsync(
        ErrorContext context, string owner, string repo, string headSha,
        Dictionary<string, string?> cache)
    {
        var subjects = context.Failures
            .Where(f => f.Kind == FailureKind.Test)
            .Select(f => TestSubjectResolver.SubjectTypeName(f.Name))
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => n!)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var subjectFiles = ResolveSubjectFilesFromLog(context);

        if (subjects.Count == 0 && subjectFiles.Count == 0)
            return;

        IReadOnlyList<string> paths;
        try
        {
            paths = await _github.ListFilePathsAsync(owner, repo, headSha);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Repo dosya listesi alınamadı; test edilen kod bağlama eklenemedi.");
            return;
        }

        var totalChars = 0;

        var matches = subjects
            .SelectMany(subject => TestSubjectResolver.MatchSourceFiles(subject, paths))
            .Concat(subjectFiles.SelectMany(
                file => TestSubjectResolver.MatchSourceFilesByName(file, paths)))
            .Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var path in matches)
        {
            if (context.RelatedSources.ContainsKey(path))
                continue;

            if (context.RelatedSources.Count >= MaxTestSubjectFiles)
                return;

            try
            {
                if (!cache.TryGetValue(path, out var content))
                {
                    content = await _github.GetFileContentAsync(owner, repo, path, headSha);
                    cache[path] = content;
                }

                if (content is null)
                    continue;

                // Tek bir devasa dosya prompt bütçesini yiyebilir. Sınırı aşan
                // dosyayı EKLEMEMEK, kırpıp eklemekten iyi: kırpılmış içerikte
                // /fix'in oldText eşleşmesi tutmaz.
                if (totalChars + content.Length > MaxTestSubjectChars)
                {
                    _log.LogInformation(
                        "'{Path}' bağlama eklenmedi: test edilen kod için boyut sınırı aşılıyor.", path);
                    continue;
                }

                context.RelatedSources[path] = content;
                totalChars += content.Length;

                _log.LogInformation("Test edilen kod bağlama eklendi: {Path}.", path);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Test edilen kod çekilemedi ({Path}), bu dosya olmadan devam ediliyor.", path);
            }
        }
    }

    /// <summary>
    /// Ham logda geçen test dosyası adlarından, test edilen kaynak dosyaların
    /// adlarını çıkarır. <see cref="TestSubjectResolver.SubjectTypeName"/>
    /// yolunun .NET dışındaki karşılığı.
    ///
    /// Neden ayrı bir yol gerekti: .NET'te ayrıştırıcı testin ADINI çıkarıyor
    /// ("CalculatorTests.Add") ve köprü oradan kuruluyor. Diğer dillerde
    /// ayrıştırıcı adı çıkaramıyor — failure Generic olarak geliyor, Name null.
    /// Elimizde kalan tek ipucu logda geçen dosya adları.
    ///
    /// Canlıda ölçüldü (pilot run 34196510936): Python'da agent doğru teşhis
    /// koydu ama hatayı test dosyasında gösterdi ve "add fonksiyonunun kodu
    /// verilmediği için düzeltme önerilemiyor" dedi.
    /// </summary>
    private static List<string> ResolveSubjectFilesFromLog(ErrorContext context)
    {
        var logs = context.Failures
            .Select(f => f.RawEvidence)
            .Append(context.RawStepLog)
            .Where(t => !string.IsNullOrWhiteSpace(t));

        return logs
            .SelectMany(log => LogParser.ExtractSourceFileNames(log))
            .Select(TestSubjectResolver.SubjectFileName)
            .Where(n => n is not null)
            .Select(n => n!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
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
