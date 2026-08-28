namespace CiAgent.Core;

/// <summary>
/// Tek bir başarısızlığın hangi katmandan geldiği. Prompt üretimi ve raporlama
/// bu bilgiye göre farklı davranabiliyor (ör. Restore'da satır no beklenmez).
/// </summary>
public enum FailureKind
{
    Test,
    Compiler,
    Restore,
    Generic
}

/// <summary>
/// Bir başarısız adımda tespit edilen TEK bir hata. Aynı adımda birden fazla
/// olabilir (xUnit tüm fail'leri sırayla listeler; bir build birden çok CS hatası
/// basar). Eski <see cref="TestFailure"/> skaler üçlüsünün yerine geçen zengin tip:
/// her failure kendi dosya:satır'ını, ham kanıtını ve (Program.cs dolduruyor)
/// kod kesitini taşır.
/// </summary>
public sealed record Failure
{
    public required FailureKind Kind { get; init; }

    /// <summary>Test adı (yalnızca <see cref="FailureKind.Test"/> için dolu).</summary>
    public string? Name { get; init; }

    /// <summary>Bu failure'ın geldiği job — bir run'da birden fazla job fail olabilir.</summary>
    public string? JobName { get; init; }

    /// <summary>Bu failure'ın geldiği başarısız adım.</summary>
    public string? StepName { get; init; }

    /// <summary>Repo kökünden itibaren relative path; bilinmiyorsa null.</summary>
    public string? FilePath { get; init; }

    public int? LineNumber { get; init; }

    public required string Message { get; init; }

    /// <summary>
    /// Bu failure'a ait ham log bloğu. Konumu (dosya:satır) bilinen failure'larda
    /// gereksiz — ErrorMessage zaten yeterli — bu yüzden yalnızca konumsuzlarda
    /// doldurulur, LLM'in ham veriden çıkarım yapabilmesi için.
    /// </summary>
    public string? RawEvidence { get; init; }

    /// <summary>
    /// GitHub Contents API'den çekilen, <see cref="LineNumber"/> etrafındaki kod
    /// kesiti. Program.cs LLM çağrısından önce, konumu bilinen her failure için
    /// ayrı ayrı doldurur.
    /// </summary>
    public string? CodeSnippet { get; set; }

    public bool IsLocated => FilePath is not null && LineNumber is not null;
}
