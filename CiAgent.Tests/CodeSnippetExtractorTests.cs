using System.Text;
using CiAgent.Core;

namespace CiAgent.Tests;

public class CodeSnippetExtractorTests
{
    // "line1", "line2", ..., "line100" - 100 satırlık sahte dosya.
    private static string FakeFile(int lineCount = 100)
    {
        var sb = new StringBuilder();
        for (var i = 1; i <= lineCount; i++)
        {
            sb.Append("line").Append(i);
            if (i < lineCount) sb.Append('\n');
        }
        return sb.ToString();
    }

    [Fact]
    public void ExtractSnippet_MiddleLine_ComputesCorrectStartAndEnd()
    {
        var result = CodeSnippetExtractor.ExtractSnippet(FakeFile(), lineNumber: 50, contextLines: 10);

        Assert.NotNull(result);
        var lines = result!.Split('\n');

        // start = 50-10 = 40, end = 50+10 = 60 -> 21 satır.
        Assert.Equal(21, lines.Length);
        Assert.Equal("40: line40", lines[0]);
        Assert.Equal("60: line60", lines[^1]);
    }

    [Fact]
    public void ExtractSnippet_NearFileStart_ClampsStartToOne_NeverNegative()
    {
        var result = CodeSnippetExtractor.ExtractSnippet(FakeFile(), lineNumber: 5, contextLines: 10);

        Assert.NotNull(result);
        var lines = result!.Split('\n');

        // start = max(1, 5-10) = 1, end = 5+10 = 15 -> 15 satır.
        Assert.Equal("1: line1", lines[0]);
        Assert.Equal(15, lines.Length);
        Assert.DoesNotContain(lines, l => l.StartsWith("-") || l.StartsWith("0:"));
    }

    [Fact]
    public void ExtractSnippet_NearFileEnd_ClampsEndToFileLength()
    {
        var result = CodeSnippetExtractor.ExtractSnippet(FakeFile(100), lineNumber: 97, contextLines: 10);

        Assert.NotNull(result);
        var lines = result!.Split('\n');

        // start = 97-10 = 87, end = min(100, 107) = 100 -> 14 satır.
        Assert.Equal("87: line87", lines[0]);
        Assert.Equal("100: line100", lines[^1]);
        Assert.Equal(14, lines.Length);
    }

    [Fact]
    public void ExtractSnippet_MarksTargetLine_WithArrowPrefix()
    {
        var result = CodeSnippetExtractor.ExtractSnippet(FakeFile(), lineNumber: 50, contextLines: 10);

        Assert.NotNull(result);
        Assert.Contains(">> 50: line50", result);

        // Komşu satırlar işaretlenmemeli, sadece hedef satır ">> " alır.
        Assert.Contains("49: line49", result);
        Assert.DoesNotContain(">> 49:", result);
        Assert.DoesNotContain(">> 51:", result);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(101)]
    public void ExtractSnippet_ReturnsNull_WhenLineNumberOutsideFileBounds(int lineNumber)
    {
        var result = CodeSnippetExtractor.ExtractSnippet(FakeFile(100), lineNumber, contextLines: 10);

        Assert.Null(result);
    }

    [Fact]
    public void ExtractSnippet_ReturnsNull_WhenFileContentIsEmpty()
    {
        Assert.Null(CodeSnippetExtractor.ExtractSnippet("", lineNumber: 1));
        Assert.Null(CodeSnippetExtractor.ExtractSnippet(null!, lineNumber: 1));
    }

    [Fact]
    public void ExtractSnippet_UsesDefaultContextOf30Lines_WhenNotSpecified()
    {
        var result = CodeSnippetExtractor.ExtractSnippet(FakeFile(200), lineNumber: 100);

        Assert.NotNull(result);
        var lines = result!.Split('\n');

        Assert.Equal("70: line70", lines[0]);
        Assert.Equal("130: line130", lines[^1]);
    }
}
