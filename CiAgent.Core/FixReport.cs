using System.Text;

namespace CiAgent.Core;

/// <summary>
/// /fix sonucunu PR yorumuna çeviren saf biçimlendirme. Ana kural: agent ne
/// yaptığını ve NE YAPAMADIĞINI açıkça söylesin — "düzelttim" izlenimi verip
/// aslında bir şey yapmamış olmasın.
/// </summary>
public static class FixReport
{
    public static string BuildMarker(long commentId) => $"<!-- ci-agent-fix:{commentId} -->";

    /// <summary>
    /// Kaç satır yorumdan çıkarılınca rapora uyarı düşülür. Tek satır (ör. bir
    /// <c>using</c>) genelde zararsız; asıl risk bir bloğun (sınıf/metot)
    /// diriltilmesi. Eşik "birden fazla satır"da.
    /// </summary>
    private const int UncommentWarnThreshold = 2;

    public static string BuildBody(FixOutcome outcome, bool committed, long commentId)
    {
        var sb = new StringBuilder();
        sb.AppendLine(BuildMarker(commentId));

        if (outcome.Succeeded)
            AppendSuccess(sb, outcome, committed);
        else
            AppendFailure(sb, outcome);

        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine("<sub>CiAgent tarafından `/fix` komutuyla oluşturuldu. "
                    + "Değişiklikleri incelemeden merge etmeyin.</sub>");

        return sb.ToString();
    }

    private static void AppendSuccess(StringBuilder sb, FixOutcome outcome, bool committed)
    {
        if (committed)
        {
            sb.AppendLine("## ✅ CiAgent — /fix");
            sb.AppendLine();
            sb.AppendLine("Düzeltme uygulandı ve **derleme + testler geçti**. Değişiklik bu PR'ın dalına commit edildi.");
        }
        else
        {
            // Varsayılan yol. Başlıkta "doğrulanmış öneri" demek bilinçli: bu bir
            // tahmin değil, uygulanıp derlenmiş ve testleri geçmiş bir yama —
            // ama doğrulamanın neyi KANITLAMADIĞI da söyleniyor.
            sb.AppendLine("## 🔍 CiAgent — /fix (doğrulanmış öneri)");
            sb.AppendLine();
            sb.AppendLine("Aşağıdaki değişiklik uygulanıp **derleme + testler çalıştırıldı ve geçti**, "
                        + "ancak dala **commit edilmedi**.");
            sb.AppendLine();
            sb.AppendLine("> Derlemenin ve testlerin geçmesi, düzeltmenin *doğru* olduğunu kanıtlamaz — "
                        + "değişen satırın test kapsamı yoksa geçen her değişiklik aynı görünür. "
                        + "Bu yüzden son karar sizde.");
            sb.AppendLine();
            sb.AppendLine("Değişikliği elle uygulayabilir, ya da bu PR'a **`/fix --commit`** yazarak "
                        + "agent'ın dala commit'lemesini isteyebilirsiniz.");
        }

        sb.AppendLine();
        sb.AppendLine($"**Ne yapıldı:** {outcome.Summary}");
        sb.AppendLine();

        // Yorumdan çıkarma işareti. Bloklamıyoruz (bkz. FixPolicy) ama insan
        // diff'e bakarken bunu bilsin: yorumun neden konduğu koddan görülmüyor.
        // /fix --commit ile inceleme atlanmışsa uyarı daha da değerli.
        var uncommented = outcome.AppliedEdits.Sum(FixPolicy.UncommentedCodeLineCount);
        if (uncommented >= UncommentWarnThreshold)
        {
            sb.AppendLine(
                $"> ⚠️ Bu düzeltme **{uncommented} satırı yorumdan çıkarıp canlı koda çeviriyor**. "
                + "O kodun neden yoruma alındığı buradan bilinemez; "
                + (committed ? "commit'lenen" : "aşağıdaki")
                + " değişikliğin bilerek kapatılmış bir kodu diriltmediğinden emin olun.");
            sb.AppendLine();
        }

        if (outcome.Attempts > 1)
        {
            // "testleri geçemedi" demek yanıltıcıydı: ilk deneme derlemede de
            // patlamış olabilir (test hiç koşmadan) ya da öneri politikaya
            // takılmış olabilir. FixOutcome hangi aşamada olduğunu tutmuyor —
            // burada yalnızca "ilk deneme tutmadı, geri bildirim sonrası
            // ikincisi geçti" denebilir.
            sb.AppendLine(
                $"> İlk deneme tutmadı; hata modele geri bildirildikten sonra "
                + $"{outcome.Attempts}. denemede geçti.");
            sb.AppendLine();
        }

        // Öneri modunda başlık da öyle okunmalı: değişiklik dalda DEĞİL.
        AppendEditList(sb, outcome,
            title: committed ? "Değişiklikler" : "Önerilen değişiklik (uygulanmadı)");
    }

