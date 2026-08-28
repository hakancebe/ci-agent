namespace CiAgent.Core;

public class ErrorContext
{
    /// <summary>Başarısız job(lar)ın adı; birden fazlaysa virgülle birleştirilmiş.</summary>
    public required string JobName { get; init; }

    /// <summary>Başarısız adım(lar)ın adı; birden fazlaysa virgülle birleştirilmiş.</summary>
    public required string FailedStepName { get; init; }

    /// <summary>
    /// Başarısız adımın ham log kesiti. Yalnızca en az bir failure'ın konumu
    /// bilinmiyorsa prompt'a giriyor (bkz. <see cref="AllFailuresLocated"/>);
    /// prompt limitine sığmazsa ilk feda edilen katman bu.
    /// </summary>
    public string? RawStepLog { get; set; }

    public List<string> FilteredAnnotations { get; set; } = new();

    /// <summary>
    /// Bu run'da tespit edilen TÜM hatalar — tek kaynak. Dosya:satır, hata mesajı,
    /// kod kesiti ve ham kanıt her failure'ın kendi üzerinde; tekrarları
    /// <see cref="FailureGrouper"/> prompt/rapor üretiminde topluyor.
    /// </summary>
    public List<Failure> Failures { get; set; } = new();

    /// <summary>
    /// Her failure'ın kendi dosya:satır konumu bulunmuşsa true. LlmService bunu
    /// RawStepLog'u prompt'a ekleyip eklememeye karar vermek için kullanıyor:
    /// konum zaten kesinse ham log ekstra bir şey katmıyor, sadece token israfı
    /// oluyor. Failures'tan türetiliyor - ayrıca set edilmiyor ki listeyle
    /// tutarsız kalması mümkün olmasın.
    /// </summary>
    public bool AllFailuresLocated => Failures.Count > 0 && Failures.All(f => f.IsLocated);
}
