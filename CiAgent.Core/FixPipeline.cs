using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CiAgent.Core;

public enum FixStatus
{
    /// <summary>Değişiklik uygulandı ve doğrulama (build + test) geçti.</summary>
    Fixed,

    /// <summary>Analizden düzenlenebilir bir kaynak dosya çıkmadı.</summary>
    NoSourceFiles,

    /// <summary>LLM güvenli bir düzeltme öneremedi (boş öneri ya da prompt sığmadı).</summary>
    NoProposal,

    /// <summary>Öneriler politika ya da eşleşme nedeniyle uygulanamadı.</summary>
    EditsRejected,

    /// <summary>Değişiklik uygulandı ama testler hâlâ kırık — her şey geri alındı.</summary>
    VerificationFailed
}

public sealed record FixOutcome(
    FixStatus Status,
    string Summary,
    IReadOnlyList<EditOutcome> Edits,
    int Attempts,
    string? VerificationOutput = null)
{
    public bool Succeeded => Status == FixStatus.Fixed;

    public IEnumerable<CodeEdit> AppliedEdits => Edits.Where(e => e.Applied).Select(e => e.Edit);
}

/// <summary>
/// /fix akışı: ilgili dosyaları oku → LLM'den değişiklik iste → uygula →
/// derle ve test et → geçtiyse bırak, geçmediyse geri al ve tekrar dene.
///
/// Buradaki asıl fikir doğrulama döngüsü: LLM'in önerisine körlemesine
/// güvenilmiyor, testler geçene kadar hiçbir şey "düzeltildi" sayılmıyor.
/// </summary>
public sealed class FixPipeline
{
    /// <summary>
    /// Kaç kez denenecek. Her deneme bir LLM çağrısı + tam bir build/test turu,
    /// yani hem para hem CI süresi. İkiden fazlası pratikte nadiren tutuyor.
    /// </summary>
    public const int MaxAttempts = 2;

    /// <summary>Prompt'a girecek en fazla dosya sayısı ve toplam boyutu.</summary>
    private const int MaxFiles = 5;
    private const int MaxTotalFileChars = 30_000;

    private readonly LlmService _llm;
    private readonly IVerificationRunner _verifier;
    private readonly ILogger _log;

    public FixPipeline(
        LlmService llm,
        IVerificationRunner verifier,
        ILogger<FixPipeline>? logger = null)
    {
        _llm = llm;
        _verifier = verifier;
        _log = logger ?? NullLogger<FixPipeline>.Instance;
    }

    public async Task<FixOutcome> RunAsync(
        ErrorContext context,
        AnalysisResult analysis,
        string workspaceRoot,
        bool dryRun = false)
    {
        var editor = new WorkspaceEditor(workspaceRoot);

        var files = await CollectFilesAsync(editor, context, analysis);
        if (files.Count == 0)
        {
            return new FixOutcome(
                FixStatus.NoSourceFiles,
                "Analizden düzenlenebilir bir kaynak dosya çıkmadı "
                + "(hata bir dosya:satır konumuna bağlanamamış ya da dosyalar düzenleme kuralları dışında).",
                [], 0);
        }

        _log.LogInformation("Düzeltme için {Count} dosya okundu: {Files}",
            files.Count, string.Join(", ", files.Keys));

        string? previousAttempt = null;
        var lastOutcome = new FixOutcome(FixStatus.NoProposal, "Hiç deneme yapılmadı.", [], 0);

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            _log.LogInformation("Düzeltme denemesi {Attempt}/{Max}...", attempt, MaxAttempts);

            lastOutcome = await AttemptAsync(
                editor, workspaceRoot, context, analysis, files, previousAttempt, attempt);

            if (lastOutcome.Succeeded)
                break;

            // Her başarısız denemeden sonra çalışma dizini temiz bırakılıyor ki
            // sonraki deneme yarım uygulanmış değişikliklerin üzerine binmesin.
            await editor.RevertAllAsync();

            // Tekrar denemek yalnızca modele SÖYLEYECEK YENİ BİR ŞEY varsa anlamlı:
            // "önerin uygulanamadı, şu yüzden" ya da "uyguladık, testler şöyle patladı".
            // Model zaten "güvenle düzeltemem" dediyse aynı bilgiyle ikinci kez sormak
            // sonucu değiştirmez, sadece bir LLM çağrısı daha harcar.
            if (!IsRetryable(lastOutcome.Status))
            {
                _log.LogInformation(
                    "Tekrar denenmeyecek: modele verilecek yeni bir geri bildirim yok ({Status}).",
                    lastOutcome.Status);
                break;
            }

            previousAttempt = DescribeFailure(lastOutcome);
        }

        if (lastOutcome.Succeeded && dryRun)
        {
            // Dry-run'da değişikliği diskte bırakmıyoruz; çağıran zaten commit
            // atmayacak, ama temiz bir çalışma dizini bırakmak doğru olan.
            _log.LogInformation("DRY-RUN: doğrulama geçti, değişiklikler geri alınıyor (commit atılmayacak).");
            await editor.RevertAllAsync();
        }

