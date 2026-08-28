namespace CiAgent.Core;

/// <summary>
/// PR yorumundan ayrıştırılan /fix komutu.
/// </summary>
public sealed record FixCommand(bool DryRun)
{
    /// <summary>
    /// Yorum gövdesinden komutu çıkarır; komut değilse null döner.
    ///
    /// Yalnızca yorumun İLK satırına bakılıyor ve satır /fix ile başlamak zorunda.
    /// Sebep: bir kod bloğunun ya da alıntının içinde geçen "/fix" agent'ı
    /// tetiklememeli — "bence burada /fix çalıştırmalıyız" diye yazan bir yorum
    /// da tetiklememeli.
    /// </summary>
    public static FixCommand? TryParse(string? commentBody)
    {
        if (string.IsNullOrWhiteSpace(commentBody))
            return null;

        var firstLine = commentBody
            .Replace("\r\n", "\n")
            .Split('\n')[0]
            .Trim();

        var parts = firstLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length == 0 || !parts[0].Equals("/fix", StringComparison.OrdinalIgnoreCase))
            return null;

        var dryRun = parts.Skip(1).Any(p => p.Equals("--dry-run", StringComparison.OrdinalIgnoreCase));

        return new FixCommand(dryRun);
    }
}

/// <summary>
/// Yorumu yazan kişi /fix çalıştırabilir mi? GitHub'ın author_association
/// alanına bakıyoruz.
///
/// Bu kontrol şart: /fix agent'a repo'da kod değiştirtip commit attırıyor.
/// Herkesin yorum yazabildiği açık bir repoda, yetkisiz birinin bunu
/// tetikleyebilmesi doğrudan bir saldırı yüzeyi olurdu.
/// </summary>
public static class FixAuthorization
{
    // OWNER: repo sahibi. MEMBER: organizasyon üyesi. COLLABORATOR: davetli katkıcı.
    // Bilerek dışarıda bırakılanlar: CONTRIBUTOR (sadece daha önce PR'ı merge edilmiş),
    // FIRST_TIME_CONTRIBUTOR, NONE — bunlar yazma yetkisi anlamına gelmiyor.
    private static readonly string[] Allowed = ["OWNER", "MEMBER", "COLLABORATOR"];

    public static bool CanRunFix(string? authorAssociation) =>
        authorAssociation is not null &&
        Allowed.Contains(authorAssociation.Trim(), StringComparer.OrdinalIgnoreCase);
}
