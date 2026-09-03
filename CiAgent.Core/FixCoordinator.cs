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

        _log.LogInformation("/fix kabul edildi (dry-run={DryRun}).", command.DryRun);
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

        var runId = await _github.FindLatestFailedRunAsync(request.Owner, request.Repo, branch);
        if (runId is null)
        {
            var message = $"`{branch}` dalında başarısız bir CI run'ı bulunamadı — düzeltilecek bir hata yok.";
            _log.LogWarning("{Message}", message);
            await PostAsync(request, message);
            return new FixRunResult(FixRunStatus.NoFailedRun, Message: message);
        }

        // Analizi dry-run modunda çalıştırıyoruz: hata bağlamı ve kök neden
        // gerekli, ama ayrı bir analiz yorumu atılmasını istemiyoruz - /fix
        // zaten kendi yorumunu yazacak.
        var analysis = await _analysis.RunAsync(
            request.Owner, request.Repo, runId.Value, dryRun: true);

        if (analysis.Context is null || analysis.Result is null)
        {
            var message = $"Run {runId} analiz edilemedi ({analysis.Status}), düzeltme denenmedi.";
            _log.LogWarning("{Message}", message);
            await PostAsync(request, message);
            return new FixRunResult(FixRunStatus.NoFailedRun, Message: message);
        }

        var outcome = await _fix.RunAsync(
            analysis.Context, analysis.Result, workspaceRoot, command.DryRun);

        var pushed = false;
        if (outcome.Succeeded && !command.DryRun)
            pushed = await CommitAsync(workspaceRoot, outcome, branch);

        await _commenter.UpsertAsync(
            request.Owner, request.Repo, request.PullRequestNumber,
            FixReport.BuildMarker(request.CommentId),
            FixReport.BuildBody(outcome, command.DryRun, request.CommentId));

        return new FixRunResult(FixRunStatus.Completed, outcome, pushed);
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
