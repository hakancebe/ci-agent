using Octokit;
using System.Text;
using System.Text.RegularExpressions;

namespace CiAgent.Core;

public static class LogParser
{
    /// <summary>Tek bir başarısız job'ın analiz için gereken üç girdisi.</summary>
    public sealed record JobLog(
        WorkflowJob Job,
        IReadOnlyList<CheckRunAnnotation> Annotations,
        string RawLog);

    public static WorkflowJobStep? FindFailedStep(WorkflowJob job)
    {
        // conclusion da null olabildiği için "Conclusion?" yapısı kullanıldı
        return job.Steps?.FirstOrDefault(s => s.Conclusion?.StringValue == "failure");
    }

    public static List<WorkflowJobStep> FindFailedSteps(WorkflowJob job)
    {
        return job.Steps?.Where(s => s.Conclusion?.StringValue == "failure").ToList()
               ?? new List<WorkflowJobStep>();
    }
    // IReadOnlyList<CheckRunAnnotation> liste sadece okuanbilir değiştirilemez
    public static List<CheckRunAnnotation> FilterAnnotations(IReadOnlyList<CheckRunAnnotation> annotations)
    {
        return annotations
            .Where(a => a.AnnotationLevel?.StringValue == "failure") // notice, warning gibi hataları eleyip sadece failure olanı alıyoruz
            .Where(a => !(a.Path?.StartsWith(".github/") ?? false)) // githubun kendi hatalarını istemiyoruz
            .GroupBy(a => (a.Path, a.StartLine, a.Message)) // aynı annotation var ise bunları Dosya yolu, Başlangıç, Hata mesajı olarak bir yerde topluyor.
            .Select(g => g.First()) // Toplanan annotationlar içinden ilkini çeker
            .ToList();  // List<CheckRunAnnotation> döndürür
    }
    public static string StripTimestamp(string logLine)
    {
        return Regex.Replace(logLine, @"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d+Z\s", "");
    }

    // Blob satırı eşikleri - gerçek log satırları ölçülerek kalibre edildi:
    //
    //   satır tipi                          boşluk%
    //   uzun stack trace satırı (213 kr)        4.7   <- en sıkışık gerçek satır
    //   NU1101 restore hatası (266 kr)          6.8
    //   uzun assert mesajı (194 kr)            16.0
    //   base64 / hex / tekrarlı blob            0.0
    //
    // Boşluk oranı tek başına ayırt edici: blob'lar %0, gerçek log satırlarının
    // en sıkışığı bile %4.7. Eşik %2 - ikisinin ortası değil, gerçek satırlardan
    // uzak durup blob'ları rahat yakalayan taraf.
    //
    // Entropi ikinci bir ölçüt olarak denendi ve çıkarıldı: ölçülen 7 gürültü
    // vakasının 6'sını zaten boşluk kuralı yakalıyordu (bkz. ROADMAP.md).
    private const int MinBlobLineLength = 200;
    private const double MaxBlobWhitespaceRatio = 0.02;
    private const int RepeatCollapseThreshold = 3;

    // Tek bir satırı gürültü açısından değerlendirir. Silmek yerine yer tutucuyla
    // değiştiriyoruz: bazen kök nedenin kendisi "adım devasa bir blob bastı"
    // olduğu için LLM'in orada bir şey olduğunu görmesi gerekiyor.
    public static string SanitizeLine(string line)
    {
        if (line.Length < MinBlobLineLength)
            return line;

        var whitespace = 0;
        foreach (var c in line)
            if (char.IsWhiteSpace(c)) whitespace++;

        if ((double)whitespace / line.Length < MaxBlobWhitespaceRatio)
            return $"[uzun/binary satır kırpıldı, {line.Length} karakter]";

        return line;
    }

    // Blok kurulmadan ÖNCE çalışan tek geçişli temizlik: timestamp strip ->
    // satır bazlı blob kırpma -> ardışık tekrar birleştirme.
    // Sıra önemli: timestamp'i önce atmazsak base64 satırının başındaki
    // "2026-08-04T..." öneki "boşluk var" sayılıp blob kuralını bozuyor.
    private static List<string> NormalizeLines(string rawLog)
    {
        var result = new List<string>();
        string? previous = null;
        var repeatCount = 0;

        void Flush()
        {
            if (previous is null) return;

            result.Add(previous);
            if (repeatCount >= RepeatCollapseThreshold)
                result.Add($"[satır {repeatCount} kez tekrarlandı]");
        }

        foreach (var rawLine in rawLog.Split('\n'))
        {
            var line = SanitizeLine(StripTimestamp(rawLine));

            if (line == previous)
            {
                repeatCount++;
                continue;
            }

            Flush();
            previous = line;
            repeatCount = 1;
        }

        Flush();
        return result;
    }

