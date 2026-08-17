public sealed record TestFailure(string? FilePath, int? LineNumber, string? ErrorMessage)
{
    public bool Found => ErrorMessage is not null;
}