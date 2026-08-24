using CiAgent.Core;

namespace CiAgent.Tests;

public class LlmServiceTests
{
    [Fact]
    public void TrimLog_ReturnsLogUnchanged_WhenUnderLimit()
    {
        var log = "kısa bir log, hiç kırpma gerekmiyor";

        var result = LlmService.TrimLog(log);

        Assert.Equal(log, result);
    }

    [Fact]
    public void TrimLog_NeverCutsALineInTheMiddle()
    {
        // Sabit uzunlukta 500 satırlık bir log kuruyoruz - hiçbir satır sınırı
        // MaxLogChars(8000)/HeadChars(1500) ile tesadüfen hizalanmıyor, yani eski
        // (kör char-index) implementasyon neredeyse kesin bir satırı ortadan kesecekti.
        var lines = Enumerable.Range(1, 500)
            .Select(i => $"satir-{i:D4}-icerik-XXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXX")
            .ToArray();
        var log = string.Join("\n", lines);
        Assert.True(log.Length > 8000); // varsayımı doğrula

        var result = LlmService.TrimLog(log);

        foreach (var line in result.Split('\n'))
        {
            if (line.Length == 0) continue;
            if (line.StartsWith("...")) continue; // kırpma marker satırı

            // Çıktıdaki her satır ya bilinen tam satırlardan biri olmalı ya da
            // marker satırı - asla bir satırın yarısı olmamalı (ör. "vokeMethod"
            // gibi kelime ortasından kesilmiş bir parça).
            Assert.Contains(line, lines);
        }
    }

    [Fact]
    public void TrimLog_KeepsHeadAndTailFromOppositeEndsOfLog()
    {
        var lines = Enumerable.Range(1, 500)
            .Select(i => $"satir-{i:D4}-icerik-XXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXX")
            .ToArray();
        var log = string.Join("\n", lines);

        var result = LlmService.TrimLog(log);

        Assert.Contains(lines[0], result);      // ilk satır (head) korunmalı
        Assert.Contains(lines[^1], result);     // son satır (tail) korunmalı
        Assert.Contains("karakter kırpıldı", result);
        Assert.DoesNotContain(lines[250], result); // ortadaki bir satır kırpılmış olmalı
    }

    [Fact]
    public void TrimLog_FallsBackToRawIndex_WhenNoNewlinesPresent()
    {
        // Hiç satır sonu içermeyen, tek parça dev bir "satır" - satır sınırına
        // yuvarlanacak bir nokta yok, eski (ham index) davranışa düşmeli, çökmemeli.
        var log = new string('X', 20000);

        var result = LlmService.TrimLog(log);

        Assert.Contains("karakter kırpıldı", result);
        Assert.True(result.Length < log.Length);
    }
}
