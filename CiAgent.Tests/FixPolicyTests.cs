using CiAgent.Core;

namespace CiAgent.Tests;

public class FixPolicyTests
{
    private static CodeEdit Edit(string file, string oldText = "eski", string newText = "yeni") =>
        new() { File = file, OldText = oldText, NewText = newText, Reason = "test" };

    // --- Repo dışına çıkma: en kritik kural ------------------------------

    [Theory]
    [InlineData("../../../etc/passwd.cs")]
    [InlineData("src/../../gizli.cs")]
    [InlineData("/etc/passwd.cs")]
    [InlineData("/Users/biri/.ssh/config.cs")]
    [InlineData("C:/Windows/System32/x.cs")]
    [InlineData(@"..\..\gizli.cs")]
    public void RejectPath_BlocksPathsEscapingTheRepo(string path)
    {
        Assert.NotNull(FixPolicy.RejectPath(path));
    }

    // --- Agent kendi güvenlik kurallarını değiştiremesin ------------------

    [Theory]
    [InlineData(".github/workflows/ci.cs")]
    [InlineData(".GitHub/scripts/deploy.cs")]
    public void RejectPath_BlocksGitHubDirectory(string path)
    {
        Assert.Contains(".github/", FixPolicy.RejectPath(path));
    }

    // --- Testi zayıflatarak "düzeltme" engellensin ------------------------

