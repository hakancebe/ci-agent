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
}