    //API üzerinden gelen metadata
    public static List<string> ExtractStepBlocks(string rawLog)
    {
        // NormalizeLines timestamp'i zaten attı, burada tekrar StripTimestamp yok.
        var lines = NormalizeLines(rawLog);
        var blocks = new List<string>();
        var currentBlock = new List<string>();

        foreach (var line in lines)
        {
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
        RegexOptions.Singleline,
        matchTimeout: TimeSpan.FromSeconds(2));

    // GitHub Actions runner'ında stack trace path'i "/home/runner/work/{repo}/{repo}/"
    // ile başlar; bu sabit öneki atıp geriye repo kökünden relative path bırakıyoruz
    // (GetFileContentAsync Contents API'yi bu formatta bekliyor). Önek eşleşmezse
    // (lokal/farklı CI) path olduğu gibi korunur — path karakter sınıfı '/' içerdiği
    // için mutlak yol da tek parça yakalanır. ExtractGenericError'ın derleyici/restore
    // dallarıyla aynı davranış.
    private static readonly Regex StackTraceFileLineRegex = new(
        @"in\s+(?:/home/runner/work/[^/]+/[^/]+/)?(?<path>[\w./-]+\.cs):line\s+(?<line>\d+)",
        RegexOptions.None,
        matchTimeout: TimeSpan.FromSeconds(2));

    // RawBlock: bu failure'a ait "Failed <ad> [...] ... Stack Trace: ..." metninin
    // TAMAMI (regex eşleşmesinin kendisi) - BuildFilteredTestLog'un konumu bilinmeyen
    // failure'lar için ham kanıtı seçici olarak geri koyabilmesi için saklanıyor.
    private sealed record NamedTestFailure(string Name, string? FilePath, int? LineNumber, string Message, string RawBlock);

    private static List<NamedTestFailure> ExtractNamedTestFailures(string stepBlock)
    {
        var failures = new List<NamedTestFailure>();

        foreach (Match m in TestFailureRegex.Matches(stepBlock))
        {
            var stackMatch = StackTraceFileLineRegex.Match(m.Groups["stack"].Value);
            failures.Add(new NamedTestFailure(
                m.Groups["name"].Value,
                stackMatch.Success ? stackMatch.Groups["path"].Value : null,
                stackMatch.Success ? int.Parse(stackMatch.Groups["line"].Value) : null,
                m.Groups["msg"].Value.Trim(),
                m.Value.TrimEnd()));
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
            @"(?:/home/runner/work/[^/]+/[^/]+/)?(?<path>[\w./-]+\.cs)\((?<line>\d+),\d+\)\s*:\s*error\s+(?<code>\w+\d*)\s*:\s*(?<msg>.+?)\s*(?:\[.*\])?\s*$",
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
        var failureList = new List<Failure>();

        foreach (var block in stepBlocks)
        {
            var failures = ExtractNamedTestFailures(block);
            if (failures.Count > 0)
            {
                // Her test kendi konumu ve (konumsuzsa) ham kanıtıyla ayrı bir Failure.
                foreach (var f in failures)
                    failureList.Add(new Failure
                    {
                        Kind = FailureKind.Test,
                        Name = f.Name,
                        JobName = job.Name,
                        StepName = failedStep.Name,
                        FilePath = f.FilePath,
                        LineNumber = f.LineNumber,
                        Message = f.Message,
                        RawEvidence = (f.FilePath != null && f.LineNumber != null) ? null : f.RawBlock
                    });

                // Hepsi konumluysa RawStepLog zaten LlmService'e hiç gönderilmeyecek
                // (bkz. ErrorContext.AllFailuresLocated), içeriği önemsiz.
                // En az biri konumsuzsa blok bazlı akıllı seçim uygulanıyor.
                matchingBlock = failures.All(f => f.FilePath != null && f.LineNumber != null)
                    ? TrimToTestSummary(block)
                    : BuildFilteredTestLog(block, failures);
                break;
            }
        }

        if (failureList.Count == 0)
        {
            foreach (var block in stepBlocks)
            {
                var (fp, ln, msg) = ExtractGenericError(block);
                if (msg != null)
                {
                    matchingBlock = block;
                    failureList.Add(new Failure
                    {
                        Kind = ClassifyGenericError(msg),
                        JobName = job.Name,
                        StepName = failedStep.Name,
                        FilePath = fp,
                        LineNumber = ln,
                        Message = msg,
                        RawEvidence = (fp != null && ln != null) ? null : block
                    });
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
            Failures = failureList
        };
    }

    /// <summary>
    /// Bir run'daki BİRDEN FAZLA başarısız job'ı tek ErrorContext'te birleştirir.
    /// Tek job verilirse çıktı, tekil BuildErrorContext ile bire bir aynıdır
    /// (aynı overload'a delege ediyor). Çoklu job'da her failure kendi JobName/
    /// StepName'ini taşımaya devam eder; ham log job başlıklarıyla birleştirilir.
    /// </summary>
    public static ErrorContext? BuildErrorContext(IReadOnlyList<JobLog> jobLogs)
    {
        var contexts = jobLogs
            .Select(j => BuildErrorContext(j.Job, j.Annotations, j.RawLog))
            .Where(c => c is not null)
            .Select(c => c!)
            .ToList();

        if (contexts.Count == 0)
            return null;
        if (contexts.Count == 1)
            return contexts[0];

        var rawParts = contexts
            .Where(c => !string.IsNullOrWhiteSpace(c.RawStepLog))
            .Select(c => $"### {c.JobName} / {c.FailedStepName}\n{c.RawStepLog}")
            .ToList();

        return new ErrorContext
        {
            JobName = string.Join(", ", contexts.Select(c => c.JobName).Distinct()),
            FailedStepName = string.Join(", ", contexts.Select(c => c.FailedStepName).Distinct()),
            RawStepLog = rawParts.Count > 0 ? string.Join("\n\n", rawParts) : null,
            FilteredAnnotations = contexts.SelectMany(c => c.FilteredAnnotations).Distinct().ToList(),
            Failures = contexts.SelectMany(c => c.Failures).ToList()
        };
    }

    // ExtractGenericError mesajı "CODE: ..." önekiyle döndürür (NU1101, CS1002, ...).
    // Fallback (##[error]) dalında önek yoktur -> Generic.
    private static FailureKind ClassifyGenericError(string message)
    {
        if (Regex.IsMatch(message, @"^NU\d")) return FailureKind.Restore;
        if (Regex.IsMatch(message, @"^(CS|NETSDK|MSB)\d")) return FailureKind.Compiler;
        return FailureKind.Generic;
    }

    // Kör char-kesme yerine blok bazlı akıllı seçim: konumu (dosya:satır)
    // zaten bilinen failure'ların stack trace'i atlanıyor - o bilgi Ayrıştırılmış
    // hata mesajı'nda (ErrorMessage) zaten var. Sadece konumu bilinmeyen failure'ların
    // TAM ham bloğu korunuyor, çünkü LLM'in kendi çıkarım yapabilmesi için asıl
    // ihtiyaç duyduğu kanıt bu. Bu fonksiyon yalnızca en az bir failure'ın konumu
    // bilinmediğinde çağrılıyor (aksi halde RawStepLog zaten LlmService'e hiç gitmiyor).
    // Not: LlmService artık ham logu kırpmıyor - çok büyükse analizi tamamen
    // atlıyor (MaxPromptChars). Yani buradaki seçicilik doğrudan "analiz yapılıp
    // yapılmayacağını" etkiliyor, sadece token tasarrufu değil.
    private static string BuildFilteredTestLog(string stepBlock, List<NamedTestFailure> failures)
    {
        var firstFailedMatch = Regex.Match(stepBlock, @"^\s*Failed\s+\S+\s*\[", RegexOptions.Multiline);
        var header = firstFailedMatch.Success ? stepBlock[..firstFailedMatch.Index].TrimEnd() : "";

        var sb = new StringBuilder();
        if (header.Length > 0)
        {
            sb.AppendLine(header);
            sb.AppendLine();
        }

        var skippedCount = 0;
        foreach (var f in failures)
        {
            if (f.FilePath != null && f.LineNumber != null)
            {
                skippedCount++;
                continue;
            }

            sb.AppendLine(f.RawBlock);
            sb.AppendLine();
        }

        if (skippedCount > 0)
            sb.AppendLine($"[{skippedCount} konumu bilinen test için ham stack trace atlandı — bkz. Ayrıştırılmış hata mesajı]");

        var summaryMatch = Regex.Match(stepBlock, @"^.*Failed!\s*-\s*Failed:.*$", RegexOptions.Multiline);
        sb.AppendLine(summaryMatch.Success
            ? summaryMatch.Value.Trim()
            : "[Özet satırı bulunamadı, muhtemelen step timeout/crash oldu]");

        return sb.ToString().TrimEnd();
    }

    private static string TrimToTestSummary(string stepBlock)
    {
        var summaryMatch = Regex.Match(stepBlock, @"^.*Failed!\s*-\s*Failed:.*$", RegexOptions.Multiline);
        return summaryMatch.Success
            ? stepBlock[..(summaryMatch.Index + summaryMatch.Length)]
            : stepBlock + "\n\n[Özet satırı bulunamadı, muhtemelen step timeout/crash oldu]";
    }

    private static string TrimPostJobNoise(string stepBlock)
    {
        var idx = stepBlock.IndexOf("Post job cleanup.", StringComparison.Ordinal);
        return idx >= 0 ? stepBlock[..idx].TrimEnd() : stepBlock;
    }

}
