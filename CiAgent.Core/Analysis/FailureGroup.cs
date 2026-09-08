using System.Text.RegularExpressions;

namespace CiAgent.Core;

/// <summary>
/// Aynı hatanın tekrarlarını tek başlık altında toplar. <see cref="Members"/>
/// hiçbir failure'ı atmaz - sadece prompt'ta ve raporda tek kez gösterilmelerini
/// sağlar.
/// </summary>
public sealed record FailureGroup(Failure Representative, List<Failure> Members)
{
    public int Occurrences => Members.Count;

    /// <summary>Bu grubun görüldüğü job'lar (matrix build'de aynı hata N job'da çıkar).</summary>
    public List<string> JobNames => Members
        .Select(m => m.JobName)
        .Where(n => !string.IsNullOrWhiteSpace(n))
        .Distinct()
        .ToList()!;

    /// <summary>Bu gruba düşen farklı test adları (çoğu zaman tek).</summary>
    public List<string> Names => Members
        .Select(m => m.Name)
        .Where(n => !string.IsNullOrWhiteSpace(n))
        .Distinct()
        .ToList()!;
}

/// <summary>
/// Prompt'a girmeden önce yapılan DETERMİNİSTİK tekrar eleme. Bilinçli olarak
/// "aynı mı" sorusunun tartışmasız cevabıyla sınırlı: aynı tip + aynı dosya:satır
/// + (boşluk normalizasyonu dışında) aynı mesaj. Buradaki amaç kök neden çıkarımı
/// yapmak DEĞİL - o iş LLM'e ait; buradaki amaç matrix build'lerde (aynı test 5
/// farklı OS/TFM job'ında patlar) prompt'un 5 kat şişmesini önlemek.
///
/// Mesaj normalizasyonu yalnızca boşluk sıkıştırması: sayıları normalize etmek
/// cazip ama yanlış olurdu - "Expected: 5, Actual: 4" ile "Expected: 350, Actual: 4"
/// gerçekten farklı iki hata.
/// </summary>
public static class FailureGrouper
{
    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.None, TimeSpan.FromSeconds(2));

    public static List<FailureGroup> Group(IEnumerable<Failure> failures)
    {
        return failures
            .GroupBy(f => (
                f.Kind, f.FilePath, f.LineNumber,
                Message: Normalize(f.Message),
                // Kimliksiz mesajlarda job+adım da anahtara giriyor, yani ayrı
                // job'lardaki failure'lar birleşmiyor.
                Scope: IsIdentityless(f.Message) ? $"{f.JobName}\u0000{f.StepName}" : null))
            .Select(g => new FailureGroup(g.First(), g.ToList()))
            .ToList();
    }

    /// <summary>
    /// Mesaj hangi hatanın olduğunu SÖYLEMİYOR mu? GitHub runner'ının
    /// "Process completed with exit code N" satırı yalnızca bir şeyin
    /// patladığını bildiriyor; hangi şey olduğunu değil.
    ///
    /// Neden önemli: canlıda ölçüldü (pilot CD run 34127272978). İki tamamen
    /// farklı job — biri docker build hatası, diğeri çıplak `exit 1` — bu aynı
    /// metni üretti. Gruplayıcı ikisini TEK hata + 2 tekrar saydı ve model
    /// ikisine ORTAK bir kök neden uydurdu: "Dockerfile... hem şu hem bu
    /// job'ın başarısız olmasının temel nedenidir." Sessiz job'ın Dockerfile
    /// ile hiçbir ilgisi yoktu.
    ///
    /// Ham log İKİSİNİ de içeriyordu; model yapılandırılmış özet ham kanıtla
    /// çeliştiğinde ÖZETE uydu. Yani yanlış gruplama, doğru veriden güçlü
    /// çıkıyor.
    ///
    /// Az gruplamak çok gruplamaktan iyi: SystemPrompt zaten modelden kök
    /// nedene göre birleştirmesini istiyor, yani ayrı görünen iki kayıt yine
    /// tek analize inebilir. Ama yanlış birleştirilmiş iki kayıttan model
    /// olmayan bir ortak sebep üretiyor ve bu rapora yazılıp insana
    /// gösteriliyor.
    /// </summary>
    private static bool IsIdentityless(string message) =>
        IdentitylessMessage.IsMatch(Normalize(message));

    private static readonly Regex IdentitylessMessage = new(
        @"^(Process completed with exit code \d+|The operation was canceled)\.?$",
        RegexOptions.IgnoreCase, TimeSpan.FromSeconds(2));

    private static string Normalize(string message) =>
        Whitespace.Replace(message, " ").Trim();
}
