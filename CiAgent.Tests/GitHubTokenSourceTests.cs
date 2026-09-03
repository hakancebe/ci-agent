using CiAgent.Core;

namespace CiAgent.Tests;

/// <summary>
/// "Agent hangi kimlikle bağlanacak" kararı. Bu mantık Program.cs'in içindeyken
/// LOKALDE HİÇ TETİKLENMİYORDU: yapılandırma zinciri user secrets'ı da okuduğu
/// için GITHUB_TOKEN her zaman doluydu, yani App yolu ancak container'da
/// çalışırdı — ve bir hata orada, canlıda ortaya çıkardı.
/// </summary>
public class GitHubTokenSourceTests
{
    private const string Pem = "-----BEGIN RSA PRIVATE KEY-----\nMIIE...\n-----END RSA PRIVATE KEY-----";

    [Fact]
    public void TryReadAppCredentials_ReturnsCredentialsWhenComplete()
    {
        var creds = GitHubTokenSource.TryReadAppCredentials("123456", Pem, "42");

        Assert.NotNull(creds);
        Assert.Equal("123456", creds!.AppId);
        Assert.Equal(42, creds.InstallationId);
    }

    [Theory]
    [InlineData(null, "42")]
    [InlineData("", "42")]
    [InlineData("   ", "42")]
    public void TryReadAppCredentials_NullWhenAppIdMissing(string? appId, string installation)
    {
        Assert.Null(GitHubTokenSource.TryReadAppCredentials(appId, Pem, installation));
    }

    [Fact]
    public void TryReadAppCredentials_NullWhenPrivateKeyMissing()
    {
        Assert.Null(GitHubTokenSource.TryReadAppCredentials("123456", null, "42"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("sayı-değil")]
    [InlineData("0")]
    [InlineData("-5")]
    public void TryReadAppCredentials_NullWhenInstallationIdInvalid(string? installation)
    {
        // Yarım yapılandırmayla token üretmeye çalışıp anlamsız bir 401 almaktansa,
        // çağıranın "GITHUB_TOKEN eksik" demesi çok daha anlaşılır bir hata.
        Assert.Null(GitHubTokenSource.TryReadAppCredentials("123456", Pem, installation));
    }

    [Fact]
    public void NormalizePem_UnescapesLiteralNewlines()
    {
        // ACA env var'larında çok satırlı değer taşımak zor; PEM genelde satır
        // sonları kaçırılmış şekilde geliyor. Çözülmezse ImportFromPem
        // "geçersiz PEM" der ve sebebini söylemez.
        var escaped = "-----BEGIN RSA PRIVATE KEY-----\\nMIIE...\\n-----END RSA PRIVATE KEY-----";

        var result = GitHubTokenSource.NormalizePem(escaped);

        Assert.Contains("\n", result);
        Assert.DoesNotContain("\\n", result);
    }

    [Fact]
    public void NormalizePem_LeavesRealNewlinesAlone()
    {
        // Dosyadan okunan PEM zaten gerçek satır sonlarıyla geliyor; bozulmamalı.
        Assert.Equal(Pem, GitHubTokenSource.NormalizePem(Pem));
    }

    [Fact]
    public void TryReadAppCredentials_NormalizesPem()
    {
        var creds = GitHubTokenSource.TryReadAppCredentials(
            "1", "-----BEGIN RSA PRIVATE KEY-----\\nabc\\n-----END RSA PRIVATE KEY-----", "7");

        Assert.NotNull(creds);
        Assert.DoesNotContain("\\n", creds!.PrivateKeyPem);
    }
}
