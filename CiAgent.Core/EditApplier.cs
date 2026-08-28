namespace CiAgent.Core;

/// <summary>
/// Bir CodeEdit'i dosya içeriğine uygular. Tüm karar verme saf string üzerinde
/// yapılıyor (dosya sistemine dokunmadan) ki test edilebilsin; disk işlemleri
/// <see cref="WorkspaceEditor"/> tarafında.
/// </summary>
public static class EditApplier
{
    /// <summary>
    /// OldText'i NewText ile değiştirir. İki durumda reddeder:
    ///
    /// - Metin bulunamadıysa: LLM dosyada olmayan bir şeyi hayal etmiş demektir.
    /// - Metin BİRDEN FAZLA yerde geçiyorsa: hangisinin kastedildiği belirsiz.
    ///   Rastgele birini seçmektense reddetmek doğru; model daha fazla bağlam
    ///   içeren daha uzun bir OldText vermeli.
    ///
    /// Satır sonu farkları (CRLF/LF) eşleşmeyi bozmasın diye karşılaştırma
    /// öncesi normalize ediliyor, sonuç dosyanın kendi biçimine geri çevriliyor.
    /// </summary>
    public static (string? Content, string? RejectionReason) Apply(string fileContent, CodeEdit edit)
    {
        var usesCrLf = fileContent.Contains("\r\n");

        var haystack = Normalize(fileContent);
        var needle = Normalize(edit.OldText);

        var first = haystack.IndexOf(needle, StringComparison.Ordinal);
        if (first < 0)
            return (null, $"aranan metin '{edit.File}' içinde bulunamadı");

        var second = haystack.IndexOf(needle, first + needle.Length, StringComparison.Ordinal);
        if (second >= 0)
            return (null,
                $"aranan metin '{edit.File}' içinde birden fazla yerde geçiyor, hangisi olduğu belirsiz");

        var updated = haystack[..first] + Normalize(edit.NewText) + haystack[(first + needle.Length)..];

        return (usesCrLf ? updated.Replace("\n", "\r\n") : updated, null);
    }

    private static string Normalize(string text) => text.Replace("\r\n", "\n");
}
