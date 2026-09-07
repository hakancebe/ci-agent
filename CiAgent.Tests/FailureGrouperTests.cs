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
    // --- Kimliksiz mesajlar ------------------------------------------------
    // Canlıda ölçüldü (pilot CD run 34127272978): docker build hatası ile
    // çıplak `exit 1` AYNI metni üretti ("Process completed with exit code 1"),
    // gruplayıcı ikisini tek hata saydı, model de ikisine ORTAK bir kök neden
    // uydurdu. Sessiz job'ın Dockerfile ile ilgisi yoktu.

    private static Failure Generic(string message, string job, string step = "Run") =>
        new()
        {
            Kind = FailureKind.Generic,
            JobName = job,
            StepName = step,
            FilePath = null,
            LineNumber = null,
            Message = message
        };

    [Fact]
    public void Group_DoesNotMergeIdentitylessFailuresFromDifferentJobs()
    {
        var failures = new[]
        {
            Generic("Process completed with exit code 1.", "olcum-docker-build"),
            Generic("Process completed with exit code 1.", "olcum-sessiz-hata"),
        };

        var groups = FailureGrouper.Group(failures);

        // Aynı metin ama aynı hata DEĞİL: bu mesaj neyin patladığını söylemiyor.
        Assert.Equal(2, groups.Count);
    }

    [Fact]
    public void Group_DoesNotMergeCancellationMessagesFromDifferentJobs()
    {
        var failures = new[]
        {
            Generic("The operation was canceled.", "deploy-a"),
            Generic("The operation was canceled.", "deploy-b"),
        };

        Assert.Equal(2, FailureGrouper.Group(failures).Count);
    }

    [Fact]
    public void Group_StillMergesIdentitylessFailures_WithinTheSameJobAndStep()
    {
        // Aynı job'ın aynı adımında iki kez aynı satır: bu gerçekten tekrar,
        // prompt'u şişirmesin.
        var failures = new[]
        {
            Generic("Process completed with exit code 1.", "deploy", "Smoke test"),
            Generic("Process completed with exit code 1.", "deploy", "Smoke test"),
        };

        var group = Assert.Single(FailureGrouper.Group(failures));
        Assert.Equal(2, group.Occurrences);
    }

    [Fact]
    public void Group_StillMergesInformativeMessagesAcrossJobs()
    {
        // Regresyon koruması: mesaj gerçekten bir hatayı TARİF ediyorsa
        // matrix birleştirmesi eskisi gibi çalışmalı.
        var failures = new[]
        {
            Generic("NU1101: Unable to find package Yok.Boyle", "build (ubuntu)"),
            Generic("NU1101: Unable to find package Yok.Boyle", "build (windows)"),
        };

        var group = Assert.Single(FailureGrouper.Group(failures));
        Assert.Equal(2, group.Occurrences);
    }

}