    private static void AppendFailure(StringBuilder sb, FixOutcome outcome)
    {
        sb.AppendLine("## ⚠️ CiAgent — /fix otomatik düzeltemedi");
        sb.AppendLine();

        // Her durum için NEDEN olmadığı ve insanın ne yapması gerektiği yazılıyor.
        var (explanation, advice) = outcome.Status switch
        {
            FixStatus.NoSourceFiles => (
                "Hata belirli bir kaynak dosyaya bağlanamadı ya da işaret edilen dosya "
                + "çalışma dizininde bulunamadı, dolayısıyla düzenlenecek bir yer yok.",
                "Konumu olmayan hatalarda (paket restore, deploy, ortam sorunu) beklenen sonuç. "
                + "Analiz yorumunda bir dosya:satır varsa yolun repo kökünden itibaren doğru "
                + "verildiğini kontrol edin; kalan durumlarda hatayı elle inceleyin."),

            // Tavsiye metni eskiden hep "bu bir test dosyası" varsayıyordu ve
            // canlıda yanlış çıktı: bir CD hatasında reddedilen dosya
            // `.github/workflows/cd.yml`'di, red sebebi ".cs değil"di, ama
            // kullanıcıya "dosya gerçekten bir test değilse adını gözden
            // geçirin" deniyordu. Artık tek bir sebep varsayılmıyor —
            // hangi dosyanın neden atlandığı zaten aşağıda listeleniyor.
            FixStatus.FilesRejected => (
                "Hatanın işaret ettiği dosyaların tümü `/fix`'in düzenleme politikası dışında; "
                + "**hiçbir değişiklik yapılmadı**.",
                "Hangi dosyanın neden atlandığı aşağıda yazılı. `/fix` yalnızca `.cs` kaynak "
                + "dosyalarını düzenliyor; test dosyaları (`*Tests.cs`, `tests/` altı) ve "
                + "`.github/` altı bilerek korunuyor — agent'ın kendi tetikleyicilerini ya da "
                + "kendi sınavını değiştirebilmesi kabul edilemez. Düzeltmeyi elle yapın."),

            FixStatus.NotAutomaticallyFixable => (
                "Analiz, doğru düzeltmenin **koddan belirlenemediğini** bildirdi; "
                + "otomatik düzeltme hiç denenmedi.",
                "Bu hata sınıfı (ör. ne olması gerektiği bilinmeyen bir değişken, iş "
                + "mantığı gerektiren bir karar) insan bilgisi istiyor. Yanlış bir "
                + "düzeltmenin commit'lenmesindense durmak tercih edildi."),

            FixStatus.NoProposal => (
                "Model, verilen bilgiyle güvenli bir düzeltme öneremedi.",
                "Uydurma bir değişiklik yapmaktansa bilerek durdu. Hatayı elle inceleyin."),

            FixStatus.EditsRejected => (
                "Modelin önerdiği değişiklikler güvenlik/tutarlılık kurallarına takıldı ve **uygulanmadı**.",
                "Aşağıdaki gerekçelere bakın. Test dosyalarına ve `.github/` altına yapılan "
                + "değişiklikler bilerek engelleniyor."),

            FixStatus.VerificationFailed => (
                $"Değişiklik uygulandı ama derleme/testler hâlâ başarısızdı ({outcome.Attempts} deneme). "
                + "**Tüm değişiklikler geri alındı.**",
                "Aşağıdaki doğrulama çıktısına bakın."),

            _ => ("Bilinmeyen bir durum oluştu.", "Job loglarını inceleyin.")
        };

        sb.AppendLine(explanation);
        sb.AppendLine();
        sb.AppendLine(advice);
        sb.AppendLine();

        if (!string.IsNullOrWhiteSpace(outcome.Summary))
        {
            sb.AppendLine($"**Modelin açıklaması:** {outcome.Summary}");
            sb.AppendLine();
        }

        if (outcome.RejectedPaths is { Count: > 0 } rejectedPaths)
        {
            sb.AppendLine("**Politika dışı bırakılan dosyalar:**");
            foreach (var r in rejectedPaths)
                sb.AppendLine($"- `{r.Path}` — {r.Reason}");
            sb.AppendLine();
        }

        var rejected = outcome.Edits.Where(e => !e.Applied).ToList();
        if (rejected.Count > 0)
        {
            sb.AppendLine("**Reddedilen değişiklikler:**");
            foreach (var r in rejected)
                sb.AppendLine($"- `{r.Edit.File}` — {r.RejectionReason}");
            sb.AppendLine();
        }

        if (outcome.Status == FixStatus.VerificationFailed && outcome.VerificationOutput is not null)
        {
            sb.AppendLine("<details>");
            sb.AppendLine("<summary>Doğrulama çıktısı</summary>");
            sb.AppendLine();
            sb.AppendLine("```");
            sb.AppendLine(new VerificationResult(false, outcome.VerificationOutput).Tail(3_000));
            sb.AppendLine("```");
            sb.AppendLine();
            sb.AppendLine("</details>");
            sb.AppendLine();

            // Denenip geri alınan değişiklik, insanın işine yarayabilecek bir ipucu.
            AppendEditList(sb, outcome, title: "Denenen (ve geri alınan) değişiklikler");
        }
    }

    private static void AppendEditList(
        StringBuilder sb, FixOutcome outcome, string title = "Değişiklikler")
    {
        var applied = outcome.Edits.Where(e => e.Applied).ToList();
        if (applied.Count == 0)
            return;

        sb.AppendLine($"**{title}:**");
        sb.AppendLine();

        foreach (var e in applied)
        {
            sb.AppendLine($"<details>");
            sb.AppendLine($"<summary><code>{e.Edit.File}</code> — {e.Edit.Reason}</summary>");
            sb.AppendLine();
            sb.AppendLine("```diff");
            foreach (var line in SplitLines(e.Edit.OldText)) sb.AppendLine($"- {line}");
            foreach (var line in SplitLines(e.Edit.NewText)) sb.AppendLine($"+ {line}");
            sb.AppendLine("```");
            sb.AppendLine();
            sb.AppendLine("</details>");
        }

        sb.AppendLine();
    }

    private static string[] SplitLines(string text) =>
        text.Replace("\r\n", "\n").Split('\n');
}
