using Octokit;

namespace CiAgent.Core;

/// <summary>
/// PR'a yorum atar; aynı marker'a sahip yorum varsa yenisini açmak yerine
/// onu günceller. Aynı /fix komutu tekrar çalıştırıldığında PR'ın altı
/// birbirinin aynı yorumlarla dolmasın diye.
/// </summary>
public sealed class PrCommenter
{
    private readonly IGitHubClient _client;

    public PrCommenter(IGitHubClient client) => _client = client;

    public async Task UpsertAsync(string owner, string repo, int prNumber, string marker, string body)
    {
        var existing = await _client.Issue.Comment.GetAllForIssue(owner, repo, prNumber);
        var match = ReportService.FindByMarker(existing.Select(c => (c.Id, c.Body)), marker);

        if (match is long id)
            await _client.Issue.Comment.Update(owner, repo, id, body);
        else
            await _client.Issue.Comment.Create(owner, repo, prNumber, body);
    }

    /// <summary>
    /// Komutu aldığımızı belli eden tepki. İnsan "çalışıyor mu acaba" diye
    /// beklemesin; /fix bir-iki dakika sürebiliyor.
    /// </summary>
    public async Task AcknowledgeAsync(string owner, string repo, long commentId)
    {
        try
        {
            await _client.Reaction.IssueComment.Create(
                owner, repo, commentId, new NewReaction(ReactionType.Eyes));
        }
        catch (Exception)
        {
            // Tepki koyamamak işi durdurmamalı - sadece bir nezaket jesti.
        }
    }
}
