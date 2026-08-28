using CiAgent.Core;

namespace CiAgent.Tests;

public class FixReportTests
{
    private static EditOutcome Applied(string file = "src/Calc.cs") =>
        EditOutcome.Ok(new CodeEdit
        {
            File = file, OldText = "return a - b;", NewText = "return a + b;",
            Reason = "toplama yerine çıkarma yapılıyordu"
        });

    private static EditOutcome Rejected(string file, string reason) =>
        EditOutcome.Rejected(
            new CodeEdit { File = file, OldText = "x", NewText = "y", Reason = "test" }, reason);

    [Fact]
    public void BuildBody_StartsWithMarker_SoRepeatedRunsUpdateOneComment()
    {
        var outcome = new FixOutcome(FixStatus.Fixed, "düzeltildi", [Applied()], 1);

        var body = FixReport.BuildBody(outcome, dryRun: false, commentId: 555);

        Assert.StartsWith(FixReport.BuildMarker(555), body);
    }

    [Fact]
    public void BuildBody_ShowsDiffAndCommitClaim_OnSuccess()
    {
        var outcome = new FixOutcome(FixStatus.Fixed, "Operatör düzeltildi", [Applied()], 1);

        var body = FixReport.BuildBody(outcome, dryRun: false, commentId: 1);

        Assert.Contains("✅", body);
        Assert.Contains("commit edildi", body);
        Assert.Contains("- return a - b;", body);
        Assert.Contains("+ return a + b;", body);
    }

    [Fact]
    public void BuildBody_SaysNothingWasCommitted_OnDryRun()
    {
        var outcome = new FixOutcome(FixStatus.Fixed, "Operatör düzeltildi", [Applied()], 1);

        var body = FixReport.BuildBody(outcome, dryRun: true, commentId: 1);

        Assert.Contains("dry-run", body);
        Assert.Contains("commit edilmedi", body);
        Assert.DoesNotContain("commit edildi.", body);
    }

    [Fact]
    public void BuildBody_SaysChangesWereReverted_WhenVerificationFailed()
    {
        // En önemli dürüstlük kuralı: bir şey düzeltilmiş izlenimi verilmemeli.
        var outcome = new FixOutcome(
            FixStatus.VerificationFailed, "denendi", [Applied()], 2,
            "Failed! - Failed: 1, Assert.Equal() Values differ");

        var body = FixReport.BuildBody(outcome, dryRun: false, commentId: 1);

        Assert.Contains("otomatik düzeltemedi", body);
        Assert.Contains("geri alındı", body);
        Assert.Contains("Assert.Equal() Values differ", body);
        Assert.DoesNotContain("✅", body);
    }

    [Fact]
    public void BuildBody_ListsRejectionReasons_WhenEditsBlocked()
    {
        var outcome = new FixOutcome(FixStatus.EditsRejected, "denendi",
            [Rejected("CiAgent.Tests/CalcTests.cs", "test dosyaları düzenlenemez")], 1);

        var body = FixReport.BuildBody(outcome, dryRun: false, commentId: 1);

        Assert.Contains("CiAgent.Tests/CalcTests.cs", body);
        Assert.Contains("test dosyaları düzenlenemez", body);
    }

    [Fact]
    public void BuildBody_ExplainsWhyNothingWasAttempted_ForRestoreStyleFailures()
    {
        var outcome = new FixOutcome(FixStatus.NoSourceFiles, "dosya yok", [], 0);

        var body = FixReport.BuildBody(outcome, dryRun: false, commentId: 1);

        Assert.Contains("kaynak dosyaya bağlanamadı", body);
        Assert.Contains("restore", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildBody_MentionsRetry_WhenSecondAttemptSucceeded()
    {
        var outcome = new FixOutcome(FixStatus.Fixed, "düzeltildi", [Applied()], 2);

        var body = FixReport.BuildBody(outcome, dryRun: false, commentId: 1);

        Assert.Contains("2. denemede", body);
    }
}
