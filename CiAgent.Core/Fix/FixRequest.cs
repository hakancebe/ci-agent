namespace CiAgent.Core;

/// <summary>
/// Bir /fix çalıştırmasının girdileri. Workflow bunları issue_comment
/// olayının payload'ından besliyor.
/// </summary>
public sealed record FixRequest
{
    public required string Owner { get; init; }
    public required string Repo { get; init; }
    public required int PullRequestNumber { get; init; }

    /// <summary>Yorumun kimliği — marker ve 👀 tepkisi için.</summary>
    public required long CommentId { get; init; }

    public required string CommentBody { get; init; }

    /// <summary>GitHub'ın author_association alanı; yetki bununla belirleniyor.</summary>
    public required string AuthorAssociation { get; init; }
}

public enum FixRunStatus
{
    /// <summary>Yorum /fix komutu değildi — sessizce çıkıldı.</summary>
    NotACommand,

    /// <summary>Yorumu yazanın yazma yetkisi yok.</summary>
    NotAuthorized,

    /// <summary>PR bir fork'tan geliyor — o dala push edilemez.</summary>
    ForkNotSupported,

    /// <summary>Bu PR için başarısız bir CI run'ı bulunamadı.</summary>
    NoFailedRun,

    /// <summary>Çalışma dizini hazırlanamadı (klonlama başarısız).</summary>
    WorkspaceUnavailable,

    /// <summary>Düzeltme denendi; sonuç <see cref="FixRunResult.Fix"/> içinde.</summary>
    Completed
}

public sealed record FixRunResult(
    FixRunStatus Status,
    FixOutcome? Fix = null,
    bool Pushed = false,
    string? Message = null);
