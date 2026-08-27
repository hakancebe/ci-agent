using System.Text.Json.Serialization;

namespace CiAgent.Core;

/// <summary>
/// TEK bir kök nedene ait analiz. Bir run'da birden fazla bağımsız sorun olabilir
/// (build job'ında derleme hatası + deploy job'ında restore hatası), bu yüzden
/// <see cref="AnalysisResult"/> bunlardan bir liste taşır.
/// </summary>
public class Analysis
{
    /// <summary>Kısa başlık — raporda bölüm adı olarak kullanılır.</summary>
    [JsonPropertyName("title")]
    public required string Title { get; init; }

    [JsonPropertyName("rootCause")]
    public required string RootCause { get; init; }

    [JsonPropertyName("suggestedFix")]
    public required string SuggestedFix { get; init; }

    [JsonPropertyName("confidence")]
    public required string Confidence { get; init; }   // high | medium | low

    [JsonPropertyName("affectedFile")]
    public string? AffectedFile { get; init; }

    [JsonPropertyName("affectedLine")]
    public int? AffectedLine { get; init; }
}

public class AnalysisResult
{
    /// <summary>Tüm run'ı tek cümlede özetleyen üst seviye değerlendirme.</summary>
    [JsonPropertyName("summary")]
    public required string Summary { get; init; }

    /// <summary>
    /// Tespit edilen kök nedenler. Çoğu run'da tek eleman olur; 5 test aynı bozuk
    /// metottan patlıyorsa LLM'in bunları TEK analizde birleştirmesi beklenir,
    /// gerçekten bağımsız sorunlar varsa ayrı elemanlar döner.
    /// </summary>
    [JsonPropertyName("analyses")]
    public List<Analysis> Analyses { get; init; } = new();

    // --- LLM'den GELMEYEN alanlar ---------------------------------------
    // [JsonIgnore] bilinçli: bu alanlar model çıktısından ASLA doldurulmamalı.
    // Aksi halde LLM `"skipped": true` üretip "analiz atlandı" durumunu taklit
    // edebilirdi. Yalnızca ForSkipped() ve LlmService bunları set eder.

    /// <summary>Hiçbir prompt kademesi limite sığmadığı için LLM'e hiç gidilmediyse true.</summary>
    [JsonIgnore]
    public bool Skipped { get; init; }

    /// <summary>Atlama gerekçesinin insan tarafından okunabilir hâli.</summary>
    [JsonIgnore]
    public string? SkipReason { get; init; }

    /// <summary>
    /// Prompt limitine sığmak için bilgi feda edildiyse (ham log / kod kesitleri /
    /// hata sayısı) neyin çıkarıldığını anlatır; tam prompt gittiyse null. init değil
    /// set: LLM yanıtı deserialize edildikten SONRA LlmService tarafından iliştiriliyor.
    /// Raporda gösteriliyor ki "analiz eksik veriyle yapıldı" bilgisi kaybolmasın.
    /// </summary>
    [JsonIgnore]
    public string? ReductionNote { get; set; }

    /// <summary>
    /// Hiçbir prompt kademesi MaxPromptChars'a sığmadığında LLM'e hiç istek atılmadan
    /// dönülen sonuç. null yerine gerçek bir AnalysisResult dönülüyor ki rapor akışı
    /// (PR yorumu / commit yorumu / Job Summary) normal işlesin ve durum sessizce
    /// kaybolmasın. Analyses boş: uydurma bir kök neden gösterilmemeli.
    /// </summary>
    public static AnalysisResult ForSkipped(int promptChars, int maxChars)
    {
        var reason =
            $"Analiz girdisi (prompt) {promptChars:N0} karakter, limit {maxChars:N0} karakter — "
            + "otomatik analiz limiti aştığı için yapılmadı.";

        return new AnalysisResult
        {
            Skipped = true,
            SkipReason = reason,
            Summary = reason
        };
    }

    /// <summary>
    /// LLM katmanı patladığında (ağ, deployment adı, rate limit, şema uyuşmazlığı)
    /// akışı durdurmadan dönülen sonuç. ForSkipped'dan farkı: burada bir sorun VAR
    /// ve kullanıcıya ne yapacağı söyleniyor, sadece kök neden otomatik bulunamadı.
    /// </summary>
    public static AnalysisResult ForLlmFailure(Exception ex)
    {
        return new AnalysisResult
        {
            Summary = "LLM analizi sırasında bir hata oluştu, otomatik analiz yapılamadı.",
            Analyses =
            {
                new Analysis
                {
                    Title = "Otomatik analiz başarısız",
                    RootCause = $"{ex.GetType().Name}: {ex.Message}",
                    SuggestedFix = "Logu manuel inceleyin. Sorun devam ederse Azure OpenAI "
                                 + "bağlantısını/secret'larını kontrol edin.",
                    Confidence = "low"
                }
            }
        };
    }
}
