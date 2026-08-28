using CiAgent.Core;
using Moq;
using Octokit;

namespace CiAgent.Tests;

/// <summary>
/// Buradaki asıl mesele yetki kapısı: yetkisiz bir yorum için hiçbir GitHub
/// çağrısı, hiçbir LLM isteği yapılmamalı.
/// </summary>
public class FixCoordinatorTests
{
    /// <summary>Çağrıldığı anda testi düşüren gateway — "hiç dokunulmadı" iddiasını sabitler.</summary>
    private sealed class ExplodingGateway : IGitHubGateway
    {
        public Task<IReadOnlyList<WorkflowJob>> GetJobsAsync(string o, string r, long id)
            => throw new InvalidOperationException("GitHub'a hiç gidilmemeliydi");
        public Task<IReadOnlyList<CheckRunAnnotation>> GetAnnotationsAsync(string o, string r, long id)
            => throw new InvalidOperationException("GitHub'a hiç gidilmemeliydi");
        public Task<string> DownloadJobLogAsync(string o, string r, long id)
            => throw new InvalidOperationException("GitHub'a hiç gidilmemeliydi");
        public Task<string?> GetFileContentAsync(string o, string r, string p, string s)
            => throw new InvalidOperationException("GitHub'a hiç gidilmemeliydi");
    }

    private sealed class ExplodingVerifier : IVerificationRunner
    {
        public Task<VerificationResult> VerifyAsync(string workingDirectory)
            => throw new InvalidOperationException("doğrulama hiç çalışmamalıydı");
    }

    /// <summary>Ağa çıkmayan LlmService; çağrılırsa test düşer.</summary>
    private sealed class ExplodingLlm : LlmService
    {
        internal override Task<string> CompleteAsync(
            List<OpenAI.Chat.ChatMessage> m, OpenAI.Chat.ChatCompletionOptions o)
            => throw new InvalidOperationException("LLM'e hiç gidilmemeliydi");
    }

    private static FixCoordinator Build(out Mock<IIssueCommentsClient> comments)
    {
        comments = new Mock<IIssueCommentsClient>();
        var issues = new Mock<IIssuesClient>();
        issues.Setup(x => x.Comment).Returns(comments.Object);

        var client = new Mock<IGitHubClient>();
        client.Setup(x => x.Issue).Returns(issues.Object);

        var llm = new ExplodingLlm();
        var github = new GitHubService("sahte-token");

        return new FixCoordinator(
            github,
            new CiAnalysisPipeline(new ExplodingGateway(), llm, new ReportService(client.Object)),
            new FixPipeline(llm, new ExplodingVerifier()),
            new PrCommenter(client.Object));
    }

    private static FixRequest Request(string body, string association) => new()
    {
        Owner = "o", Repo = "r", PullRequestNumber = 7,
        CommentId = 42, CommentBody = body, AuthorAssociation = association,
        WorkspaceRoot = Path.GetTempPath()
    };

    [Theory]
    [InlineData("normal bir yorum")]
    [InlineData("bence /fix çalıştıralım")]
    [InlineData("")]
    public async Task RunAsync_DoesNothing_WhenCommentIsNotACommand(string body)
    {
        var coordinator = Build(out var comments);

        var result = await coordinator.RunAsync(Request(body, "OWNER"));

        Assert.Equal(FixRunStatus.NotACommand, result.Status);
        // Hiç yorum atılmamalı: her yoruma cevap veren bir bot gürültü olurdu.
        comments.Verify(x => x.Create(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<long>(),
            It.IsAny<string>()), Times.Never);
    }

    [Theory]
    [InlineData("CONTRIBUTOR")]
    [InlineData("FIRST_TIME_CONTRIBUTOR")]
    [InlineData("NONE")]
    public async Task RunAsync_StopsBeforeAnyWork_WhenAuthorLacksWriteAccess(string association)
    {
        var coordinator = Build(out var comments);

        // Gateway/LLM/verifier çağrılırsa exception fırlatır; buraya kadar
        // sorunsuz gelmesi hiçbirine dokunulmadığının kanıtı.
        var result = await coordinator.RunAsync(Request("/fix", association));

        Assert.Equal(FixRunStatus.NotAuthorized, result.Status);
        Assert.Null(result.Fix);
        Assert.False(result.Pushed);
        comments.Verify(x => x.Create(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<long>(),
            It.IsAny<string>()), Times.Never);
    }
}
