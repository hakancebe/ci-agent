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

        var body = FixReport.BuildBody(outcome, committed: true, commentId: 555);

        Assert.StartsWith(FixReport.BuildMarker(555), body);
    }

    [Fact]
    public void BuildBody_ShowsDiffAndCommitClaim_OnSuccess()
    {
        var outcome = new FixOutcome(FixStatus.Fixed, "Operatör düzeltildi", [Applied()], 1);

        var body = FixReport.BuildBody(outcome, committed: true, commentId: 1);

        Assert.Contains("✅", body);
        Assert.Contains("commit edildi", body);
        Assert.Contains("- return a - b;", body);
        Assert.Contains("+ return a + b;", body);
    }

    [Fact]
    public void BuildBody_SaysNothingWasCommitted_AndOffersCommitFlag_InProposeMode()
    {
        // Varsayılan yol: doğrulandı ama dala yazılmadı. Yorum üç şeyi birden
        // söylemeli — doğrulandığını, commit EDİLMEDİĞİNİ ve nasıl istenebileceğini.
        var outcome = new FixOutcome(FixStatus.Fixed, "Operatör düzeltildi", [Applied()], 1);

        var body = FixReport.BuildBody(outcome, committed: false, commentId: 1);

        Assert.Contains("derleme + testler çalıştırıldı ve geçti", body);
        Assert.Contains("commit edilmedi", body);
        Assert.Contains("/fix --commit", body);
        Assert.DoesNotContain("commit edildi.", body);
    }

    [Fact]
    public void BuildBody_DoesNotClaimTheFixIsProvenCorrect_InProposeMode()
    {
        // Bu oturumun kök dersi: derleme+test geçmesi düzeltmenin DOĞRU olduğunu
        // göstermiyor. Yorum bu sınırı açıkça söylemeli, yoksa "yeşil = doğru"
        // yanılgısını pekiştirir.
        var outcome = new FixOutcome(FixStatus.Fixed, "Operatör düzeltildi", [Applied()], 1);

        var body = FixReport.BuildBody(outcome, committed: false, commentId: 1);

        Assert.Contains("kanıtlamaz", body);
        Assert.Contains("test kapsamı", body);
    }

    [Fact]
    public void BuildBody_SaysChangesWereReverted_WhenVerificationFailed()
    {
        // En önemli dürüstlük kuralı: bir şey düzeltilmiş izlenimi verilmemeli.
        var outcome = new FixOutcome(
            FixStatus.VerificationFailed, "denendi", [Applied()], 2,
            "Failed! - Failed: 1, Assert.Equal() Values differ");

        var body = FixReport.BuildBody(outcome, committed: false, commentId: 1);

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

        var body = FixReport.BuildBody(outcome, committed: false, commentId: 1);

        Assert.Contains("CiAgent.Tests/CalcTests.cs", body);
        Assert.Contains("test dosyaları düzenlenemez", body);
    }

    [Fact]
    public void BuildBody_ExplainsWhyNothingWasAttempted_ForRestoreStyleFailures()
    {
        var outcome = new FixOutcome(FixStatus.NoSourceFiles, "dosya yok", [], 0);

        var body = FixReport.BuildBody(outcome, committed: false, commentId: 1);

        Assert.Contains("kaynak dosyaya bağlanamadı", body);
        Assert.Contains("restore", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildBody_NamesBlockedFilesAndSaysNothingChanged_OnFilesRejected()
    {
        // En önemli fark: bu "altyapı sorunu" değil "bu dosyaya dokunamam".
        // Kullanıcı hangi dosyanın neden atlandığını görmeli, "restore" tavsiyesini
        // DEĞİL.
        var outcome = new FixOutcome(
            FixStatus.FilesRejected,
            "Hatanın işaret ettiği dosyaların tümü düzenleme politikası dışında.",
            [], 0,
            RejectedPaths: [new RejectedPath(
                "src/CiPilot.Core/Tests.cs",
                "test dosyaları düzenlenemez: 'src/CiPilot.Core/Tests.cs'")]);

        var body = FixReport.BuildBody(outcome, committed: false, commentId: 1);

        Assert.Contains("otomatik düzeltemedi", body);
        Assert.Contains("Politika dışı bırakılan dosyalar", body);
        Assert.Contains("src/CiPilot.Core/Tests.cs", body);
        Assert.Contains("hiçbir değişiklik yapılmadı", body);
        Assert.DoesNotContain("restore", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("✅", body);
    }

    [Fact]
    public void BuildBody_DoesNotGiveTestFileAdvice_WhenRejectionWasNotAboutTests()
    {
        // Canlıda ölçüldü: bir CD hatasında reddedilen dosya
        // .github/workflows/cd.yml'di, sebep ".cs değil"di — ama rapor
        // "dosya gerçekten bir test değilse adını gözden geçirin" diyordu.
        // Alakasız tavsiye, insanı yanlış yere bakmaya yönlendiriyor.
        var outcome = new FixOutcome(
            FixStatus.FilesRejected,
            "Hatanın işaret ettiği dosyaların tümü düzenleme politikası dışında.",
            [], 0,
            RejectedPaths: [new RejectedPath(
                ".github/workflows/cd.yml",
                "yalnızca .cs dosyaları düzenlenebilir: '.github/workflows/cd.yml'")]);

        var body = FixReport.BuildBody(outcome, committed: false, commentId: 1);

        Assert.Contains(".github/workflows/cd.yml", body);
        Assert.DoesNotContain("dosya gerçekten bir test değilse", body);
    }

    [Fact]
    public void BuildBody_SaysNothingWasAttempted_WhenAnalysisSaidNotFixable()
    {
        var outcome = new FixOutcome(
            FixStatus.NotAutomaticallyFixable,
            "Bu değişkenin ne olması gerektiği koddan çıkarılamıyor.", [], 0);

        var body = FixReport.BuildBody(outcome, committed: false, commentId: 1);

        Assert.Contains("otomatik düzeltemedi", body);
        Assert.Contains("koddan belirlenemediğini", body);
        Assert.Contains("hiç denenmedi", body);
        Assert.DoesNotContain("✅", body);
    }

    [Fact]
    public void BuildBody_MentionsRetry_WithoutClaimingWhichStageFailed()
    {
        var outcome = new FixOutcome(FixStatus.Fixed, "düzeltildi", [Applied()], 2);

        var body = FixReport.BuildBody(outcome, committed: false, commentId: 1);

        Assert.Contains("2. denemede", body);
        // İlk denemenin nerede patladığını (derleme/test/politika) bilmiyoruz;
        // rapor da iddia etmemeli.
        Assert.DoesNotContain("testleri geçemedi", body);
    }

    private static EditOutcome UncommentsABlock() =>
        EditOutcome.Ok(new CodeEdit
        {
            File = "src/CiPilot.Core/Tests.cs",
            OldText =
                "// public class GizliMetodSahibi\n" +
                "// {\n" +
                "//     public void GizliMetod() { }\n" +
                "// }",
            NewText =
                "public class GizliMetodSahibi\n" +
                "{\n" +
                "    public void GizliMetod() { }\n" +
                "}",
            Reason = "yorumdaki tanım aktifleştiriliyor"
        });

    [Fact]
    public void BuildBody_WarnsWhenFixUncommentsCode()
    {
        var outcome = new FixOutcome(FixStatus.Fixed, "tanım aktifleştirildi", [UncommentsABlock()], 1);

        var body = FixReport.BuildBody(outcome, committed: false, commentId: 1);

        Assert.Contains("yorumdan çıkar", body);
        Assert.Contains("bilerek kapatılmış", body);
    }

    [Fact]
    public void BuildBody_NoUncommentWarning_ForOrdinaryFix()
    {
        var outcome = new FixOutcome(FixStatus.Fixed, "operatör düzeltildi", [Applied()], 1);

        var body = FixReport.BuildBody(outcome, committed: false, commentId: 1);

        Assert.DoesNotContain("yorumdan çıkar", body);
    }
}
