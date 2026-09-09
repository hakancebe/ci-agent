using System.Text.Json;
using CiAgent.Core;

namespace CiAgent.Service;

/// <summary>
/// Bir /fix çalıştırması için Container Apps Job'a geçirilecek bilgiler.
/// </summary>
internal sealed record FixJob(
    string DeliveryId,
    string Owner,
    string Repo,
    int PullRequestNumber,
    long CommentId,
    string CommentBody,
    string AuthorAssociation,
    long InstallationId)
{
    /// <summary>
    /// Eşzamanlılık anahtarı: aynı PR'da iki /fix aynı anda çalışmamalı, yoksa
    /// ikisi de aynı dala push etmeye çalışır ve biri çakışır.
    /// </summary>
    public string ConcurrencyKey => $"{Owner}/{Repo}#{PullRequestNumber}";

    public override string ToString() => ConcurrencyKey;
}

/// <summary>
/// issue_comment olayını /fix işine çevirir.
///
/// Buradaki elemeler eski ci-agent-fix.yml'deki `if:` bloğunun karşılığı — ama
/// nihai kararlar DEĞİL, yalnızca ucuz ön elemeler. Gerçek yetki ve komut kararı
/// CiAgent.Core'daki FixAuthorization/FixCommand'de, testleriyle birlikte duruyor;
/// burada onları tekrar çağırarak "boşuna container başlatma" maliyetinden
/// kaçınıyoruz. YAML'de bu iki kural kopyalanmıştı; burada aynı koda gidiyorlar.
/// </summary>
internal static class FixEventParser
{
    public static (FixJob? Job, string Reason) Parse(
        string eventName, string deliveryId, JsonDocument payload)
    {
        if (!string.Equals(eventName, "issue_comment", StringComparison.OrdinalIgnoreCase))
            return (null, $"'{eventName}' bir /fix olayı değil");

        var root = payload.RootElement;

        if (GetString(root, "action") != "created")
            return (null, "yalnızca yeni yorumlar işleniyor (düzenleme/silme değil)");

        if (!root.TryGetProperty("issue", out var issue))
            return (null, "payload'da issue yok");

        // issue_comment hem issue'lar hem PR'lar için tetikleniyor; PR olmayanı
        // eliyoruz çünkü düzeltilecek bir dal yok.
        if (!issue.TryGetProperty("pull_request", out _))
            return (null, "yorum bir PR'da değil, issue'da");

        if (!issue.TryGetProperty("number", out var numberElement)
            || !numberElement.TryGetInt32(out var prNumber))
        {
            return (null, "PR numarası okunamadı");
        }

        if (!root.TryGetProperty("comment", out var comment))
            return (null, "payload'da comment yok");

        var body = GetString(comment, "body") ?? "";

        // FixCommand.TryParse: "/fix" yorumun İLK satırında olmalı. Bu, YAML'deki
        // startsWith(...) kontrolünden daha sıkı — "bence /fix çalıştıralım" gibi
        // bir cümle agent'ı tetiklemiyor.
        if (FixCommand.TryParse(body) is null)
            return (null, "yorum bir /fix komutu değil");

        var association = GetString(comment, "author_association");
        if (!FixAuthorization.CanRunFix(association))
            return (null, $"yazanın yetkisi yok (author_association={association})");

        if (!comment.TryGetProperty("id", out var commentIdElement)
            || !commentIdElement.TryGetInt64(out var commentId))
        {
            return (null, "yorum id'si okunamadı");
        }

        if (!root.TryGetProperty("repository", out var repository))
            return (null, "payload'da repository yok");

        var repo = GetString(repository, "name");
        var owner = repository.TryGetProperty("owner", out var ownerElement)
            ? GetString(ownerElement, "login")
            : null;

        if (string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(repo))
            return (null, "repository owner/name okunamadı");

        if (!root.TryGetProperty("installation", out var installation)
            || !installation.TryGetProperty("id", out var installationIdElement)
            || !installationIdElement.TryGetInt64(out var installationId))
        {
            return (null, "payload'da installation.id yok (App webhook'u değil?)");
        }

        return (new FixJob(
            deliveryId, owner!, repo!, prNumber, commentId, body,
            association ?? "", installationId), "kuyruğa alındı");
    }

    private static string? GetString(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
