using CiAgent.Core;

namespace CiAgent.Tests;

/// <summary>
/// Patlayan testin ADINDAN test ettiği tipi çıkarmak, test hatalarının
/// düzeltilebilmesinin ön koşulu: hata konumu testi gösterdiği için bozuk
/// metodun dosyası başka türlü hiç bulunamıyor.
/// </summary>
public class TestSubjectResolverTests
{
    [Theory]
    [InlineData("CiPilot.Core.Tests.CalculatorTests.Add_ReturnsSum", "Calculator")]
    [InlineData("Foo.BarTest.Baz", "Bar")]
    [InlineData("A.B.OrderServiceSpecs.Ships", "OrderService")]
    [InlineData("A.B.OrderServiceFacts.Ships", "OrderService")]
    // Theory testlerinde ad parametreleri de taşıyor.
    [InlineData("CiPilot.Core.Tests.CalculatorTests.Add(a: 1, b: 2)", "Calculator")]
    public void SubjectTypeName_StripsKnownSuffixes(string testName, string expected)
    {
        Assert.Equal(expected, TestSubjectResolver.SubjectTypeName(testName));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("TekParca")]                       // nokta yok, sınıf ayrılamıyor
    [InlineData("Foo.Bar.Baz")]                    // sınıf adı bilinen sonekle bitmiyor
    // Sınıfın adı düpedüz "Tests" ise geriye boş tip adı kalır; o durumda her .cs
    // dosyası eşleşmeye aday olurdu, bu yüzden çözülemez sayılıyor.
    [InlineData("Foo.Tests.Bar")]
    public void SubjectTypeName_ReturnsNull_WhenItCannotBeDerived(string? testName)
    {
        Assert.Null(TestSubjectResolver.SubjectTypeName(testName));
    }

    [Fact]
    public void MatchSourceFiles_FindsImplementationAnywhereInRepo()
    {
        string[] repo =
        [
            "src/CiPilot.Core/Calculator.cs",
            "src/CiPilot.Core/Other.cs",
            "README.md"
        ];

        Assert.Equal(["src/CiPilot.Core/Calculator.cs"],
            TestSubjectResolver.MatchSourceFiles("Calculator", repo));
    }

    [Fact]
    public void MatchSourceFiles_SkipsTestFiles_BecauseTheFixMustLandInProductionCode()
    {
        // Aynı adı taşıyan bir test dosyası varsa o değil, uygulama dosyası
        // seçilmeli — /fix zaten test dosyalarını düzenleyemiyor.
        string[] repo =
        [
            "tests/CiPilot.Core.Tests/Calculator.cs",
            "src/CiPilot.Core/Calculator.cs"
        ];

        Assert.Equal(["src/CiPilot.Core/Calculator.cs"],
            TestSubjectResolver.MatchSourceFiles("Calculator", repo));
    }

    [Fact]
    public void MatchSourceFiles_DoesNotMatchOnPartialNames()
    {
        // "Calculator" ararken "CalculatorFactory.cs" eşleşmemeli.
        string[] repo = ["src/CalculatorFactory.cs", "src/MyCalculator.cs"];

        Assert.Empty(TestSubjectResolver.MatchSourceFiles("Calculator", repo));
    }

    // --- Dosya adı geleneği (.NET dışı diller) -----------------------------
    // Canlıda ölçüldü (pilot run 34196510936): Python'da agent hatayı doğru
    // teşhis etti ama calculator.py yerine test dosyasını gösterdi ve
    // "add fonksiyonunun kodu verilmediği için düzeltme önerilemiyor" dedi.
    // .NET'te bu köprü test ADINDAN kuruluyor; diğer dillerde ad yok, DOSYA var.

    [Theory]
    [InlineData("Python/pytest", "py-app/test_calculator.py", "calculator.py")]
    [InlineData("Python/unittest", "tests/calculator_test.py", "calculator.py")]
    [InlineData("Go", "internal/calculator_test.go", "calculator.go")]
    [InlineData("Jest", "node-app/calculator.test.js", "calculator.js")]
    [InlineData("Jest/spec", "src/calculator.spec.js", "calculator.js")]
    [InlineData("React/TSX", "src/Calculator.test.tsx", "Calculator.tsx")]
    [InlineData("JUnit", "src/test/java/CalculatorTest.java", "Calculator.java")]
    [InlineData("RSpec", "spec/calculator_spec.rb", "calculator.rb")]
    [InlineData("PHPUnit", "tests/CalculatorTest.php", "Calculator.php")]
    [InlineData("xUnit", "tests/CalculatorTests.cs", "Calculator.cs")]
    public void SubjectFileName_FollowsTheNamingConventionOfEachEcosystem(
        string ecosystem, string testFile, string expected)
    {
        Assert.True(
            TestSubjectResolver.SubjectFileName(testFile) == expected,
            $"{ecosystem}: '{testFile}' -> '{expected}' bekleniyordu.");
    }

    [Theory]
    [InlineData("py-app/calculator.py")]      // sıradan kaynak dosyası
    [InlineData("src/latest.py")]             // "test" ile bitiyor ama test değil
    [InlineData("src/contest.go")]            // aynı tuzak
    [InlineData("Makefile")]                  // uzantı yok
    [InlineData("src/protest.rb")]
    [InlineData("")]
    [InlineData(null)]
    public void SubjectFileName_ReturnsNull_WhenTheNameIsNotATestFile(string? path)
    {
        // Yanlış pozitif sessizce zarar verir: alakasız bir dosya prompt'a girer
        // ve model onu "hatanın olduğu yer" sanabilir.
        Assert.Null(TestSubjectResolver.SubjectFileName(path));
    }

    [Fact]
    public void MatchSourceFilesByName_FindsTheFileRegardlessOfLanguage()
    {
        string[] repo = ["py-app/calculator.py", "py-app/test_calculator.py"];

        Assert.Equal(["py-app/calculator.py"],
            TestSubjectResolver.MatchSourceFilesByName("calculator.py", repo));
    }

    [Fact]
    public void MatchSourceFilesByName_SkipsTestFiles()
    {
        // Aranan şey testin test ETTİĞİ dosya. Adı eşleşse bile başka bir test
        // dosyası gösterilmemeli.
        string[] repo = ["src/calculator.test.js", "test/calculator.js"];

        Assert.Equal(["test/calculator.js"],
            TestSubjectResolver.MatchSourceFilesByName("calculator.js", repo));
    }

    [Fact]
    public void MatchSourceFilesByName_SkipsVendorDirectories()
    {
        // Repoya commit'lenmiş bir bağımlılık kopyası doğru dosyayı gölgeleyebilir.
        string[] repo =
        [
            "node_modules/some-pkg/calculator.js",
            "vendor/other/calculator.js",
            "app/calculator.js"
        ];

        Assert.Equal(["app/calculator.js"],
            TestSubjectResolver.MatchSourceFilesByName("calculator.js", repo));
    }

    [Fact]
    public void MatchSourceFilesByName_KeepsNonCsFiles_UnlikeTheDotNetPath()
    {
        // .NET yolu FixPolicy ile eleniyor (yalnızca .cs). Burada amaç farklı:
        // /fix düzenlesin diye değil, model SEBEBİ görsün diye ekliyoruz.
        string[] repo = ["app/calculator.py"];

        Assert.NotEmpty(TestSubjectResolver.MatchSourceFilesByName("calculator.py", repo));
    }
}
