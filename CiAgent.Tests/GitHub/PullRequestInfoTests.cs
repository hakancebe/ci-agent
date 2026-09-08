using CiAgent.Core;

namespace CiAgent.Tests;

/// <summary>
/// Fork tespiti bir güvenlik/ekonomi sınırı: yanlış tarafa düşerse ya fork PR'ında
/// boşuna analiz yapılıp en sonda push 403 alınır, ya da (daha kötüsü) gerçekten
/// push edilebilir bir PR "fork" sanılıp /fix hiç çalışmaz.
///
/// Bu kural eskiden ci-agent-fix.yml içinde bir `github-script` adımıydı ve hiçbir
/// şekilde test edilemiyordu; koda taşınmasının somut kazancı bu dosya.
/// </summary>
public class PullRequestInfoTests
{
    [Fact]
    public void IsFromFork_SameRepo_False()
    {
        Assert.False(PullRequestInfo.IsFromFork(
            "hakancebe/ci-agent-pilot", "hakancebe/ci-agent-pilot"));
    }

    [Fact]
    public void IsFromFork_DifferentOwner_True()
    {
        // Klasik fork: aynı repo adı, farklı sahip.
        Assert.True(PullRequestInfo.IsFromFork(
            "katkici/ci-agent-pilot", "hakancebe/ci-agent-pilot"));
    }

    [Fact]
    public void IsFromFork_CaseDiffersButSameRepo_False()
    {
        // GitHub adlarda büyük-küçük harfi korur ama eşleştirmede ayırt etmez.
        // Duyarlı karşılaştırma, kendi repomuzdaki PR'ı "fork" sanıp /fix'i
        // sessizce devre dışı bırakırdı.
        Assert.False(PullRequestInfo.IsFromFork(
            "HakanCebe/CI-Agent-Pilot", "hakancebe/ci-agent-pilot"));
    }

    [Theory]
    [InlineData(null, "hakancebe/ci-agent-pilot")]
    [InlineData("hakancebe/ci-agent-pilot", null)]
    [InlineData("", "hakancebe/ci-agent-pilot")]
    [InlineData("   ", "hakancebe/ci-agent-pilot")]
    public void IsFromFork_MissingRepoName_TreatedAsFork(string? head, string? @base)
    {
        // Fork PR açıldıktan sonra fork silinirse GitHub head.repo'yu null döndürüyor.
        // Bilinmeyen durumda "push edilebilir" varsaymak, en sonda 403 almak demekti;
        // güvenli taraf "fork" saymak.
        Assert.True(PullRequestInfo.IsFromFork(head, @base));
    }

    [Fact]
    public void IsFromFork_SimilarButDifferentName_True()
    {
        // Alt dize eşleşmesi gibi gevşek bir kontrol burada felaket olurdu.
        Assert.True(PullRequestInfo.IsFromFork(
            "hakancebe/ci-agent-pilot-fork", "hakancebe/ci-agent-pilot"));
    }
}
