using System.Text.Json.Serialization;

namespace CiAgent.Core;

/// <summary>
/// LLM'in önerdiği TEK bir dosya değişikliği: "şu metni bul, bununla değiştir".
///
/// Neden diff/yama değil: LLM'ler yamalarda satır numarasını ve bağlamı sık sık
/// yanlış üretiyor, `git apply` tutmuyor. Neden tüm dosyayı yeniden yazdırmıyoruz:
/// büyük dosyalarda model alakasız kod parçalarını sessizce düşürebiliyor.
/// Bul-değiştir, uygulanmadan önce dosyada birebir doğrulanabilen tek biçim.
/// </summary>
public sealed record CodeEdit
{
    /// <summary>Repo kökünden itibaren relative yol.</summary>
    [JsonPropertyName("file")]
    public required string File { get; init; }

    /// <summary>Dosyada BİREBİR var olması gereken metin. Bulunamazsa değişiklik reddedilir.</summary>
    [JsonPropertyName("oldText")]
    public required string OldText { get; init; }

    [JsonPropertyName("newText")]
    public required string NewText { get; init; }

    /// <summary>Bu değişikliğin neden yapıldığı — rapor yorumunda gösteriliyor.</summary>
    [JsonPropertyName("reason")]
    public required string Reason { get; init; }
}

/// <summary>LLM'in bir düzeltme denemesinde döndürdüğü paket.</summary>
public class FixProposal
{
    [JsonPropertyName("summary")]
    public required string Summary { get; init; }

    /// <summary>
    /// Boş liste geçerli bir cevap: "bu hatayı verilen bilgiyle güvenle düzeltemem".
    /// Uydurma bir değişiklik yapmaktansa boş dönmesi yeğdir.
    /// </summary>
    [JsonPropertyName("edits")]
    public List<CodeEdit> Edits { get; init; } = new();
}

/// <summary>Tek bir CodeEdit'in uygulanma sonucu.</summary>
public sealed record EditOutcome(CodeEdit Edit, bool Applied, string? RejectionReason)
{
    public static EditOutcome Ok(CodeEdit edit) => new(edit, true, null);
    public static EditOutcome Rejected(CodeEdit edit, string reason) => new(edit, false, reason);
}
