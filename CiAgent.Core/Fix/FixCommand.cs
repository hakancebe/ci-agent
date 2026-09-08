namespace CiAgent.Core;

/// <summary>
/// PR yorumundan ayrıştırılan /fix komutu.
///
/// VARSAYILAN ÖNERİ MODU: `/fix` düzeltmeyi üretir, uygular ve derleme+testlerle
/// DOĞRULAR — ama dala commit ETMEZ. Commit istemek için `/fix --commit` gerekir.
///
/// Neden varsayılan bu: doğrulama döngüsü (derleme + test) tek başına düzeltmenin
/// DOĞRU olduğunu göstermiyor. Değişen satırın test kapsamı yoksa model derlenen
/// herhangi bir değişikliği yapabiliyor ve döngü onaylıyor. Canlıda beş turda beş
/// farklı "derlemeyi geçiren ama hatayı gizleyen" varyant görüldü (uydurma literal,
/// farklı literal, boş string, satırı yorum yapmak, adı literal içine saklamak).
/// Deterministik koruma yalnızca CS0103'ü kapsıyor; diğer hata sınıflarında tek
/// savunma modelin kendi kararı ve o kararın kararsız olduğu ölçüldü.
///
/// Bu yüzden commit artık otomatik değil, insanın açık isteği. Ajan yine tüm işi
/// yapıyor (öneri + doğrulama), yalnızca son adım — dala yazmak — insana ait.
/// </summary>
public sealed record FixCommand(bool Commit)
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

        // --dry-run hâlâ KABUL EDİLİYOR ama artık varsayılanı tarif ediyor, yani
        // etkisiz. Bilerek hata vermiyoruz: eski alışkanlıkla yazan biri anlamca
        // doğru olan şeyi istemiş oluyor.
        var commit = parts.Skip(1).Any(p => p.Equals("--commit", StringComparison.OrdinalIgnoreCase));

        return new FixCommand(commit);
    }
}

/// <summary>
/// Yorumu yazan kişi /fix çalıştırabilir mi? GitHub'ın author_association
/// alanına bakıyoruz.
///
/// Bu kontrol şart: /fix agent'a repo'da kod değiştirtiyor (ve --commit ile
/// commit attırıyor). Herkesin yorum yazabildiği açık bir repoda, yetkisiz
/// birinin bunu tetikleyebilmesi doğrudan bir saldırı yüzeyi olurdu.
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
