using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CiAgent.Core;

/// <summary>
/// /fix'in uçtan uca akışı: yorumu doğrula → düzeltilecek hatayı bul → analiz et
/// → düzeltmeyi dene → sonucu PR'a yaz (ve başarılıysa commit et).
///
/// Yetki ve komut kontrolü EN BAŞTA yapılıyor: yetkisiz bir yorum için tek bir
/// API çağrısı ya da LLM isteği bile harcanmıyor.
/// </summary>
public sealed class FixCoordinator
{
    private readonly GitHubService _github;
    private readonly CiAnalysisPipeline _analysis;
    private readonly FixPipeline _fix;
    private readonly PrCommenter _commenter;
    private readonly IWorkspaceProvider _workspace;
    private readonly ILogger _log;

    public FixCoordinator(
        GitHubService github,
        CiAnalysisPipeline analysis,
        FixPipeline fix,
        PrCommenter commenter,
        IWorkspaceProvider workspace,
        ILogger<FixCoordinator>? logger = null)
    {
        _github = github;
        _analysis = analysis;
        _fix = fix;
        _commenter = commenter;
        _workspace = workspace;
        _log = logger ?? NullLogger<FixCoordinator>.Instance;
    }

    public async Task<FixRunResult> RunAsync(FixRequest request)
    {
        try
        {
            return await RunCoreAsync(request);
        }
        finally
        {
            // Klonlanan dizin her durumda siliniyor - düzeltme başarısız olsa,
            // exception fırlasa bile. İçinde token'lı bir .git/config var.
            _workspace.Cleanup();
        }
    }

    private async Task<FixRunResult> RunCoreAsync(FixRequest request)
    {
        var command = FixCommand.TryParse(request.CommentBody);
        if (command is null)
        {
            _log.LogInformation("Yorum bir /fix komutu değil, çıkılıyor.");
            return new FixRunResult(FixRunStatus.NotACommand);
        }

        if (!FixAuthorization.CanRunFix(request.AuthorAssociation))
        {
            // Sebebi PR'a yazmıyoruz: yetkisiz birine agent'ın varlığını ve
            // tetikleme koşullarını anlatmak gereksiz.
            _log.LogWarning(
                "/fix reddedildi: yorumu yazanın yazma yetkisi yok (author_association={Assoc}).",
                request.AuthorAssociation);
            return new FixRunResult(FixRunStatus.NotAuthorized);
        }

        _log.LogInformation("/fix kabul edildi (commit={Commit}).", command.Commit);
        await _commenter.AcknowledgeAsync(request.Owner, request.Repo, request.CommentId);

        var pr = await _github.GetPullRequestInfoAsync(
            request.Owner, request.Repo, request.PullRequestNumber);

        // Fork kapısı. Bu kural eskiden SADECE ci-agent-fix.yml'de vardı; agent
        // Actions'tan çıkıp webhook'a taşınınca kaybolacaktı. Burada olmasının
        // sebebi ekonomi kadar dürüstlük de: fork'un dalına push edemeyeceğimizi
        // BAŞTA biliyoruz, o yüzden analiz edip LLM'e para ödeyip en sonda 403
        // almak yerine hemen ve anlaşılır şekilde duruyoruz.
        if (pr.IsFork)
        {
            const string forkMessage =
                "Bu PR bir fork'tan geldiği için otomatik düzeltme yapılamıyor: agent'ın "
                + "token'ı yalnızca kurulu olduğu repolar için geçerli, katkıcının fork'una "
                + "push edemiyor.\n\nAnaliz yorumundaki önerilen çözümü elle uygulayabilirsiniz.";

            _log.LogWarning("/fix reddedildi: PR fork'tan geliyor ({Branch}).", pr.Branch);
            await PostAsync(request, forkMessage);
            return new FixRunResult(FixRunStatus.ForkNotSupported, Message: forkMessage);
        }

        var branch = pr.Branch;

        // Kod ancak BURADA hazırlanıyor: komut geçerli, yazan yetkili ve PR
        // push edilebilir bir dalda. Daha erken klonlamak, yetkisiz bir yorumun
        // bile repo indirtmesi demek olurdu.
        var workspaceRoot = await _workspace.PrepareAsync(request.Owner, request.Repo, branch);
        if (workspaceRoot is null)
        {
            var message = $"`{branch}` dalı çalışma dizinine hazırlanamadı (klonlama başarısız).";
            _log.LogError("{Message}", message);
            await PostAsync(request, message);
            return new FixRunResult(FixRunStatus.WorkspaceUnavailable, Message: message);
        }

        // Önce PR'daki analiz yorumunun işaret ettiği run'a bakıyoruz, sonra
        // dal aramasına düşüyoruz. İki sebep:
        //
        // 1) CD hataları dal aramasına HİÇ düşmüyor: workflow_run ile
        //    tetiklenen çalışmalar varsayılan dala yazılıyor, PR'ın dalına
        //    değil. Canlıda ölçüldü (pilot PR #16): CD patlamışken /fix
        //    "bu dalda başarısız run yok" dedi.
        // 2) Semantik olarak da doğrusu bu: /fix, insanın PR'da GÖRDÜĞÜ
        //    analizin üzerinde çalışmalı, ayrı bir arama sonucunun değil.
        var runId = await FindRunFromAnalysisCommentAsync(request)
                    ?? await _github.FindLatestFailedRunAsync(request.Owner, request.Repo, branch);

        if (runId is null)
        {
            var message = $"Bu PR'da analiz yorumu yok ve `{branch}` dalında başarısız bir "
                        + "run bulunamadı — düzeltilecek bir hata yok.";
            _log.LogWarning("{Message}", message);
            await PostAsync(request, message);
            return new FixRunResult(FixRunStatus.NoFailedRun, Message: message);
        }

        // Analiz yorumuna gömülü sonucu okumayı dene. Bulursak LLM'e hiç
        // gitmiyoruz ve — asıl mesele — kullanıcının yorumda GÖRDÜĞÜ kararla
        // (rozet dahil) birebir aynı sonucu kullanıyoruz. Eskiden /fix kendi
        // analizini koşturduğu için ikisi ayrışabiliyordu: canlıda bir turda
        // rozet çıkmamışken /fix yine de "düzeltilemez" demişti.
        var stored = await LoadStoredAnalysisAsync(request, runId.Value);

        // Bağlam (loglar, kod kesitleri, test edilen kod) her hâlükârda yeniden
        // kuruluyor — o yoruma gömülmüyor ve düzeltme için şart.
        var analysis = await _analysis.RunAsync(
            request.Owner, request.Repo, runId.Value, dryRun: true, precomputed: stored);

        if (analysis.Context is null || analysis.Result is null)
        {
            var message = $"Run {runId} analiz edilemedi ({analysis.Status}), düzeltme denenmedi.";
            _log.LogWarning("{Message}", message);
            await PostAsync(request, message);
            return new FixRunResult(FixRunStatus.NoFailedRun, Message: message);
        }

        // dryRun burada "commit etme" demek: öneri modunda değişiklik uygulanıp
        // doğrulanıyor, sonra çalışma dizini temiz bırakılıyor. Yamanın kendisi
        // FixOutcome.Edits içinde taşındığı için rapor diff'i yine gösterebiliyor.
        var outcome = await _fix.RunAsync(
            analysis.Context, analysis.Result, workspaceRoot, dryRun: !command.Commit);

        // Commit artık VARSAYILAN DEĞİL, açık istek. Gerekçesi FixCommand'in
        // dokümantasyonunda: doğrulama döngüsü düzeltmenin doğruluğunu değil
        // yalnızca derlenip testleri geçtiğini gösteriyor.
        var pushed = false;
        if (outcome.Succeeded && command.Commit)
            pushed = await CommitAsync(workspaceRoot, outcome, branch);

        await _commenter.UpsertAsync(
            request.Owner, request.Repo, request.PullRequestNumber,
            FixReport.BuildMarker(request.CommentId),
            FixReport.BuildBody(outcome, committed: pushed, request.CommentId));

        return new FixRunResult(FixRunStatus.Completed, outcome, pushed);
    }

