namespace CiAgent.Core;

public class ErrorContext
{
    /// <summary>Başarısız job(lar)ın adı; birden fazlaysa virgülle birleştirilmiş.</summary>
    public required string JobName { get; init; }

    /// <summary>
    /// Hatanın geldiği workflow'un görünen adı ("CD") ve repodaki dosya yolu
    /// (".github/workflows/cd.yml"). Bilinmiyorsa null.
    ///
    /// Neden var: canlı ölçümde model, deploy hatasında düzeltilecek dosya olarak
    /// ".github/workflows/deploy.yml" dedi — repoda öyle bir dosya YOK, gerçeği
    /// cd.yml'di. Doğru ad zaten webhook payload'ında ve Actions API'sinde
    /// duruyordu; prompt'a hiç girmediği için model boşluğu uydurarak doldurdu.
    /// Bilgiyi vermek, "uydurma" talimatı yazmaktan daha etkili.
    /// </summary>
    public WorkflowInfo? Workflow { get; set; }

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
    /// Bir failure'ın KONUMUNA bağlı olmayan ama analiz için gereken kaynak
    /// dosyalar: yol -> tam içerik.
    ///
    /// Tek kullanımı şimdilik patlayan testin test ettiği uygulama dosyası.
    /// Test hatalarında konum testi gösterdiği için kod kesiti de testten
    /// geliyordu; bozuk metodun kendisi prompt'a hiç girmiyordu. Bu sözlük o
    /// boşluğu dolduruyor (bkz. <see cref="TestSubjectResolver"/>).
    ///
    /// Kesit değil TAM içerik tutuluyor: /fix bu dosyayı düzenleyecekse
    /// oldText'i birebir eşleştirmesi gerekiyor, kırpılmış metin bunu bozar.
    /// </summary>
    public Dictionary<string, string> RelatedSources { get; set; } = new();

    /// <summary>
    /// Her failure'ın kendi dosya:satır konumu bulunmuşsa true. LlmService bunu
    /// RawStepLog'u prompt'a ekleyip eklememeye karar vermek için kullanıyor:
    /// konum zaten kesinse ham log ekstra bir şey katmıyor, sadece token israfı
    /// oluyor. Failures'tan türetiliyor - ayrıca set edilmiyor ki listeyle
    /// tutarsız kalması mümkün olmasın.
    /// </summary>
    public bool AllFailuresLocated => Failures.Count > 0 && Failures.All(f => f.IsLocated);
}
