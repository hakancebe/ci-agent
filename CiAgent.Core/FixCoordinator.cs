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
    private readonly ILogger _log;

    public FixCoordinator(
        GitHubService github,
        CiAnalysisPipeline analysis,
        FixPipeline fix,
        PrCommenter commenter,
        ILogger<FixCoordinator>? logger = null)
    {
        _github = github;
        _analysis = analysis;
        _fix = fix;
        _commenter = commenter;
        _log = logger ?? NullLogger<FixCoordinator>.Instance;
    }

    public async Task<FixRunResult> RunAsync(FixRequest request)
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

        var (branch, _) = await _github.GetPullRequestHeadAsync(
            request.Owner, request.Repo, request.PullRequestNumber);

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
            analysis.Context, analysis.Result, request.WorkspaceRoot, command.DryRun);

        var pushed = false;
        if (outcome.Succeeded && !command.DryRun)
            pushed = await CommitAsync(request, outcome, branch);

        await _commenter.UpsertAsync(
            request.Owner, request.Repo, request.PullRequestNumber,
            FixReport.BuildMarker(request.CommentId),
            FixReport.BuildBody(outcome, command.DryRun, request.CommentId));

        return new FixRunResult(FixRunStatus.Completed, outcome, pushed);
    }

    private async Task<bool> CommitAsync(FixRequest request, FixOutcome outcome, string branch)
    {
        var git = new GitWorkspace(request.WorkspaceRoot, _log);
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