        return lastOutcome;
    }

    private async Task<FixOutcome> AttemptAsync(
        WorkspaceEditor editor,
        string workspaceRoot,
        ErrorContext context,
        AnalysisResult analysis,
        IReadOnlyDictionary<string, string> files,
        string? previousAttempt,
        int attempt)
    {
        var proposal = await _llm.ProposeFixAsync(context, analysis, files, previousAttempt);

        if (proposal is null)
            return new FixOutcome(FixStatus.NoProposal,
                "Düzeltme istemi boyut sınırını aştığı için LLM'e hiç gidilmedi.", [], attempt);

        if (proposal.Edits.Count == 0)
            return new FixOutcome(FixStatus.NoProposal, proposal.Summary, [], attempt);

        if (proposal.Edits.Count > FixPolicy.MaxEdits)
            return new FixOutcome(FixStatus.EditsRejected,
                $"Öneri {proposal.Edits.Count} değişiklik içeriyor, sınır {FixPolicy.MaxEdits}. "
                + "Bu kadar geniş bir değişiklik otomatik uygulanmamalı.", [], attempt);

        var outcomes = new List<EditOutcome>();
        foreach (var edit in proposal.Edits)
        {
            var outcome = await editor.ApplyAsync(edit);
            outcomes.Add(outcome);

            if (!outcome.Applied)
                _log.LogWarning("Değişiklik reddedildi ({File}): {Reason}",
                    edit.File, outcome.RejectionReason);
        }

        // Kısmi uygulama kabul edilmiyor: önerilerin bir kısmı tutup diğerleri
        // tutmadıysa ortaya modelin hiç öngörmediği bir ara durum çıkar.
        if (outcomes.Any(o => !o.Applied))
            return new FixOutcome(FixStatus.EditsRejected, proposal.Summary, outcomes, attempt);

        _log.LogInformation("{Count} değişiklik uygulandı, doğrulanıyor (derleme + testler)...",
            outcomes.Count);

        var verification = await _verifier.VerifyAsync(workspaceRoot);

        if (verification.Succeeded)
        {
            _log.LogInformation("Doğrulama geçti.");
            return new FixOutcome(FixStatus.Fixed, proposal.Summary, outcomes, attempt, verification.Output);
        }

        _log.LogWarning("Doğrulama başarısız, değişiklikler geri alınacak.");
        return new FixOutcome(FixStatus.VerificationFailed, proposal.Summary, outcomes, attempt, verification.Output);
    }

    private static bool IsRetryable(FixStatus status) =>
        status is FixStatus.EditsRejected or FixStatus.VerificationFailed;

    /// <summary>
    /// Modelin bir sonraki denemede aynı duvara toslamaması için önceki denemenin
    /// neden tutmadığını insan diliyle özetler.
    /// </summary>
    private static string DescribeFailure(FixOutcome outcome) => outcome.Status switch
    {
        FixStatus.EditsRejected =>
            "Önerdiğin değişiklikler uygulanamadı:\n"
            + string.Join("\n", outcome.Edits
                .Where(e => !e.Applied)
                .Select(e => $"- {e.Edit.File}: {e.RejectionReason}"))
            + "\noldText'i dosyadaki metinle BİREBİR ve o dosyada yalnızca bir kez geçecek şekilde ver.",

        FixStatus.VerificationFailed =>
            "Değişikliklerin uygulandı ama derleme/testler hâlâ başarısız:\n"
            + (outcome.VerificationOutput is null
                ? "(çıktı yok)"
                : new VerificationResult(false, outcome.VerificationOutput).Tail()),

        _ => "Önceki deneme sonuç vermedi."
    };

    /// <summary>
    /// Analizin işaret ettiği dosyaları toplar. Modele YALNIZCA bunlar gösteriliyor;
    /// görmediği bir dosyayı düzenlemesi zaten politika tarafından reddedilir.
    /// </summary>
    private async Task<Dictionary<string, string>> CollectFilesAsync(
        WorkspaceEditor editor, ErrorContext context, AnalysisResult analysis)
    {
        // Öncelik LLM'in "etkilenen dosya" dediğinde; sonra parser'ın bulduğu konumlar.
        var candidates = analysis.Analyses
            .Select(a => a.AffectedFile)
            .Concat(context.Failures.Select(f => f.FilePath))
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase);

        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var totalChars = 0;

        foreach (var path in candidates)
        {
            if (files.Count >= MaxFiles) break;

            if (FixPolicy.RejectPath(path) is string reason)
            {
                _log.LogInformation("Dosya atlandı ({Path}): {Reason}", path, reason);
                continue;
            }

            var content = await editor.ReadAsync(path);
            if (content is null)
            {
                _log.LogWarning("Dosya çalışma dizininde bulunamadı: {Path}", path);
                continue;
            }

            if (totalChars + content.Length > MaxTotalFileChars)
            {
                _log.LogInformation("Dosya atlandı ({Path}): toplam boyut sınırı aşılıyor.", path);
                continue;
            }

            files[path] = content;
            totalChars += content.Length;
        }

        return files;
    }
}
