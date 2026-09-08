using Octokit;

namespace CiAgent.Core;

/// <summary>
/// /fix'in bir PR hakkında bilmesi gereken her şey.
/// </summary>
public sealed record PullRequestInfo(string Branch, string HeadSha, bool IsFork)
{
    /// <summary>
    /// Octokit'in PR nesnesinden üretir. Fork tespiti ayrı bir metod olarak
    /// duruyor ki ağa çıkmadan test edilebilsin — bu karar bir güvenlik sınırı,
    /// "herhalde doğru çalışıyordur" denecek bir yer değil.
    /// </summary>
    public static PullRequestInfo From(PullRequest pr)
        => new(
            pr.Head.Ref,
            pr.Head.Sha,
            IsFromFork(pr.Head?.Repository?.FullName, pr.Base?.Repository?.FullName));

    /// <summary>
    /// PR başka bir repodan (fork) mu geliyor? Karar yalnızca iki repo adına
    /// bakıyor — Octokit nesnesine değil.
    ///
    /// Neden ayrı ve saf bir metod: Octokit'in PullRequest/Repository modelleri
    /// salt okunur ve devasa ctor'lara sahip, testte elle kurmak neredeyse imkânsız.
    /// Kararı string'lere indirgeyince bu güvenlik sınırı ağa hiç çıkmadan, okunur
    /// testlerle sabitlenebiliyor (aynı yaklaşım: GitHubService.SelectLatestFailedRun).
    ///
    /// Neden önemli: fork'un dalına push edemeyiz. GitHub App'in token'ı yalnızca
    /// App'in KURULU OLDUĞU repolar için geçerli; katkıcının kendi fork'u o listede
    /// değil. Bu kontrol olmadan agent analizi yapar, LLM'e para öder, düzeltmeyi
    /// üretir ve en sonda push'ta 403 alır.
    ///
    /// null repo adı: fork PR açıldıktan sonra fork silinmişse GitHub bu alanı boş
    /// döndürüyor. O durumda da push edilemez — "fork" saymak güvenli taraf.
    /// </summary>
    internal static bool IsFromFork(string? headRepoFullName, string? baseRepoFullName)
    {
        if (string.IsNullOrWhiteSpace(headRepoFullName) || string.IsNullOrWhiteSpace(baseRepoFullName))
            return true;

        // GitHub repo adlarında büyük-küçük harfi korur ama eşleştirmede ayırt
        // etmez; duyarlı karşılaştırma aynı repoyu "fork" sanmaya yol açardı.
        return !string.Equals(headRepoFullName, baseRepoFullName, StringComparison.OrdinalIgnoreCase);
    }
}
