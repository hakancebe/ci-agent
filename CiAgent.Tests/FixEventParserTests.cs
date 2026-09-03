using System.Text.Json;
using CiAgent.Service;

namespace CiAgent.Tests;

/// <summary>
/// issue_comment olayının /fix işine dönüşmesi. Buradaki elemelerin tamamı
/// eskiden ci-agent-fix.yml'deki tek satırlık bir `if:` ifadesiydi:
///
///   github.event.issue.pull_request &amp;&amp;
///   startsWith(github.event.comment.body, '/fix') &amp;&amp;
///   contains(fromJSON('["OWNER","MEMBER","COLLABORATOR"]'), ...author_association)
///
/// O ifadede bir yazım hatası, ya agent'ın hiç çalışmaması ya da yetkisiz birinin
/// tetikleyebilmesi demekti — ve hiçbir test bunu yakalayamazdı.
/// </summary>
public class FixEventParserTests
{
    private static JsonDocument Payload(
        string action = "created",
        string body = "/fix",
        string association = "OWNER",
        bool isPullRequest = true,
        long? installationId = 999,
        int prNumber = 7,
        long commentId = 555)
    {
        var pullRequest = isPullRequest
            ? """, "pull_request": { "url": "https://api.github.com/repos/o/r/pulls/7" }"""
            : "";

        var installation = installationId is null
            ? ""
            : $$""", "installation": { "id": {{installationId}} }""";

        return JsonDocument.Parse($$"""
        {
          "action": "{{action}}",
          "issue": { "number": {{prNumber}}{{pullRequest}} },
          "comment": {
            "id": {{commentId}},
            "body": {{JsonSerializer.Serialize(body)}},
            "author_association": "{{association}}"
          },
          "repository": {
            "name": "ci-agent-pilot",
            "owner": { "login": "hakancebe" }
          }{{installation}}
        }
        """);
    }

    [Fact]
    public void Parse_AcceptsAuthorizedFixComment()
    {
        using var payload = Payload();

        var (job, _) = FixEventParser.Parse("issue_comment", "d-1", payload);

        Assert.NotNull(job);
        Assert.Equal("hakancebe", job!.Owner);
        Assert.Equal("ci-agent-pilot", job.Repo);
        Assert.Equal(7, job.PullRequestNumber);
        Assert.Equal(555, job.CommentId);
        Assert.Equal("OWNER", job.AuthorAssociation);
        Assert.Equal(999, job.InstallationId);
    }

    [Theory]
    [InlineData("OWNER")]
    [InlineData("MEMBER")]
    [InlineData("COLLABORATOR")]
    public void Parse_AcceptsWriteCapableAssociations(string association)
    {
        using var payload = Payload(association: association);

        var (job, _) = FixEventParser.Parse("issue_comment", "d", payload);

        Assert.NotNull(job);
    }

    [Theory]
    [InlineData("CONTRIBUTOR")]      // daha önce PR'ı merge edilmiş ama yazma yetkisi yok
    [InlineData("FIRST_TIME_CONTRIBUTOR")]
    [InlineData("NONE")]
    public void Parse_RejectsNonWriteAssociations(string association)
    {
        // Açık bir repoda herkes yorum yazabiliyor; bu kapı olmasa yabancı biri
        // agent'a kod değiştirtip commit attırabilirdi.
        using var payload = Payload(association: association);

        var (job, reason) = FixEventParser.Parse("issue_comment", "d", payload);

        Assert.Null(job);
        Assert.Contains("yetki", reason);
    }

    [Fact]
    public void Parse_RejectsCommentOnIssueNotPullRequest()
    {
        // issue_comment hem issue'lar hem PR'lar için tetikleniyor; issue'da
        // düzeltilecek bir dal yok.
        using var payload = Payload(isPullRequest: false);

        var (job, reason) = FixEventParser.Parse("issue_comment", "d", payload);

        Assert.Null(job);
        Assert.Contains("PR", reason);
    }

    [Theory]
    [InlineData("bence burada /fix çalıştırmalıyız")]
    [InlineData("normal bir yorum")]
    [InlineData("```\n/fix\n```")]
    public void Parse_RejectsBodiesWhereFixIsNotTheCommand(string body)
    {
        // FixCommand.TryParse yalnızca İLK satıra bakıyor ve satır /fix ile
        // BAŞLAMAK zorunda. YAML'deki startsWith(...) bundan daha gevşekti:
        // alıntı ya da kod bloğu içindeki /fix'i ayırt edemezdi.
        using var payload = Payload(body: body);

        var (job, _) = FixEventParser.Parse("issue_comment", "d", payload);

        Assert.Null(job);
    }

    [Fact]
    public void Parse_AcceptsFixWithArguments()
    {
        using var payload = Payload(body: "/fix --dry-run");

        var (job, _) = FixEventParser.Parse("issue_comment", "d", payload);

        Assert.NotNull(job);
        // Gövde olduğu gibi taşınıyor: --dry-run kararını Core'daki FixCommand
        // veriyor, burada yorumlanmıyor.
        Assert.Equal("/fix --dry-run", job!.CommentBody);
    }

    [Theory]
    [InlineData("edited")]
    [InlineData("deleted")]
    public void Parse_IgnoresNonCreatedActions(string action)
    {
        // Yorum düzenleme ile /fix tetiklenebilseydi, birisi eski bir yorumu
        // düzenleyerek agent'ı tekrar tekrar çalıştırabilirdi.
        using var payload = Payload(action: action);

        var (job, _) = FixEventParser.Parse("issue_comment", "d", payload);

        Assert.Null(job);
    }

    [Fact]
    public void Parse_IgnoresOtherEventTypes()
    {
        using var payload = Payload();

        var (job, reason) = FixEventParser.Parse("workflow_run", "d", payload);

        Assert.Null(job);
        Assert.Contains("workflow_run", reason);
    }

    [Fact]
    public void Parse_RejectsPayloadWithoutInstallationId()
    {
        using var payload = Payload(installationId: null);

        var (job, reason) = FixEventParser.Parse("issue_comment", "d", payload);

        Assert.Null(job);
        Assert.Contains("installation", reason);
    }

    [Fact]
    public void ConcurrencyKey_IdentifiesPullRequest()
    {
        // Eşzamanlılık anahtarı PR'ı tanımlamalı: aynı PR'da iki /fix aynı dala
        // push etmeye çalışır ve biri çakışır.
        var job = new FixJob("d", "hakancebe", "ci-agent-pilot", 7, 1, "/fix", "OWNER", 9);

        Assert.Equal("hakancebe/ci-agent-pilot#7", job.ConcurrencyKey);
    }
}
