namespace CiAgent.Core;

/// <summary>
/// Patlayan bir testin ADINDAN, test ettiği tipin kaynak dosyasını bulur.
///
/// Neden gerekli: bir test patladığında hata konumu TEST dosyasını gösteriyor,
/// dolayısıyla kod kesiti de testten alınıyor ve ajan testin çağırdığı bozuk
/// metodu HİÇ GÖRMÜYOR. Canlıda doğrulandı — Calculator.Add toplama yerine
/// çıkarma yapacak şekilde bozulduğunda analiz şunu dedi: "Add metodunun kendisi
/// gösterilmediği için hatanın kesin sebebi koddan çıkarılamamaktadır" ve
/// düzeltmeyi reddetti. Teşhis doğruydu, elindeki veri eksikti.
///
/// Sezgi .NET'in yerleşik adlandırma geleneğine dayanıyor: CalculatorTests
/// sınıfı Calculator'ı test eder. Kesin değil, ama YANLIŞ tahminin maliyeti
/// düşük — prompt'a alakasız bir dosya girer, o kadar. Doğru tahminin kazancı
/// ise bütün bir hata sınıfının düzeltilebilir hale gelmesi.
///
/// İKİ GİRİŞ NOKTASI var, çünkü elimizdeki bilgi dile göre değişiyor:
///
///   .NET'te ayrıştırıcı testin ADINI çıkarıyor ("CalculatorTests.Add"), o yüzden
///   <see cref="SubjectTypeName"/> sınıf adından gidiyor.
///
///   Diğer dillerde ayrıştırıcı adı çıkaramıyor — elimizde yalnızca logda geçen
///   test DOSYASININ adı var. <see cref="SubjectFileName"/> onu kullanıyor.
///   Canlıda ölçüldü (pilot run 34196510936): Python'da agent doğru teşhis koydu
///   ama "add fonksiyonunun kodu verilmediği için düzeltme önerilemiyor" dedi ve
///   hatayı calculator.py yerine test dosyasında gösterdi.
/// </summary>
public static class TestSubjectResolver
{
    // Sıra önemli: "Tests" önce denenmeli, yoksa "CalculatorTests" için "Test"
    // eşleşir ve geriye "Calculators" kalırdı.
    private static readonly string[] Suffixes = ["Tests", "Test", "Specs", "Spec", "Facts"];

    /// <summary>
    /// "CiPilot.Core.Tests.CalculatorTests.Add_ReturnsSum" -> "Calculator".
    /// Ad çözülemezse (bilinen bir sonek yok, biçim beklenmedik) null.
    /// </summary>
    public static string? SubjectTypeName(string? testName)
    {
        if (string.IsNullOrWhiteSpace(testName))
            return null;

        // Parametreli (Theory) testlerde ad "Foo.BarTests.Baz(x: 1)" gibi gelir.
        var name = testName.Split('(')[0].Trim();

        var parts = name.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
            return null;

        // Son parça metot adı; ondan önceki sınıf adı.
        var className = parts[^2];

        foreach (var suffix in Suffixes)
        {
            // Length kontrolü şart: sınıfın adı düpedüz "Tests" ise geriye boş
            // bir tip adı kalır ve her .cs dosyası eşleşmeye aday olurdu.
            if (className.Length > suffix.Length
                && className.EndsWith(suffix, StringComparison.Ordinal))
            {
                return className[..^suffix.Length];
            }
        }

        return null;
    }

    /// <summary>
    /// Bir test DOSYASININ adından, test ettiği kaynak dosyanın adını çıkarır.
    /// "test_calculator.py" -> "calculator.py", "calculator.test.js" -> "calculator.js".
    /// Ad bir test dosyası gibi görünmüyorsa null.
    ///
    /// Diller ayrı ayrı ele alınmıyor; ele alınan şey ADLANDIRMA GELENEĞİ. Aynı
    /// gelenek birden çok dilde geçerli (ör. "_test" hem Go hem Python'da).
    /// Uzantı korunuyor: Go testi Go dosyasını, Python testi Python dosyasını
    /// gösterir.
    /// </summary>
    public static string? SubjectFileName(string? testFilePath)
    {
        if (string.IsNullOrWhiteSpace(testFilePath))
            return null;

        var fileName = testFilePath.Replace('\\', '/').Split('/')[^1];

        var dot = fileName.LastIndexOf('.');
        if (dot <= 0 || dot == fileName.Length - 1)
            return null;

        var stem = fileName[..dot];
        var extension = fileName[dot..];

        var subject = StripTestMarker(stem);
        return subject is null ? null : subject + extension;
    }

