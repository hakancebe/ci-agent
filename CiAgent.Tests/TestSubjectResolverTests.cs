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
}
