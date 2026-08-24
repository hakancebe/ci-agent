using Octokit;
using System.Text;
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

    // Bir step bloğunda birden fazla test aynı anda fail olabilir (xUnit hepsini
    // sırayla listeler). Regex.Matches ile tamamını yakalıyoruz; tek sonuç
    // döndüren ExtractTestFailure bunun üzerine kurulu (geriye dönük uyumluluk için).
    private static readonly Regex TestFailureRegex = new(
        @"Failed\s+(?<name>\S+)\s*\[.*?\]\s*Error Message:\s*(?<msg>.+?)Stack Trace:\s*(?<stack>.*?)(?=\r?\n\s*Failed\s+\S+\s*\[|\r?\n\s*Failed!\s*-|\z)",
        RegexOptions.Singleline);

    private static readonly Regex StackTraceFileLineRegex = new(
        @"in\s+(?:/[\w./-]+/)?([\w.]+\.cs):line\s+(\d+)");

    private sealed record NamedTestFailure(string Name, string? FilePath, int? LineNumber, string Message);

    private static List<NamedTestFailure> ExtractNamedTestFailures(string stepBlock)
    {
        var failures = new List<NamedTestFailure>();

        foreach (Match m in TestFailureRegex.Matches(stepBlock))
        {
            var stackMatch = StackTraceFileLineRegex.Match(m.Groups["stack"].Value);
            failures.Add(new NamedTestFailure(
                m.Groups["name"].Value,
                stackMatch.Success ? stackMatch.Groups[1].Value : null,
                stackMatch.Success ? int.Parse(stackMatch.Groups[2].Value) : null,
                m.Groups["msg"].Value.Trim()));
        }

        return failures;
    }

    //log dosyasının kendisi
    public static TestFailure ExtractTestFailure(string stepBlock)
    {
        var failures = ExtractNamedTestFailures(stepBlock);
        if (failures.Count == 0)
            return new TestFailure(null, null, null);

        var first = failures[0];
        return new TestFailure(first.FilePath, first.LineNumber, first.Message);
    }

    public static TestFailure ExtractGenericError(string stepBlock)
    {
        // 1) NuGet/restore tarzı: "xxx.csproj : error CODE: mesaj"
        var restoreMatch = Regex.Match(
            stepBlock,
            @"(?<path>[^\s:]+\.csproj)\s*:\s*error\s+(?<code>\w+\d*)\s*:\s*(?<msg>.+?)\s*(?:\[.*\])?\s*$",
            RegexOptions.Multiline);

        if (restoreMatch.Success)
        {
            return new TestFailure(
                restoreMatch.Groups["path"].Value,
                null,
                $"{restoreMatch.Groups["code"].Value}: {restoreMatch.Groups["msg"].Value}");
        }

        // 2) C# derleyici tarzı: "Dosya.cs(satır,kolon): error CODE: mesaj".
        // Path karakter sınıfı bilinçli olarak dar tutuldu ([\w./-]) ki "##[error]"
        // gibi önekler path'e karışmasın.
        var compilerMatch = Regex.Match(
            stepBlock,
            @"(?<path>[\w./-]+\.cs)\((?<line>\d+),\d+\)\s*:\s*error\s+(?<code>\w+\d*)\s*:\s*(?<msg>.+?)\s*(?:\[.*\])?\s*$",
            RegexOptions.Multiline);

        if (compilerMatch.Success)
        {
            return new TestFailure(
                compilerMatch.Groups["path"].Value,
                int.Parse(compilerMatch.Groups["line"].Value),
                $"{compilerMatch.Groups["code"].Value}: {compilerMatch.Groups["msg"].Value}");
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
        var allFailuresLocated = false;

        foreach (var block in stepBlocks)
        {
            var failures = ExtractNamedTestFailures(block);
            if (failures.Count > 0)
            {
                matchingBlock = TrimToTestSummary(block);
                (filePath, lineNumber, errorMessage) = CombineTestFailures(failures);
                // Tek tek her failure'ın kendi dosya:satır'ı bulunmuş mu? (üstteki
                // filePath/lineNumber sadece ilk konumu bilinen failure'a ait.)
                allFailuresLocated = failures.All(f => f.FilePath != null && f.LineNumber != null);
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
                    allFailuresLocated = fp != null && ln != null;
                    break;
                }
            }
        }

        // Başarısız adımdan sonraki "Post job cleanup" vb. içerik hatayla ilgisiz;
        // LLM'e gereksiz gürültü göndermemek için kırpıyoruz.
        if (matchingBlock != null)
            matchingBlock = TrimPostJobNoise(matchingBlock);

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
            ErrorMessage = errorMessage,
            AllFailuresLocated = allFailuresLocated
        };
    }

    // Aynı adımda birden fazla test fail olduğunda hiçbirini gizlemeden hepsini
    // tek bir ErrorMessage'da toplar. Üst seviye FilePath/LineNumber, konumu
    // bilinen ilk test'ten alınır; ama her testin kendi dosya:satır bilgisi de
    // mesaj içinde ayrıca yer alır.
    private static (string? FilePath, int? LineNumber, string ErrorMessage) CombineTestFailures(
        List<NamedTestFailure> failures)
    {
        if (failures.Count == 1)
        {
            var only = failures[0];
            return (only.FilePath, only.LineNumber, only.Message);
        }

        var withLocation = failures.FirstOrDefault(f => f.FilePath != null);

        var sb = new StringBuilder();
        sb.AppendLine($"{failures.Count} test başarısız oldu:");
        for (var i = 0; i < failures.Count; i++)
        {
            var f = failures[i];
            var location = f.FilePath != null ? $" ({f.FilePath}:{f.LineNumber})" : "";
            sb.AppendLine();
            sb.AppendLine($"{i + 1}) {f.Name}{location}");
            sb.AppendLine($"   {f.Message}");
        }

        return (withLocation?.FilePath, withLocation?.LineNumber, sb.ToString().TrimEnd());
    }

    private static string TrimToTestSummary(string stepBlock)
    {
        var summaryMatch = Regex.Match(stepBlock, @"^.*Failed!\s*-\s*Failed:.*$", RegexOptions.Multiline);
        return summaryMatch.Success
            ? stepBlock[..(summaryMatch.Index + summaryMatch.Length)]
            : stepBlock;
    }

    private static string TrimPostJobNoise(string stepBlock)
    {
        var idx = stepBlock.IndexOf("Post job cleanup.", StringComparison.Ordinal);
        return idx >= 0 ? stepBlock[..idx].TrimEnd() : stepBlock;
    }

}