    /// <summary>
    /// Uzantısız addan test işaretini söker. Bulunamazsa null — yani "bu bir test
    /// dosyası değil".
    /// </summary>
    private static string? StripTestMarker(string stem)
    {
        // JS/TS: "calculator.test.js" -> uzantı atıldıktan sonra "calculator.test"
        foreach (var infix in InfixMarkers)
        {
            if (stem.Length > infix.Length && stem.EndsWith(infix, StringComparison.OrdinalIgnoreCase))
                return stem[..^infix.Length];
        }

        // Python/Ruby: "test_calculator"
        foreach (var prefix in PrefixMarkers)
        {
            if (stem.Length > prefix.Length && stem.StartsWith(prefix, StringComparison.Ordinal))
                return stem[prefix.Length..];
        }

        // Go/Python/Ruby: "calculator_test"
        foreach (var suffix in SnakeSuffixMarkers)
        {
            if (stem.Length > suffix.Length && stem.EndsWith(suffix, StringComparison.Ordinal))
                return stem[..^suffix.Length];
        }

        // Java/C#/PHP: "CalculatorTest". Büyük harf ŞART — yoksa "latest.py"
        // gibi masum bir ad test dosyası sanılırdı.
        foreach (var suffix in Suffixes)
        {
            if (stem.Length > suffix.Length && stem.EndsWith(suffix, StringComparison.Ordinal))
                return stem[..^suffix.Length];
        }

        return null;
    }

    private static readonly string[] InfixMarkers = [".test", ".spec"];
    private static readonly string[] PrefixMarkers = ["test_", "Test_"];
    private static readonly string[] SnakeSuffixMarkers = ["_test", "_spec", "_tests", "_specs"];

    /// <summary>
    /// Aday tip adı için repodaki kaynak dosya yollarını seçer.
    ///
    /// Eleme <see cref="FixPolicy.RejectPath"/> ile yapılıyor — bilinçli bir
    /// yeniden kullanım: burada bulduğumuz dosya, /fix'in DÜZENLEMESİNE izin
    /// verilen dosya olmalı. Aksi halde modele gösterip sonra "dokunamam"
    /// demiş olurduk.
    /// </summary>
    public static IReadOnlyList<string> MatchSourceFiles(
        string subjectTypeName, IEnumerable<string> repoFilePaths)
    {
        if (string.IsNullOrWhiteSpace(subjectTypeName))
            return [];

        return MatchByFileName(subjectTypeName + ".cs", repoFilePaths)
            .Where(p => FixPolicy.RejectPath(p) is null)
            .ToList();
    }

    /// <summary>
    /// <see cref="MatchSourceFiles"/>'ın dil bağımsız kardeşi: hedef zaten tam
    /// dosya adı.
    ///
    /// <see cref="FixPolicy.RejectPath"/> ile ELENMİYOR, çünkü o politika yalnızca
    /// .cs'e izin veriyor ve buradaki amaç farklı: dosyayı /fix düzenleyebilsin
    /// diye değil, model SEBEBİ görebilsin diye ekliyoruz. .NET dışı bir dilde
    /// /fix zaten devreye girmiyor; rapor da bunu açıkça yazıyor.
    ///
    /// Yine de iki eleme var: kendisi test dosyası olanlar (aradığımız şey testin
    /// test ETTİĞİ dosya) ve bağımlılık klasörleri (repoya commit'lenmiş bir
    /// node_modules kopyası doğru dosyayı gölgeleyebilirdi).
    /// </summary>
    public static IReadOnlyList<string> MatchSourceFilesByName(
        string subjectFileName, IEnumerable<string> repoFilePaths)
    {
        if (string.IsNullOrWhiteSpace(subjectFileName))
            return [];

        return MatchByFileName(subjectFileName, repoFilePaths)
            .Where(p => SubjectFileName(p) is null)
            .Where(p => !IsVendorPath(p))
            .ToList();
    }

    private static IEnumerable<string> MatchByFileName(
        string target, IEnumerable<string> repoFilePaths) =>
        repoFilePaths.Where(p => p.Replace('\\', '/').Split('/')[^1]
                                  .Equals(target, StringComparison.OrdinalIgnoreCase));

    private static bool IsVendorPath(string path) =>
        path.Replace('\\', '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Any(seg => VendorDirectories.Contains(seg));

    private static readonly HashSet<string> VendorDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "node_modules", "vendor", "site-packages", "dist", "build",
        "bin", "obj", ".venv", "venv", "third_party"
    };
}
