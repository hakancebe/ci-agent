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

    // FilePath+LineNumber ikisi de doluysa (compile/test hataları) GitHub Contents
    // API'den çekilen dosyanın ±30 satırlık kesiti. Path+line yoksa (restore/deploy
    // hataları) bu adım hiç tetiklenmez, CodeSnippet null kalır. Program.cs LLM
    // çağrısından önce doldurur; LlmService.BuildPrompt bunu prompt'a ekler.
    public string? CodeSnippet { get; set; }

    // Tek hatada FilePath+LineNumber doluysa, ya da çoklu test hatasında HER
    // failure'ın kendi konumu (dosya:satır) bulunmuşsa true. LlmService bunu
    // RawStepLog'u prompt'a ekleyip eklememeye karar vermek için kullanıyor:
    // konum zaten kesinse (build-cs1002'nin aksine) ham log ekstra bir şey
    // katmıyor, sadece token israfı oluyor.
    public bool AllFailuresLocated { get; set; }
}