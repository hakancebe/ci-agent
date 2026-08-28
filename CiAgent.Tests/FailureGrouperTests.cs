using CiAgent.Core;

namespace CiAgent.Tests;

public class FailureGrouperTests
{
    private static Failure Test(string name, string? file, int? line, string message, string? job = null) =>
        new()
        {
            Kind = FailureKind.Test,
            Name = name,
            JobName = job,
            StepName = "Test",
            FilePath = file,
            LineNumber = line,
            Message = message
        };

    [Fact]
    public void Group_CollapsesIdenticalFailuresAcrossMatrixJobs()
    {
        // Matrix build: aynı test 3 farklı job'da birebir aynı şekilde patlıyor.
        var failures = new[]
        {
            Test("CalcTests.Add", "src/Calc.cs", 12, "Assert.Equal() Failure", job: "build (ubuntu)"),
            Test("CalcTests.Add", "src/Calc.cs", 12, "Assert.Equal() Failure", job: "build (windows)"),
            Test("CalcTests.Add", "src/Calc.cs", 12, "Assert.Equal() Failure", job: "build (macos)"),
        };

        var groups = FailureGrouper.Group(failures);

        var group = Assert.Single(groups);
        Assert.Equal(3, group.Occurrences);
        Assert.Equal(3, group.JobNames.Count);
        Assert.Contains("build (ubuntu)", group.JobNames);
        Assert.Contains("build (macos)", group.JobNames);
        // Hiçbir failure atılmadı - hepsi Members'ta duruyor.
        Assert.Equal(3, group.Members.Count);
    }

    [Fact]
    public void Group_KeepsDifferentAssertValuesApart()
    {
        // Sayıları normalize etmek cazip olurdu ama YANLIŞ olurdu: bunlar
        // gerçekten farklı iki hata.
        var failures = new[]
        {
            Test("CalcTests.Add", "src/Calc.cs", 12, "Expected: 5, Actual: 4"),
            Test("CalcTests.Add", "src/Calc.cs", 12, "Expected: 350, Actual: 4"),
        };

        var groups = FailureGrouper.Group(failures);

        Assert.Equal(2, groups.Count);
    }

    [Fact]
    public void Group_NormalizesWhitespaceOnly()
    {
        // Aynı mesajın farklı girinti/satır sonuyla gelmesi ayrı grup üretmemeli.
        var failures = new[]
        {
            Test("CalcTests.Add", "src/Calc.cs", 12, "Assert.Equal()   Failure:\n  Values differ"),
            Test("CalcTests.Add", "src/Calc.cs", 12, "Assert.Equal() Failure: Values differ"),
        };

        var groups = FailureGrouper.Group(failures);

        Assert.Single(groups);
    }

    [Fact]
    public void Group_KeepsDifferentLinesApart()
    {
        var failures = new[]
        {
            Test("CalcTests.Add", "src/Calc.cs", 12, "Values differ"),
            Test("CalcTests.Sub", "src/Calc.cs", 20, "Values differ"),
        };

        Assert.Equal(2, FailureGrouper.Group(failures).Count);
    }

    [Fact]
    public void Group_KeepsDifferentKindsApart_EvenWithSameMessage()
    {
        var restore = new Failure { Kind = FailureKind.Restore, Message = "aynı mesaj" };
        var compiler = new Failure { Kind = FailureKind.Compiler, Message = "aynı mesaj" };

        Assert.Equal(2, FailureGrouper.Group(new[] { restore, compiler }).Count);
    }

    [Fact]
    public void Group_ReturnsEmpty_ForEmptyInput()
    {
        Assert.Empty(FailureGrouper.Group(Array.Empty<Failure>()));
    }
}
