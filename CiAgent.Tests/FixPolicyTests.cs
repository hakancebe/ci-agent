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
    public void RejectPath_BlocksTestFiles(string path)
    {
        Assert.Contains("test", FixPolicy.RejectPath(path), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("src/Calc.cs")]
    [InlineData("CiAgent.Core/LogParser.cs")]
    [InlineData("deep/nested/path/Service.cs")]
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
}
