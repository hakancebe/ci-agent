using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CiAgent.Core;

namespace CiAgent.Tests;

/// <summary>
/// App JWT'si yanlışsa GitHub 401 döner ve agent hiçbir iş yapamaz — ama bu ancak
/// canlıda anlaşılır. Burada JWT'yi test içinde üretilen bir anahtar çiftiyle
/// üretip AÇIK anahtarla doğruluyoruz: imzanın gerçekten geçerli bir RS256 imzası
/// olduğu ağa hiç çıkmadan kanıtlanıyor.
/// </summary>
public class GitHubAppAuthTests
{
    private static (string PrivateKeyPem, RSA PublicKey) CreateKeyPair()
    {
        var rsa = RSA.Create(2048);
        var pem = rsa.ExportRSAPrivateKeyPem();   // PKCS#1 — GitHub'ın verdiği format

        var publicKey = RSA.Create();
        publicKey.ImportRSAPublicKey(rsa.ExportRSAPublicKey(), out _);

        return (pem, publicKey);
    }

    private static (JsonElement Header, JsonElement Claims) Decode(string jwt)
    {
        var parts = jwt.Split('.');

        static JsonElement Part(string segment)
        {
            // base64url → base64: karakterleri geri çevir, dolguyu tamamla.
            var padded = segment.Replace('-', '+').Replace('_', '/');
            padded = padded.PadRight(padded.Length + (4 - padded.Length % 4) % 4, '=');
            return JsonDocument.Parse(Convert.FromBase64String(padded)).RootElement.Clone();
        }

        return (Part(parts[0]), Part(parts[1]));
    }

    [Fact]
    public void CreateAppJwt_ProducesThreeSegments()
    {
        var (pem, _) = CreateKeyPair();

        var jwt = GitHubAppAuth.CreateAppJwt("12345", pem, DateTimeOffset.UtcNow);

        Assert.Equal(3, jwt.Split('.').Length);
    }

    [Fact]
    public void CreateAppJwt_SignatureVerifiesWithPublicKey()
    {
        var (pem, publicKey) = CreateKeyPair();

        var jwt = GitHubAppAuth.CreateAppJwt("12345", pem, DateTimeOffset.UtcNow);

        var parts = jwt.Split('.');
        var signingInput = Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}");

        var signature = parts[2].Replace('-', '+').Replace('_', '/');
        signature = signature.PadRight(signature.Length + (4 - signature.Length % 4) % 4, '=');

        Assert.True(publicKey.VerifyData(
            signingInput,
            Convert.FromBase64String(signature),
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1));
    }

    [Fact]
    public void CreateAppJwt_UsesRs256Header()
    {
        // GitHub yalnızca RS256 kabul ediyor; "alg" yanlışsa 401 gelir.
        var (pem, _) = CreateKeyPair();

        var (header, _) = Decode(GitHubAppAuth.CreateAppJwt("12345", pem, DateTimeOffset.UtcNow));

        Assert.Equal("RS256", header.GetProperty("alg").GetString());
        Assert.Equal("JWT", header.GetProperty("typ").GetString());
    }

    [Fact]
    public void CreateAppJwt_NumericAppIdIsSentAsNumber()
    {
        // GitHub'ın dokümanı iss'i sayı olarak veriyor; string gönderimin kabul
        // edilip edilmediği sürüme göre değişebildiği için sayısal ID sayı olarak
        // gidiyor. Yanlışı "JWT could not be decoded" 401'i olarak döner.
        var (pem, _) = CreateKeyPair();

        var (_, claims) = Decode(GitHubAppAuth.CreateAppJwt("999888", pem, DateTimeOffset.UtcNow));

        Assert.Equal(JsonValueKind.Number, claims.GetProperty("iss").ValueKind);
        Assert.Equal(999888, claims.GetProperty("iss").GetInt64());
    }

    [Fact]
    public void CreateAppJwt_NonNumericAppIdIsSentAsString()
    {
        // GitHub'ın yeni Client ID formatı (Iv1.xxx) sayısal değil.
        var (pem, _) = CreateKeyPair();

        var (_, claims) = Decode(GitHubAppAuth.CreateAppJwt("Iv1.abc123", pem, DateTimeOffset.UtcNow));

        Assert.Equal(JsonValueKind.String, claims.GetProperty("iss").ValueKind);
        Assert.Equal("Iv1.abc123", claims.GetProperty("iss").GetString());
    }

    [Fact]
    public void CreateAppJwt_BacksDatedIatToToleranceClockSkew()
    {
        // GitHub, iat'ı GELECEKTE olan JWT'yi reddediyor. Sunucu saatimiz birkaç
        // saniye ileriyse token üretimi sebepsiz patlardı; iat geriye alınıyor.
        var (pem, _) = CreateKeyPair();
        var now = DateTimeOffset.UtcNow;

        var (_, claims) = Decode(GitHubAppAuth.CreateAppJwt("1", pem, now));

        Assert.True(claims.GetProperty("iat").GetInt64() < now.ToUnixTimeSeconds());
    }

    [Fact]
    public void CreateAppJwt_ExpiryStaysUnderGitHubTenMinuteLimit()
    {
        // GitHub 10 dakikadan uzun ömürlü JWT'yi reddediyor. iat geriye alındığı
        // için pencere iat→exp olarak ölçülmeli; sınıra dayanırsa canlıda patlar.
        var (pem, _) = CreateKeyPair();
        var now = DateTimeOffset.UtcNow;

        var (_, claims) = Decode(GitHubAppAuth.CreateAppJwt("1", pem, now));

        var window = claims.GetProperty("exp").GetInt64() - claims.GetProperty("iat").GetInt64();

        Assert.True(window < 600, $"JWT penceresi {window} sn — GitHub'ın 600 sn sınırını aşıyor.");
        Assert.True(claims.GetProperty("exp").GetInt64() > now.ToUnixTimeSeconds());
    }

    [Fact]
    public void CreateAppJwt_AcceptsPkcs8PrivateKey()
    {
        // GitHub PKCS#1 ("BEGIN RSA PRIVATE KEY") veriyor ama kullanıcı anahtarı
        // dönüştürmüş olabilir; ImportFromPem ikisini de tanıyor.
        using var rsa = RSA.Create(2048);
        var pkcs8 = rsa.ExportPkcs8PrivateKeyPem();

        var jwt = GitHubAppAuth.CreateAppJwt("1", pkcs8, DateTimeOffset.UtcNow);

        Assert.Equal(3, jwt.Split('.').Length);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_RejectsEmptyAppId(string appId)
    {
        var (pem, _) = CreateKeyPair();

        Assert.Throws<ArgumentException>(() => new GitHubAppAuth(appId, pem));
    }

    [Fact]
    public void Constructor_RejectsEmptyPrivateKey()
    {
        Assert.Throws<ArgumentException>(() => new GitHubAppAuth("123", ""));
    }
}
