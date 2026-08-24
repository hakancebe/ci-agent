namespace CiAgent.Core;

public class ErrorContext
{
    public required string JobName { get; init; }
    public required string FailedStepName { get; init; }
    public string? RawStepLog { get; set; }
    public List<string> FilteredAnnotations { get; set; } = new();
    public string? FilePath { get; set; }
    public int? LineNumber { get; set; }
    public string? ErrorMessage { get; set; }

    // Tek hatada FilePath+LineNumber doluysa, ya da çoklu test hatasında HER
    // failure'ın kendi konumu (dosya:satır) bulunmuşsa true. LlmService bunu
    // RawStepLog'u prompt'a ekleyip eklememeye karar vermek için kullanıyor:
    // konum zaten kesinse (build-cs1002'nin aksine) ham log ekstra bir şey
    // katmıyor, sadece token israfı oluyor.
    public bool AllFailuresLocated { get; set; }
}