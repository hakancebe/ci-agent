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

        // İzin verilen uzantılar, doğrulayıcının ÇALIŞTIRABİLDİĞİ ekosistemlerden
        // türüyor (EcosystemDetector). İkisi ayrışırsa agent doğrulayamadığı bir
        // dosyayı düzenlerdi ve "testler geçene kadar düzeltildi sayılmaz"
        // güvencesi çökerdi.
        if (!EcosystemDetector.AllEditableExtensions.Any(
                ext => normalized.EndsWith(ext, StringComparison.OrdinalIgnoreCase)))
        {
            return $"bu dosya türü düzenlenemiyor: '{path}' "
                 + $"(desteklenen: {string.Join(", ", EcosystemDetector.AllEditableExtensions)})";
        }

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

        // Ad geleneğinden test dosyası mı? Tespit dile bağımsız ve tek yerde:
        // "FooTests.cs", "test_foo.py", "foo.test.js", "foo_test.go" hepsi burada
        // yakalanıyor (TestSubjectResolver). Dosya adının TAMAMI "Tests.cs" ise
        // (önünde ad yok) test sayılmıyor — src/Core/Tests.cs gibi sıradan bir
        // kaynak dosyası olabilir; o kural resolver'ın içinde.
        if (TestSubjectResolver.SubjectFileName(fileName) is not null)
            return true;

        // Dizin adında "Tests"/"Test" geçen her şey (CiAgent.Tests/, test/, src/Test/)
        return segments[..^1].Any(s =>
            s.Equals("test", StringComparison.OrdinalIgnoreCase) ||
            s.Equals("tests", StringComparison.OrdinalIgnoreCase) ||
            s.Equals("spec", StringComparison.OrdinalIgnoreCase) ||
            s.Equals("specs", StringComparison.OrdinalIgnoreCase) ||
            s.Equals("__tests__", StringComparison.OrdinalIgnoreCase) ||
            s.EndsWith(".Tests", StringComparison.OrdinalIgnoreCase) ||
            s.EndsWith(".Test", StringComparison.OrdinalIgnoreCase));
    }

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
        // İki ayrı görünüm gerekiyor:
        //   *NoComments — literaller DURUYOR; "yeni literal geldi mi" sorusu için.
        //   *Code       — literal İÇERİKLERİ de boşaltılmış; "ad hâlâ ÇALIŞAN kodda
        //                 mı" sorusu için. Console.WriteLine("tanimsizDegisken")
        //                 örneğinde ad metinde geçiyor ama artık kod değil VERİ —
        //                 bu ayrım yapılmazsa yer tutucu tespitten kaçıyor.
        var oldNoComments = StripComments(edit.OldText);
        var newNoComments = StripComments(edit.NewText);
        var oldCode = StripLiteralContents(oldNoComments);
        var newCode = StripLiteralContents(newNoComments);

        foreach (var name in undefinedNames)
        {
            // Bu edit o adı hiç ilgilendirmiyorsa konumuz değil.
            if (!ContainsIdentifier(oldCode, name))
                continue;

            // Ad canlı kodda hâlâ duruyorsa: ya tanımlanmış ya da o satıra
            // dokunulmamış. İkisi de meşru; gerçekten düzelip düzelmediğine
            // doğrulama (derleme + test) karar verir.
            if (ContainsIdentifier(newCode, name))
                continue;

            // Buradan sonrası tehlikeli bölge: ad CANLI KODDAN kayboldu, yani
            // değişiklik derlenecek ve "düzeltildi" gibi görünecek.
            var literals = IntroducedLiterals(oldNoComments, newNoComments);
            if (literals.Count > 0)
            {
                return $"tanımsız '{name}' adı, kodda dayanağı olmayan bir literal "
                     + $"({string.Join(", ", literals)}) ile değiştirilmiş — bu hatayı "
                     + "düzeltmez, gizler. Adın ne olması gerektiği koddan "
                     + "çıkarılamıyorsa edits'i BOŞ bırak.";
            }

            // Literal yok ama yerine yeni bir AD da gelmediyse, kod düzeltilmedi:
            // yorum satırına alındı, silindi ya da başka bir yolla etkisizleştirildi.
            if (IntroducedIdentifiers(oldCode, newCode).Count == 0)
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
    /// String/char literallerinin İÇERİĞİNİ boşaltır, tırnakları bırakır
    /// ("tanimsizDegisken" -> ""). Böylece literal içine saklanmış bir ad,
    /// "kodda hâlâ kullanılıyor" sayılmaz — literal içi kod değil, veridir.
    /// </summary>
    private static string StripLiteralContents(string text) =>
        Regex.Replace(
            Regex.Replace(text, "\"(?:[^\"\\\\]|\\\\.)*\"", "\"\""),
            "'(?:[^'\\\\]|\\\\.)*'", "''");

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

    // --- Yorumdan çıkarma işareti ---------------------------------------
    //
    // RejectPlaceholderEdit'in YÖN karşıtı. O, "kodu yoruma alıp hatayı
    // gizleme" hamlesini reddeder. Bu ise "yorumdaki kodu diriltme" hamlesini
    // yalnızca SAYAR — bloklamaz. Sebep: kodun neden yorumlandığı repodan
    // bilinemez ve çoğu zaman meşru bir düzeltmedir (geliştirici geçici
    // kapatmış). Ama /fix --commit ile inceleme atlanırsa insanın gözden
    // kaçırmaması gereken bir şey; rapor (FixReport) bunu ayrıca yazsın diye
    // buradan ölçülüyor. Ölçülmüş bir kötüye kullanım gözlenirse sert kurala
    // dönebilir — şimdilik yalnızca işaret.

    /// <summary>
    /// Bu değişikliğin <see cref="CodeEdit.OldText"/>'ten kaç tane <b>kod
    /// görünümlü yorum satırı</b> kaldırdığı — <see cref="CodeEdit.NewText"/>'te
    /// canlı kod satırı sayısı artmışsa. 0 = yorumdan çıkarma yok.
    ///
    /// Soru bilinçli olarak "eski yorum gövdesi yeni kodda AYNEN var mı?"
    /// değil: satır yorumdan çıkarılırken değiştirilebiliyor
    /// (<c>private</c> → <c>public</c>) ve birebir eşleşme aranınca sayı
    /// düşük çıkıp uyarı eşiğinin altına kayabiliyordu. Bunun yerine yalnızca
    /// "yorumdaydı, kod görünümlüydü, artık yorumda değil" sayılıyor.
    /// </summary>
    public static int UncommentedCodeLineCount(CodeEdit edit)
    {
        var oldCodeComments = CommentedCodePayloads(edit.OldText).Where(LooksLikeCode).ToList();
        if (oldCodeComments.Count == 0)
            return 0;

        // Yeni metinde HÂLÂ yorumda olanları düş: kalan = bu edit'in yorumdan
        // çıkardığı kod satırları. Eşleştirme gövde üzerinden ama yalnızca
        // "yorum → yorum" tarafında; "yorum → canlı" tarafına hiç bakılmıyor.
        foreach (var stillCommented in CommentedCodePayloads(edit.NewText).Where(LooksLikeCode))
            oldCodeComments.Remove(stillCommented);

        if (oldCodeComments.Count == 0)
            return 0;

        // Ölü bir yorum bloğunu SİLMEK de bu sayıyı şişirir. "Yorumdan çıkarma"
        // ile "silme"yi ayırmak için: canlı kod satırı sayısı artmış olmalı.
        if (LiveCodeLineCount(edit.NewText) <= LiveCodeLineCount(edit.OldText))
            return 0;

        return oldCodeComments.Count;
    }

    /// <summary>
    /// Yorum gövdesi düz açıklama değil de kod mu görünüyor? Güçlü işaretler:
    /// <c>{ } ;</c> noktalaması ya da bir tür bildirimi anahtar kelimesi
    /// (<c>class struct interface enum record namespace</c>). <c>public</c>,
    /// <c>return</c>, <c>new</c> gibi zayıf kelimeler bilerek DIŞARIDA —
    /// Türkçe/İngilizce düz yorumda da geçerler ("public API'yi bozma").
    /// </summary>
    private static bool LooksLikeCode(string commentBody) =>
        commentBody.IndexOfAny(CodePunctuation) >= 0 || StrongDeclPattern.IsMatch(commentBody);

    private static readonly char[] CodePunctuation = { '{', '}', ';' };

    private static readonly Regex StrongDeclPattern = new(
        @"(?<![A-Za-z0-9_])(class|struct|interface|enum|record|namespace)(?![A-Za-z0-9_])",
        RegexOptions.Compiled);

    /// <summary>Yorumları çıkardıktan sonra kalan boş olmayan satır sayısı.</summary>
    private static int LiveCodeLineCount(string text) =>
        StripComments(text)
            .Replace("\r\n", "\n").Split('\n')
            .Count(l => l.Trim().Length > 0);

    /// <summary>
    /// Metindeki yorum satırlarının "kod gövdesi": yorum işaretleri (<c>//</c>,
    /// <c>/*</c>, <c>*/</c>, satır başı <c>*</c>) soyulmuş ve trim'lenmiş.
    /// Salt işaret satırları ve boş gövdeler atlanır. Tam parser değil —
    /// "yorumdan çıkarılmış blok" durumunu yakalamaya yeten yaklaşık bir tarama.
    /// </summary>
    private static List<string> CommentedCodePayloads(string text)
    {
        var payloads = new List<string>();
        var inBlock = false;

        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            var t = raw.Trim();
            string? body = null;

            if (inBlock)
            {
                var end = t.IndexOf("*/", StringComparison.Ordinal);
                if (end >= 0) { body = t[..end]; inBlock = false; }
                else body = t;
                body = body.TrimStart('*').Trim();
            }
            else if (t.StartsWith("//", StringComparison.Ordinal))
            {
                body = t[2..].Trim();
            }
            else if (t.StartsWith("/*", StringComparison.Ordinal))
            {
                var rest = t[2..];
                var end = rest.IndexOf("*/", StringComparison.Ordinal);
                if (end >= 0) rest = rest[..end];
                else inBlock = true;
                body = rest.TrimStart('*').Trim();
            }

            if (!string.IsNullOrWhiteSpace(body))
                payloads.Add(body);
        }

        return payloads;
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
