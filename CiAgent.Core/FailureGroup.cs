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
            .GroupBy(f => (f.Kind, f.FilePath, f.LineNumber, Message: Normalize(f.Message)))
            .Select(g => new FailureGroup(g.First(), g.ToList()))
            .ToList();
    }

    private static string Normalize(string message) =>
        Whitespace.Replace(message, " ").Trim();
}
