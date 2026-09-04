namespace CiAgent.Core;

/// <summary>
/// Bir düzeltmenin UYGULANMADAN ÖNCE geçmesi gereken kurallar. LLM'in ürettiği
/// yol ve içerik güvenilmez girdi sayılır: model halüsinasyon yapabilir, log'a
/// gömülmüş bir talimat modeli yönlendirmiş olabilir. Bu yüzden karar burada,
/// tek ve test edilebilir bir yerde veriliyor.
/// </summary>
public static class FixPolicy
{
    /// <summary>
    /// Tek bir çalıştırmada uygulanabilecek en fazla değişiklik. Üstü "agent
    /// projeyi yeniden yazıyor" demektir; insan incelemesi olmadan istemeyiz.
    /// </summary>
    public const int MaxEdits = 10;

    /// <summary>Tek bir değişikliğin en fazla boyutu (eski + yeni metin, karakter).</summary>
    public const int MaxEditChars = 8_000;

    /// <summary>
    /// Yol güvenli mi? Reddedilme sebebini döner, sorun yoksa null.
    ///
    /// En önemlisi ilk kural: repo dışına çıkan yollar (../.. , /etc/passwd,
    /// C:\...) kesinlikle reddedilir — aksi halde LLM'e verilen bir metin
    /// agent'ı runner üzerinde rastgele dosya yazmaya ikna edebilirdi.
    /// </summary>
    public static string? RejectPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return "dosya yolu boş";

        // Ters bölü Windows yolu ya da kaçış denemesi olabilir; tek biçime indiriyoruz.
        var normalized = path.Replace('\\', '/').Trim();

        if (normalized.StartsWith('/') || (normalized.Length > 1 && normalized[1] == ':'))
            return $"mutlak yol kabul edilmiyor: '{path}'";

        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);

        if (segments.Contains(".."))
            return $"repo dışına çıkan yol kabul edilmiyor: '{path}'";

        if (!normalized.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            return $"yalnızca .cs dosyaları düzenlenebilir: '{path}'";

        // Workflow'lar, izinler ve agent'ın kendi tetikleyicileri burada.
        // Agent'ın kendi güvenlik kurallarını değiştirebilmesi kabul edilemez.
        if (segments.Length > 0 && segments[0].Equals(".github", StringComparison.OrdinalIgnoreCase))
            return $".github/ altındaki dosyalar düzenlenemez: '{path}'";

        // Test dosyalarına dokunmak yasak: LLM bir hatayı "düzeltmenin" en kolay
        // yolu olarak testi zayıflatmayı ya da silmeyi seçebilir. Doğrulama
        // döngüsü de anlamını yitirirdi - kendi sınavını yazan öğrenci olurdu.
        if (IsTestPath(segments, normalized))
            return $"test dosyaları düzenlenemez: '{path}'";

        return null;
    }

    private static bool IsTestPath(string[] segments, string normalized)
    {
        var fileName = segments.Length > 0 ? segments[^1] : normalized;

        // "FooTests.cs" / "FooTest.cs" bir test dosyasıdır; ama dosya adının TAMAMI
        // "Tests.cs" / "Test.cs" ise (önünde ad yok) bu sıradan bir kaynak dosyası
        // olabilir - src/Core/Tests.cs gibi. Bunu isimden test sayıp /fix'i komple
        // durdurmak yanlış pozitif üretiyordu; dizin sinyali (aşağıda) zaten daha
        // güçlü ve gerçek test projelerini yakalıyor.
        if (HasNamePrefixBefore(fileName, "Tests.cs") || HasNamePrefixBefore(fileName, "Test.cs"))
            return true;

        // Dizin adında "Tests"/"Test" geçen her şey (CiAgent.Tests/, test/, src/Test/)
        return segments[..^1].Any(s =>
            s.Equals("test", StringComparison.OrdinalIgnoreCase) ||
            s.Equals("tests", StringComparison.OrdinalIgnoreCase) ||
            s.EndsWith(".Tests", StringComparison.OrdinalIgnoreCase) ||
            s.EndsWith(".Test", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// <paramref name="fileName"/> <paramref name="suffix"/> ile bitiyor mu VE
    /// suffix'ten önce en az bir karakter ad var mı? ("FooTests.cs" evet,
    /// "Tests.cs" hayır.)
    /// </summary>
    private static bool HasNamePrefixBefore(string fileName, string suffix) =>
        fileName.Length > suffix.Length
        && fileName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase);

    /// <summary>Değişikliğin içeriği kabul edilebilir mi? Sebep döner, sorun yoksa null.</summary>
    public static string? RejectEdit(CodeEdit edit)
    {
        var pathProblem = RejectPath(edit.File);
        if (pathProblem is not null)
            return pathProblem;

        if (string.IsNullOrEmpty(edit.OldText))
            return "aranacak metin boş — dosyanın tamamını değiştirmeye çalışıyor olabilir";

        if (edit.OldText == edit.NewText)
            return "eski ve yeni metin aynı, değişiklik yok";

        var size = edit.OldText.Length + edit.NewText.Length;
        if (size > MaxEditChars)
            return $"değişiklik çok büyük ({TurkishNumber.Group(size)} karakter, sınır {TurkishNumber.Group(MaxEditChars)})";

        return null;
    }
}
