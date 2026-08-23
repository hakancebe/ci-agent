using Octokit;
using System.Text.RegularExpressions;

namespace CiAgent.Core;

public static class LogParser
{
    public static WorkflowJobStep? FindFailedStep(WorkflowJob job)
    {
        // conclusion da null olabildiği için "Conclusion?" yapısı kullanıldı
        return job.Steps?.FirstOrDefault(s => s.Conclusion?.StringValue == "failure");
    }

    public static List<CheckRunAnnotation> FilterAnnotations(IReadOnlyList<CheckRunAnnotation> annotations)
    {
        return annotations
        // notice, warning gibi hataları eleyip sadece failure olanı alıyoruz
            .Where(a => a.AnnotationLevel?.StringValue == "failure")
            .Where(a => !(a.Path?.StartsWith(".github/") ?? false))
            .GroupBy(a => (a.Path, a.StartLine, a.Message))
            .Select(g => g.First())
            .ToList();
    }   
    public static string StripTimestamp(string logLine)
    {
        return Regex.Replace(logLine, @"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d+Z\s", "");
    }

    //API üzerinden gelen metadata
    public static List<string> ExtractStepBlocks(string rawLog)
    {
        var lines = rawLog.Split('\n');
        var blocks = new List<string>();
        var currentBlock = new List<string>();

        foreach (var rawLine in lines)
        {
            var line = StripTimestamp(rawLine);

            if (line.StartsWith("##[group]Run "))
            {
                if (currentBlock.Count > 0)
                    blocks.Add(string.Join("\n", currentBlock));

                currentBlock = new List<string> { line };
                continue;
            }

            if (currentBlock.Count > 0)
            {
                currentBlock.Add(line);
            }
        }

        if (currentBlock.Count > 0)
            blocks.Add(string.Join("\n", currentBlock));

        return blocks;
    }

    //log dosyasının kendisi
    public static TestFailure ExtractTestFailure(string stepBlock)
    {
        var failedMatch = Regex.Match(stepBlock, @"Failed\s+(\S+)\s*\[.*?\]\s*Error Message:\s*(.+?)Stack Trace:", RegexOptions.Singleline);
        if (!failedMatch.Success)
            return new TestFailure(null, null, null);

        var errorMessage = failedMatch.Groups[2].Value.Trim();

        var stackTraceMatch = Regex.Match(stepBlock, @"in\s+(?:/[\w./-]+/)?([\w.]+\.cs):line\s+(\d+)");
        string? filePath = stackTraceMatch.Success ? stackTraceMatch.Groups[1].Value : null;
        int? lineNumber = stackTraceMatch.Success ? int.Parse(stackTraceMatch.Groups[2].Value) : null;

        return new TestFailure(filePath, lineNumber, errorMessage);
    }

    public static TestFailure ExtractGenericError(string stepBlock)
    {
        var match = Regex.Match(
            stepBlock,
            @"(?<path>[^\s:]+\.csproj)\s*:\s*error\s+(?<code>\w+\d*)\s*:\s*(?<msg>.+?)\s*(?:\[.*\])?\s*$",
            RegexOptions.Multiline);

        if (match.Success)
        {
            return new TestFailure(
                match.Groups["path"].Value,
                null,
                $"{match.Groups["code"].Value}: {match.Groups["msg"].Value}");
        }

        // Son çare: ##[error] satırı (örn. "Process completed with exit code 1").
        var genericMatch = Regex.Match(stepBlock, @"##\[error\](?<msg>.+)$", RegexOptions.Multiline);
        if (genericMatch.Success)
            return new TestFailure(null, null, genericMatch.Groups["msg"].Value.Trim());

        return new TestFailure(null, null, null);
    }

    public static ErrorContext? BuildErrorContext(WorkflowJob job, IReadOnlyList<CheckRunAnnotation> annotations, string rawLog)
    {
        var failedStep = FindFailedStep(job);
        if (failedStep == null)
            return null;

        var stepBlocks = ExtractStepBlocks(rawLog);

        string? matchingBlock = null;
        string? filePath = null;
        int? lineNumber = null;
        string? errorMessage = null;

        foreach (var block in stepBlocks)
        {
            var (fp, ln, msg) = ExtractTestFailure(block);
            if (msg != null)
            {
                matchingBlock = TrimToTestSummary(block);  // ← değişiklik burada
                filePath = fp;
                lineNumber = ln;
                errorMessage = msg;
                break;
            }
        }

        if (errorMessage is null)
        {
            foreach (var block in stepBlocks)
            {
                var (fp, ln, msg) = ExtractGenericError(block);
                if (msg != null)
                {
                    matchingBlock = block;
                    filePath = fp;
                    lineNumber = ln;
                    errorMessage = msg;
                    break;
                }
            }
        }

        var filteredAnnotations = FilterAnnotations(annotations)
            .Select(a => $"{a.Path}:{a.StartLine} - {a.Message}")
            .ToList();

        return new ErrorContext
        {
            JobName = job.Name,
            FailedStepName = failedStep.Name,
            RawStepLog = matchingBlock,
            FilteredAnnotations = filteredAnnotations,
            FilePath = filePath,
            LineNumber = lineNumber,
            ErrorMessage = errorMessage
        };
    }

    private static string TrimToTestSummary(string stepBlock)
    {
        var summaryMatch = Regex.Match(stepBlock, @"^.*Failed!\s*-\s*Failed:.*$", RegexOptions.Multiline);
        return summaryMatch.Success
            ? stepBlock[..(summaryMatch.Index + summaryMatch.Length)]
            : stepBlock;
    }

}