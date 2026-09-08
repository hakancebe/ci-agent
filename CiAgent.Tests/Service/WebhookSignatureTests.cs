using System.Security.Cryptography;
using System.Text;
using CiAgent.Service;

namespace CiAgent.Tests;

/// <summary>
/// İmza doğrulama servisin TEK kimlik doğrulama katmanı: endpoint internete açık,
/// "bu istek gerçekten GitHub'dan mı geldi" sorusunun başka cevabı yok. Bu yüzden
/// hem doğru imzayı kabul ettiği hem de her bozulma çeşidini reddettiği test ediliyor.
/// </summary>
public class WebhookSignatureTests
{
    private const string Secret = "s3cr3t-webhook-key";

    private static string Sign(byte[] body, string secret)
    {
        var hash = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body);
        return "sha256=" + Convert.ToHexString(hash).ToLowerInvariant();
    }

    [Fact]
    public void IsValid_AcceptsCorrectSignature()
    {
        var body = Encoding.UTF8.GetBytes("""{"action":"completed"}""");

        Assert.True(WebhookSignature.IsValid(body, Sign(body, Secret), Secret));
    }

    [Fact]
    public void IsValid_AcceptsUppercaseHex()
    {
        // GitHub küçük harf gönderiyor ama hex büyük harfle de aynı değer;
        // FromHexString ikisini de kabul ettiği için bu davranış kayıt altında.
        var body = Encoding.UTF8.GetBytes("""{"action":"completed"}""");
        var signature = Sign(body, Secret).ToUpperInvariant().Replace("SHA256=", "sha256=");

        Assert.True(WebhookSignature.IsValid(body, signature, Secret));
    }

    [Fact]
    public void IsValid_RejectsTamperedBody()
    {
        var original = Encoding.UTF8.GetBytes("""{"repo":"safe-repo"}""");
        var signature = Sign(original, Secret);

        // Saldırganın asıl denemesi bu: geçerli bir imzayı yakalayıp gövdeyi değiştirmek.
        var tampered = Encoding.UTF8.GetBytes("""{"repo":"victim-repo"}""");

        Assert.False(WebhookSignature.IsValid(tampered, signature, Secret));
    }

    [Fact]
    public void IsValid_RejectsWrongSecret()
    {
        var body = Encoding.UTF8.GetBytes("""{"action":"completed"}""");
        var signature = Sign(body, "baska-bir-secret");

        Assert.False(WebhookSignature.IsValid(body, signature, Secret));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void IsValid_RejectsMissingHeader(string? header)
    {
        var body = Encoding.UTF8.GetBytes("{}");

        Assert.False(WebhookSignature.IsValid(body, header, Secret));
    }

    [Fact]
    public void IsValid_RejectsMissingPrefix()
    {
        // Ham hex, "sha256=" öneki olmadan. GitHub her zaman önekle gönderiyor;
        // öneksizi kabul etmek imza algoritmasını belirsiz bırakırdı.
        var body = Encoding.UTF8.GetBytes("{}");
        var bare = Sign(body, Secret)["sha256=".Length..];

        Assert.False(WebhookSignature.IsValid(body, bare, Secret));
    }

    [Fact]
    public void IsValid_RejectsSha1Prefix()
    {
        // GitHub eskiden X-Hub-Signature (SHA-1) de gönderiyordu. SHA-1 kırık;
        // yanlışlıkla kabul edilmediği kayıt altında.
        var body = Encoding.UTF8.GetBytes("{}");
        var signature = Sign(body, Secret).Replace("sha256=", "sha1=");

        Assert.False(WebhookSignature.IsValid(body, signature, Secret));
    }

    [Fact]
    public void IsValid_RejectsMalformedHex()
    {
        var body = Encoding.UTF8.GetBytes("{}");

        // 64 karakter ama hex değil — FromHexString patlar, exception dışarı sızmamalı.
        Assert.False(WebhookSignature.IsValid(body, "sha256=" + new string('z', 64), Secret));
    }

    [Fact]
    public void IsValid_RejectsWrongLengthSignature()
    {
        var body = Encoding.UTF8.GetBytes("{}");

        Assert.False(WebhookSignature.IsValid(body, "sha256=abcd", Secret));
    }

    [Fact]
    public void IsValid_RejectsEmptySecret()
    {
        // Secret yapılandırılmamışsa HİÇBİR isteği kabul etmemeli. Aksi halde
        // eksik yapılandırma, endpoint'i herkese açık bırakırdı.
        var body = Encoding.UTF8.GetBytes("{}");

        Assert.False(WebhookSignature.IsValid(body, Sign(body, ""), ""));
    }

    [Fact]
    public void IsValid_AcceptsEmptyBody()
    {
        var body = Array.Empty<byte>();

        Assert.True(WebhookSignature.IsValid(body, Sign(body, Secret), Secret));
    }
}