    /// <summary>
    /// Analiz yorumundaki gizli veri bloğundan sonucu okur; yoksa null döner ve
    /// analiz baştan yapılır.
    ///
    /// Başarısızlık sessiz ve zararsız: yorum silinmiş, elle düzenlenmiş ya da
    /// bu bloğu taşımayan eski bir sürümde yazılmış olabilir. Hepsinin doğru
    /// cevabı aynı — "veri yok, analizi kendin yap".
    /// </summary>
    /// <summary>
    /// PR'daki en yeni analiz yorumunun run'ı. Okunamazsa null döner ve
    /// çağıran taraf dal aramasına düşer — bu arama bir kolaylık, zorunluluk
    /// değil.
    /// </summary>
    private async Task<long?> FindRunFromAnalysisCommentAsync(FixRequest request)
    {
        try
        {
            var runId = await _commenter.FindLatestAnalysisRunIdAsync(
                request.Owner, request.Repo, request.PullRequestNumber);

            if (runId is long id)
                _log.LogInformation("PR'daki analiz yorumu run {RunId}'i işaret ediyor.", id);

            return runId;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "PR yorumlarından run id okunamadı; dal araması yapılacak.");
            return null;
        }
    }

    private async Task<AnalysisResult?> LoadStoredAnalysisAsync(FixRequest request, long runId)
    {
        try
        {
            var body = await _commenter.FindBodyByMarkerAsync(
                request.Owner, request.Repo, request.PullRequestNumber,
                ReportService.BuildMarker(runId));

            var stored = AnalysisPayload.TryDecode(body);

            _log.LogInformation(stored is not null
                ? "Analiz yorumundaki sonuç okundu; LLM analizi tekrarlanmayacak."
                : "Analiz yorumunda gömülü sonuç bulunamadı; analiz baştan yapılacak.");

            return stored;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Gömülü analiz sonucu okunamadı; analiz baştan yapılacak.");
            return null;
        }
    }

    private async Task<bool> CommitAsync(string workspaceRoot, FixOutcome outcome, string branch)
    {
        var git = new GitWorkspace(workspaceRoot, _log);
        await git.ConfigureIdentityAsync("ci-agent[bot]", "ci-agent[bot]@users.noreply.github.com");

        var files = outcome.AppliedEdits.Select(e => e.File).Distinct().ToList();

        var message =
            $"fix: {outcome.Summary}\n\n"
            + "CiAgent tarafından /fix komutuyla otomatik uygulandı.\n"
            + $"Değiştirilen dosyalar: {string.Join(", ", files)}\n"
            + "Derleme ve testler bu commit ile geçiyor.";

        return await git.CommitAndPushAsync(files, message, branch);
    }

    private Task PostAsync(FixRequest request, string message)
    {
        var body = $"{FixReport.BuildMarker(request.CommentId)}\n## ⚠️ CiAgent — /fix\n\n{message}\n";
        return _commenter.UpsertAsync(
            request.Owner, request.Repo, request.PullRequestNumber,
            FixReport.BuildMarker(request.CommentId), body);
    }
}
