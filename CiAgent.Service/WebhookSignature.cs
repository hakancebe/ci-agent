using System.Security.Cryptography;
using System.Text;

namespace CiAgent.Service;

/// <summary>
/// GitHub'ın webhook imzasını (X-Hub-Signature-256) doğrular.
///
/// Bu, servisin TEK kimlik doğrulama katmanı: endpoint internetten herkese açık,
/// "bu isteği gerçekten GitHub mu gönderdi" sorusunun cevabı yalnızca bu imza.
/// Doğrulama olmadan herhangi biri sahte bir workflow_run olayı uydurup agent'a
/// istediği repo'yu analiz ettirebilir (ve /fix ile push ettirebilirdi).
/// </summary>
internal static class WebhookSignature
{
    private const string Prefix = "sha256=";

    /// <param name="body">
    /// İsteğin HAM gövde byte'ları. JSON'a deserialize edilip yeniden serialize
    /// edilmiş hali KULLANILAMAZ: imza byte'ların birebir kendisi üzerinden
    /// hesaplanıyor, boşluk/alan sırası değişirse imza tutmaz.
    /// </param>
    /// <param name="signatureHeader">X-Hub-Signature-256 başlığı ("sha256=..." formatında).</param>
    /// <param name="secret">App kaydında girilen webhook secret.</param>
    public static bool IsValid(ReadOnlySpan<byte> body, string? signatureHeader, string secret)
    {
        if (string.IsNullOrWhiteSpace(signatureHeader) || string.IsNullOrWhiteSpace(secret))
            return false;

        if (!signatureHeader.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
            return false;

        var providedHex = signatureHeader[Prefix.Length..];

        // HMAC çıktısı 32 byte = 64 hex karakter. Uzunluk tutmuyorsa FromHexString
        // zaten patlardı; erken elemek exception'ı akış kontrolü olarak kullanmaktan iyi.
        if (providedHex.Length != 64)
            return false;

        byte[] provided;
        try
        {
            provided = Convert.FromHexString(providedHex);
        }
        catch (FormatException)
        {
            return false;
        }

        var expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body);

        // Sabit zamanlı karşılaştırma ŞART. Sıradan bir == (ya da SequenceEqual) ilk
        // farklı byte'ta çıkar; saldırgan cevap süresini ölçerek imzayı byte byte
        // tahmin edebilir. FixedTimeEquals süreyi içerikten bağımsız tutar.
        return CryptographicOperations.FixedTimeEquals(expected, provided);
    }
}
