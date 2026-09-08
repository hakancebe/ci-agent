using CiAgent.Core;

namespace CiAgent.Tests;

/// <summary>
/// Gömülü analiz sonucu, rozet ile /fix'in kararının ayrışmasını önleyen tek
/// doğruluk kaynağı. Bu yüzden iki şey önemli: gidiş-dönüş kayıpsız olmalı, ve
/// veri okunamadığında sessizce null dönmeli (analiz baştan yapılabilsin).
/// </summary>
public class AnalysisPayloadTests
{
    private static AnalysisResult Sample(bool fixable = false) => new()
    {
        Summary = "Tanımsız değişken.",
        Analyses =
        {
            new Analysis
            {
                Title = "Tanımsız ad",
                RootCause = "Kapsamda yok.",
                SuggestedFix = "Koddan çıkarılamıyor.",
                Confidence = "high",
                AffectedFile = "src/Calc.cs",
                AffectedLine = 31,
                Fixable = fixable
            }
        }
    };

    [Fact]
    public void Encode_ThenDecode_PreservesTheDecisionThatDrivesTheBadge()
    {
        var decoded = AnalysisPayload.TryDecode("gövde\n" + AnalysisPayload.Encode(Sample()));

        Assert.NotNull(decoded);
        var a = Assert.Single(decoded!.Analyses);
        Assert.False(a.Fixable);           // rozetin dayandığı alan
        Assert.Equal("src/Calc.cs", a.AffectedFile);
        Assert.Equal(31, a.AffectedLine);
        Assert.Equal("high", a.Confidence);
    }

    [Fact]
    public void Encode_ProducesHiddenBlock_SoItDoesNotShowInRenderedMarkdown()
    {
        var encoded = AnalysisPayload.Encode(Sample());

        Assert.StartsWith("<!--", encoded);
        Assert.EndsWith("-->", encoded);
        // İçerik base64: ham JSON olsaydı içindeki "-->" HTML yorumunu erken kapatabilirdi.
        Assert.DoesNotContain("\"summary\"", encoded);
    }

    [Fact]
    public void Encode_SurvivesContentThatWouldBreakAnHtmlComment()
    {
        var nasty = new AnalysisResult
        {
            Summary = "kapatma denemesi --> ve \"tırnak\"\nsatır sonu",
            Analyses = { new Analysis
            {
                Title = "x", RootCause = "y --> z", SuggestedFix = "s",
                Confidence = "low", Fixable = true
            }}
        };

        var decoded = AnalysisPayload.TryDecode(AnalysisPayload.Encode(nasty));

        Assert.Equal(nasty.Summary, decoded!.Summary);
        Assert.Equal("y --> z", Assert.Single(decoded.Analyses).RootCause);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("hiç veri bloğu olmayan bir yorum")]          // eski sürümde yazılmış yorum
    [InlineData("<!-- ci-agent-data:bu-base64-degil -->")]     // elle bozulmuş
    [InlineData("<!-- ci-agent-data:eyJib3p1ayI6")]            // kapanışı eksik
    public void TryDecode_ReturnsNull_InsteadOfThrowing(string? body)
    {
        // Çağıran taraf null'ı "analizi kendin yap" diye okuyor; exception akışı
        // durdururdu ve /fix hiç çalışmazdı.
        Assert.Null(AnalysisPayload.TryDecode(body));
    }

    [Fact]
    public void TryDecode_ReturnsNull_WhenBase64IsValidButNotOurSchema()
    {
        var junk = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("[1,2,3]"));

        Assert.Null(AnalysisPayload.TryDecode($"<!-- ci-agent-data:{junk} -->"));
    }
}