    [Theory]
    [InlineData("CiAgent.Tests/LogParserTests.cs")]
    [InlineData("tests/Foo/BarTest.cs")]
    [InlineData("src/Test/Helper.cs")]
    [InlineData("MyProject.Tests/Support/Fixture.cs")]
    // Adın önünde gerçek bir prefix varsa isim sinyali tek başına yeter,
    // dizin test dizini olmasa bile.
    [InlineData("src/Core/CalculatorTests.cs")]
    [InlineData("src/Core/WidgetTest.cs")]
    public void RejectPath_BlocksTestFiles(string path)
    {
        Assert.Contains("test", FixPolicy.RejectPath(path), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("src/Calc.cs")]
    [InlineData("CiAgent.Core/LogParser.cs")]
    [InlineData("deep/nested/path/Service.cs")]
    // Dosya adının TAMAMI "Tests.cs"/"Test.cs" ise (önünde ad yok) ve dizin de
    // test dizini değilse bu sıradan bir kaynak dosyasıdır - isimden test sayıp
    // /fix'i durdurmak yanlış pozitifti.
    [InlineData("src/CiPilot.Core/Tests.cs")]
    [InlineData("src/CiPilot.Core/Test.cs")]
    public void RejectPath_AllowsOrdinarySourceFiles(string path)
    {
        Assert.Null(FixPolicy.RejectPath(path));
    }

    [Theory]
    [InlineData("README.md")]
    [InlineData("build.sh")]
    [InlineData("src/app.csproj")]
    [InlineData("secrets.json")]
    public void RejectPath_BlocksNonSourceFiles(string path)
    {
        Assert.Contains(".cs", FixPolicy.RejectPath(path));
    }

    // --- İçerik kuralları -------------------------------------------------

    [Fact]
    public void RejectEdit_BlocksEmptyOldText_WhichWouldRewriteWholeFile()
    {
        var reason = FixPolicy.RejectEdit(Edit("src/A.cs", oldText: ""));
        Assert.Contains("boş", reason);
    }

    [Fact]
    public void RejectEdit_BlocksNoOpEdit()
    {
        var reason = FixPolicy.RejectEdit(Edit("src/A.cs", oldText: "ayni", newText: "ayni"));
        Assert.Contains("aynı", reason);
    }

    [Fact]
    public void RejectEdit_BlocksOversizedEdit()
    {
        var huge = new string('x', FixPolicy.MaxEditChars);
        var reason = FixPolicy.RejectEdit(Edit("src/A.cs", oldText: huge, newText: huge + "y"));
        Assert.Contains("çok büyük", reason);
    }

    [Fact]
    public void RejectEdit_AllowsReasonableEdit()
    {
        Assert.Null(FixPolicy.RejectEdit(Edit("src/Calc.cs", "return a - b;", "return a + b;")));
    }

    // --- Yer tutucu koruması (CS0103) -------------------------------------

    private static readonly string[] Tanimsiz = ["tanimsizDegisken"];

    [Fact]
    public void UndefinedNamesFrom_ExtractsNameFromCompilerMessage()
    {
        var names = FixPolicy.UndefinedNamesFrom([
            "CS0103: The name 'tanimsizDegisken' does not exist in the current context",
            "CS0029: Cannot implicitly convert type 'string' to 'int'"
        ]);

        Assert.Equal(["tanimsizDegisken"], names);
    }

    [Theory]
    // Canlıda üç turda çıkan üç varyant: hepsi derlenir, hepsi testleri geçer,
    // hiçbiri hatayı düzeltmez.
    [InlineData("Console.WriteLine(\"örnek metin\");")]
    [InlineData("Console.WriteLine(\"Bir değer\");")]
    [InlineData("Console.WriteLine(\"\");")]
    [InlineData("Console.WriteLine(42);")]
    public void RejectPlaceholderEdit_BlocksLiteralSubstitution(string newText)
    {
        var edit = Edit("src/A.cs", "Console.WriteLine(tanimsizDegisken);", newText);

        var reason = FixPolicy.RejectPlaceholderEdit(edit, Tanimsiz);

        Assert.Contains("tanimsizDegisken", reason);
        Assert.Contains("gizler", reason);
    }

    [Fact]
    public void RejectPlaceholderEdit_BlocksCommentingOutTheLine()
    {
        // Canlıda 4. tur: literal uydurmak yerine satırı yorum yaptı. Ad metinsel
        // olarak duruyor ama canlı koddan silinmiş — ilk sürüm tam bu yüzden
        // atlamıştı.
        var edit = Edit("src/A.cs",
            "Console.WriteLine(tanimsizDegisken);",
            "// Console.WriteLine(tanimsizDegisken);");

        var reason = FixPolicy.RejectPlaceholderEdit(edit, Tanimsiz);

        Assert.Contains("etkisizleştirilmiş", reason);
    }

    [Theory]
    [InlineData("")]                              // satırı komple sil
    [InlineData(";")]                             // gövdeyi boşalt
    [InlineData("Console.WriteLine(null);")]      // literal benzeri anahtar kelime
    public void RejectPlaceholderEdit_BlocksOtherWaysOfNeutralizingTheCode(string newText)
    {
        var edit = Edit("src/A.cs", "Console.WriteLine(tanimsizDegisken);", newText);

        Assert.NotNull(FixPolicy.RejectPlaceholderEdit(edit, Tanimsiz));
    }

    [Fact]
    public void RejectPlaceholderEdit_AllowsTypoFix_BecauseNoLiteralIsIntroduced()
    {
        // Meşru CS0103 düzeltmesi: ad kayboluyor ama yerine literal değil,
        // kapsamdaki başka bir AD geliyor. Engellenmemeli.
        var edit = Edit("src/A.cs", "return a + bbb;", "return a + b;");

        Assert.Null(FixPolicy.RejectPlaceholderEdit(edit, ["bbb"]));
    }

    [Fact]
    public void RejectPlaceholderEdit_AllowsDeclaringTheMissingName()
    {
        // Adı gerçekten TANIMLAYAN düzeltme: ad newText'te duruyor, yani
        // literal içerse bile yer tutucu değil.
        var edit = Edit("src/A.cs",
            "Console.WriteLine(tanimsizDegisken);",
            "string tanimsizDegisken = \"örnek\";\nConsole.WriteLine(tanimsizDegisken);");

        Assert.Null(FixPolicy.RejectPlaceholderEdit(edit, Tanimsiz));
    }

    [Fact]
    public void RejectPlaceholderEdit_IgnoresEditsUnrelatedToTheUndefinedName()
    {
        var edit = Edit("src/A.cs", "return a - b;", "return a + b;");

        Assert.Null(FixPolicy.RejectPlaceholderEdit(edit, Tanimsiz));
    }

    [Fact]
    public void RejectPlaceholderEdit_DoesNotMatchNameInsideLongerIdentifier()
    {
        // 'bbb' adı tanımsız olsa da buradaki 'abbbc' başka bir tanımlayıcı;
        // bu edit o adı ilgilendirmiyor.
        var edit = Edit("src/A.cs", "return abbbc;", "return \"x\";");

        Assert.Null(FixPolicy.RejectPlaceholderEdit(edit, ["bbb"]));
    }
}
