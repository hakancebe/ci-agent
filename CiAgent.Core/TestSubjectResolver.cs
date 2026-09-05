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

        var target = subjectTypeName + ".cs";

        return repoFilePaths
            .Where(p => p.Replace('\\', '/').Split('/')[^1]
                         .Equals(target, StringComparison.OrdinalIgnoreCase))
            .Where(p => FixPolicy.RejectPath(p) is null)
            .ToList();
    }
}
