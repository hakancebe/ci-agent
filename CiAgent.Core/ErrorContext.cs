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
}