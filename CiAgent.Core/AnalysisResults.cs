using System.Text.Json.Serialization;

namespace CiAgent.Core;

public class AnalysisResult
{
    [JsonPropertyName("summary")]
    public required string Summary { get; init; }

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

    // --- LLM'den GELMEYEN alanlar ---------------------------------------
    // [JsonIgnore] bilinçli: bu iki alan model çıktısından ASLA doldurulmamalı.
    // Aksi halde LLM `"skipped": true` üretip "analiz atlandı" durumunu taklit
    // edebilirdi. Yalnızca ForSkipped() bunları set eder.

    /// <summary>Prompt limiti aşıldığı için LLM'e hiç gidilmediyse true.</summary>
    [JsonIgnore]
    public bool Skipped { get; init; }

    /// <summary>Atlama gerekçesinin insan tarafından okunabilir hâli.</summary>
    [JsonIgnore]
    public string? SkipReason { get; init; }

    /// <summary>
    /// Prompt MaxPromptChars'ı aştığında LLM'e hiç istek atılmadan dönülen sonuç.
    /// null yerine gerçek bir AnalysisResult dönülüyor ki rapor akışı (PR yorumu /
    /// commit yorumu / Job Summary) normal işlesin ve durum sessizce kaybolmasın.
    /// Confidence "low": mevcut ConfidenceBadge eşlemesi bozulmasın diye.
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
            Confidence = "low",
            Summary = reason,
            RootCause = "Belirlenmedi — log bu run için otomatik analiz sınırının üzerinde.",
            SuggestedFix = "Başarısız adımın loglarını elle inceleyin."
        };
    }
}