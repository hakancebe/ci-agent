using System.Text.RegularExpressions;

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

    // --- Yer tutucu (placeholder) koruması --------------------------------
    //
    // Gözlenen davranış: tanımsız bir ada (CS0103) rastlayan model, değeri
    // koddan çıkaramadığında BOŞ dönmek yerine derlemeyi geçirecek bir literal
    // uyduruyor. Üç canlı denemede üç farklı varyant çıktı:
    //   Console.WriteLine(tanimsizDegisken)  ->  Console.WriteLine("örnek metin")
    //                                        ->  Console.WriteLine("Bir değer")
    //                                        ->  Console.WriteLine("")
    // Üçü de derlenir, üçü de testleri geçer (satırın teste etkisi yok) ve üçü
    // de hatayı DÜZELTMEZ, gizler. Prompt'la üç kez engellenmeye çalışıldı,
    // tutmadı: görevin çerçevesi "CI'ı yeşile döndür" olduğu sürece model
    // derlenen bir yol buluyor. Bu yüzden kural artık burada, olasılığa bağlı
    // olmayan bir yerde.

    /// <summary>CS0103 mesajlarından tanımsız ad(lar)ı çıkarır.</summary>
    private static readonly Regex UndefinedNamePattern =
        new(@"CS0103[^']*'([^']+)'", RegexOptions.Compiled);

    /// <summary>
    /// String/char/sayı literalleri. Yer tutucu tespitinde "yeni literal geldi mi"
    /// sorusunu cevaplamak için kullanılıyor.
    /// </summary>
    private static readonly Regex LiteralPattern =
        new("\"(?:[^\"\\\\]|\\\\.)*\"|'(?:[^'\\\\]|\\\\.)*'|\\b\\d+(?:\\.\\d+)?\\b",
            RegexOptions.Compiled);

    /// <summary>
    /// Hata mesajlarında geçen CS0103 tanımsız adlarını toplar.
    /// </summary>
    public static IReadOnlyList<string> UndefinedNamesFrom(IEnumerable<string> messages) =>
        messages
            .SelectMany(m => UndefinedNamePattern.Matches(m).Select(x => x.Groups[1].Value))
            .Distinct(StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// Bu değişiklik tanımsız bir adı GERÇEKTEN çözüyor mu, yoksa yalnızca
    /// derleyiciyi susturuyor mu? Sebep döner, sorun yoksa null.
    ///
    /// Kural bilinçli olarak BEYAZ LİSTE: kötü biçimleri tek tek saymak kaybedilen
    /// bir oyun oldu — canlıda dört tur, dört kaçış yolu çıktı (uydurma literal,
    /// farklı literal, boş string, satırı yorum yapmak) ve sıradakiler hazırdı
    /// (satırı silmek, ';' bırakmak, #if false). Bu yüzden artık MEŞRU olan iki
    /// biçim tanımlanıyor, gerisi reddediliyor:
    ///
    ///   1) Yazım hatası: ad, kapsamdaki BAŞKA BİR ADLA değiştirilir
    ///      (a + bbb -> a + b). Yeni bir tanımlayıcı gelir, literal gelmez.
    ///   2) Tanımlama: ad canlı kodda KALIR, yanına tanımı eklenir.
    ///
    /// Karşılaştırma yorumlar AYIKLANARAK yapılıyor: satırı yorum yapmak adı
    /// metinsel olarak korur ama anlamsal olarak yok eder — ilk sürüm tam bu
    /// yüzden atlamıştı.
    /// </summary>
    public static string? RejectPlaceholderEdit(
        CodeEdit edit, IReadOnlyCollection<string> undefinedNames)
    {
        var oldLive = StripComments(edit.OldText);
        var newLive = StripComments(edit.NewText);

        foreach (var name in undefinedNames)
        {
            // Bu edit o adı hiç ilgilendirmiyorsa konumuz değil.
            if (!ContainsIdentifier(oldLive, name))
                continue;

            // Ad canlı kodda hâlâ duruyorsa: ya tanımlanmış ya da o satıra
            // dokunulmamış. İkisi de meşru; gerçekten düzelip düzelmediğine
            // doğrulama (derleme + test) karar verir.
            if (ContainsIdentifier(newLive, name))
                continue;

            // Buradan sonrası tehlikeli bölge: ad CANLI KODDAN kayboldu, yani
            // değişiklik derlenecek ve "düzeltildi" gibi görünecek.
            var literals = IntroducedLiterals(oldLive, newLive);
            if (literals.Count > 0)
            {
                return $"tanımsız '{name}' adı, kodda dayanağı olmayan bir literal "
                     + $"({string.Join(", ", literals)}) ile değiştirilmiş — bu hatayı "
                     + "düzeltmez, gizler. Adın ne olması gerektiği koddan "
                     + "çıkarılamıyorsa edits'i BOŞ bırak.";
            }

            // Literal yok ama yerine yeni bir AD da gelmediyse, kod düzeltilmedi:
            // yorum satırına alındı, silindi ya da başka bir yolla etkisizleştirildi.
            if (IntroducedIdentifiers(oldLive, newLive).Count == 0)
            {
                return $"tanımsız '{name}' adı düzeltilmemiş, kod etkisizleştirilmiş "
                     + "(yorum satırına alınmış, silinmiş ya da boşaltılmış) — bu hatayı "
                     + "düzeltmez, gizler. Adı ya kapsamdaki doğru adla değiştir, ya "
                     + "tanımla, ya da edits'i BOŞ bırak.";
            }
        }

        return null;
    }

    /// <summary>Ad, metinde tam bir tanımlayıcı olarak geçiyor mu? (abbbc içindeki bbb sayılmaz.)</summary>
    private static bool ContainsIdentifier(string text, string name) =>
        Regex.IsMatch(text, $@"(?<![A-Za-z0-9_]){Regex.Escape(name)}(?![A-Za-z0-9_])");

    /// <summary>
    /// Satır ve blok yorumlarını çıkarır. Amaç metni derlemek değil, "bu kod
    /// gerçekten çalışıyor mu" sorusuna yaklaşık ama işe yarar bir cevap vermek.
    /// </summary>
    private static string StripComments(string text) =>
        Regex.Replace(text, @"/\*.*?\*/|//[^\n]*", "", RegexOptions.Singleline);

    /// <summary>
    /// newText'te olup oldText'te olmayan tanımlayıcılar. Literal benzeri anahtar
    /// kelimeler (null, true, false, default) DIŞARIDA: onlarla değiştirmek de
    /// yer tutucudur, "başka bir ad kullandı" sayılmamalı.
    /// </summary>
    private static List<string> IntroducedIdentifiers(string oldText, string newText)
    {
        var before = IdentifierPattern.Matches(oldText).Select(m => m.Value).ToHashSet(StringComparer.Ordinal);

        return IdentifierPattern.Matches(newText)
            .Select(m => m.Value)
            .Where(id => !before.Contains(id) && !LiteralKeywords.Contains(id))
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    private static readonly Regex IdentifierPattern =
        new(@"[A-Za-z_][A-Za-z0-9_]*", RegexOptions.Compiled);

    private static readonly HashSet<string> LiteralKeywords =
        new(StringComparer.Ordinal) { "null", "true", "false", "default" };

    /// <summary>
    /// newText'te olup oldText'te olmayan literaller. Çokluk korunuyor: aynı
    /// literal eskide bir, yenide iki kez geçiyorsa biri yenidir.
    /// </summary>
    private static List<string> IntroducedLiterals(string oldText, string newText)
    {
        var introduced = LiteralPattern.Matches(newText).Select(m => m.Value).ToList();

        foreach (var existing in LiteralPattern.Matches(oldText).Select(m => m.Value))
            introduced.Remove(existing);

        return introduced;
    }

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
