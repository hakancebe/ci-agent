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

    // Path karakter sınıfı ([\w./-]) runner'ın tam absolute path'ini (örn.
    // "/home/runner/work/ci-agent-pilot/ci-agent-pilot/tests/.../Foo.cs") olduğu gibi
    // yakalar - filtreleme StripRunnerWorkPrefix'te ayrı bir adımda yapılıyor.
    private static readonly Regex StackTraceFileLineRegex = new(
        @"in\s+(?<path>[\w./-]+\.cs):line\s+(?<line>\d+)",
        RegexOptions.None,
        matchTimeout: TimeSpan.FromSeconds(2));

    // GitHub Actions runner checkout path'i "/home/runner/work/{repo}/{repo}/" şeklinde
    // repo adını iki kez tekrarlıyor (work dizini + içindeki checkout dizini aynı isimde).
    // Bu sabit öneki atıp geriye repo kökünden itibaren relative path'i bırakıyoruz -
    // GitHubService.GetFileContentAsync Contents API'yi repo-relative path bekliyor.
    // Eşleşmezse (lokal/farklı runner ortamı) path olduğu gibi bırakılır.
    private static readonly Regex RunnerWorkPrefixRegex = new(
        @"^/home/runner/work/(?<repo>[^/]+)/\k<repo>/",
        RegexOptions.None,
        matchTimeout: TimeSpan.FromSeconds(2));

    private static string StripRunnerWorkPrefix(string path)
    {
        var match = RunnerWorkPrefixRegex.Match(path);
        return match.Success ? path[match.Length..] : path;
    }

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
                stackMatch.Success ? StripRunnerWorkPrefix(stackMatch.Groups["path"].Value) : null,
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
            @"(?<path>[\w./-]+\.cs)\((?<line>\d+),\d+\)\s*:\s*error\s+(?<code>\w+\d*)\s*:\s*(?<msg>.+?)\s*(?:\[.*\])?\s*$",
            RegexOptions.Multiline);

        if (compilerMatch.Success)
        {
            return new TestFailure(
                StripRunnerWorkPrefix(compilerMatch.Groups["path"].Value),
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
                (filePath, lineNumber, errorMessage) = CombineTestFailures(failures);
                // Tek tek her failure'ın kendi dosya:satır'ı bulunmuş mu? (üstteki
                // filePath/lineNumber sadece ilk konumu bilinen failure'a ait.)
                allFailuresLocated = failures.All(f => f.FilePath != null && f.LineNumber != null);

                // Hepsi konumluysa RawStepLog zaten LlmService'e hiç gönderilmeyecek
                // (bkz. AllFailuresLocated), içeriği önemsiz - eski davranış yeterli.
                // En az biri konumsuzsa blok bazlı akıllı seçim uygulanıyor.
                matchingBlock = allFailuresLocated
                    ? TrimToTestSummary(block)
                    : BuildFilteredTestLog(block, failures);
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
