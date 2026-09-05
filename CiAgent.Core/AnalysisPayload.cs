using System.Text;
using System.Text.Json;

namespace CiAgent.Core;

/// <summary>
/// Analiz sonucunu PR yorumunun İÇİNE gizli bir blok olarak gömer ve geri okur.
///
/// Neden gerekli: rozet ("🔒 Otomatik düzeltilemez") ile /fix'in kararı farklı
/// çıkabiliyordu. Sebep tek bir soruyu İKİ ayrı LLM çağrısının cevaplamasıydı —
/// web servisi yorumu yazarken bir kez, /fix job'ı kendi analizini koştururken
/// bir kez daha. fixable kararı kararsız olduğu için iki bağımsız örneklem
/// ayrışıyordu; canlıda bir turda rozet çıkmadı ama /fix yine de reddetti.
///
/// Çözüm tek doğruluk kaynağı: analiz bir kez yapılıp sonucu yoruma gömülüyor,
/// /fix onu okuyup AYNI sonucu kullanıyor. Yan fayda: /fix başına bir LLM
/// çağrısı tasarruf.
///
/// Neden yorumun içinde: web servisi ile /fix job'ı AYRI container'lar, ortak
/// bellekleri yok. PR yorumu ikisinin de eriştiği tek kalıcı yer — ve zaten
/// run id'ye göre upsert edildiği için doğal bir anahtarı var.
///
/// Base64 bilinçli: ham JSON içinde "-->" geçse HTML yorumunu erken kapatır,
/// ayrıca markdown'da satır sonları ve tırnaklar sorun çıkarabilir.
/// </summary>
public static class AnalysisPayload
{
    private const string Prefix = "<!-- ci-agent-data:";
    private const string Suffix = " -->";

    /// <summary>Yorumun sonuna eklenecek gizli veri satırı.</summary>
    public static string Encode(AnalysisResult result)
    {
        var json = JsonSerializer.Serialize(result);
        var base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
        return Prefix + base64 + Suffix;
    }

    /// <summary>
    /// Yorum gövdesinden analiz sonucunu geri okur. Blok yoksa ya da
    /// çözülemezse null — çağıran taraf analizi baştan yapmalı.
    ///
    /// Bozuk/eski veriye karşı toleranslı olması şart: yorum elle düzenlenmiş
    /// olabilir, ya da eski sürümde yazılmış olup bu bloğu hiç taşımıyor
    /// olabilir. İkisi de hata değil, "veri yok" demek.
    /// </summary>
    public static AnalysisResult? TryDecode(string? commentBody)
    {
        if (string.IsNullOrWhiteSpace(commentBody))
            return null;

        var start = commentBody.IndexOf(Prefix, StringComparison.Ordinal);
        if (start < 0)
            return null;

        var payloadStart = start + Prefix.Length;
        var end = commentBody.IndexOf(Suffix, payloadStart, StringComparison.Ordinal);
        if (end < 0)
            return null;

        var base64 = commentBody[payloadStart..end].Trim();

        try
        {
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(base64));
            return JsonSerializer.Deserialize<AnalysisResult>(json);
        }
        catch (Exception)
        {
            // FormatException (bozuk base64), JsonException (şema değişmiş),
            // DecoderFallbackException — hepsinin doğru cevabı aynı: veri yok.
            return null;
        }
    }
}
