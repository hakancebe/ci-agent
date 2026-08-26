using System.Text;

namespace CiAgent.Core;

public static class CodeSnippetExtractor
{
    /// <summary>
    /// fileContent'ten lineNumber etrafında ±contextLines'lık, satır numaralı bir kesit
    /// üretir. Hedef satır ">> " ile işaretlenir ki LLM hangi satırın patladığını
    /// doğrudan görsün. lineNumber dosya sınırları dışındaysa (tutarsız/bozuk veri,
    /// örn. parser'ın yanlış bir satır no yakalaması) sessizce null döner.
    /// </summary>
    public static string? ExtractSnippet(string fileContent, int lineNumber, int contextLines = 30)
    {
        if (string.IsNullOrEmpty(fileContent))
            return null;

        var lines = fileContent.Split('\n');

        if (lineNumber < 1 || lineNumber > lines.Length)
            return null;

        var start = Math.Max(1, lineNumber - contextLines);
        var end = Math.Min(lines.Length, lineNumber + contextLines);

        var sb = new StringBuilder();
        for (var i = start; i <= end; i++)
        {
            var marker = i == lineNumber ? ">> " : "";
            // lines 0-indexed, i 1-indexed satır no; CRLF dosyalarında satır sonundaki
            // \r'ı temizliyoruz ki kesit satırları bozuk görünmesin.
            sb.Append(marker).Append(i).Append(": ").Append(lines[i - 1].TrimEnd('\r')).Append('\n');
        }

        return sb.ToString().TrimEnd('\n');
    }
}
