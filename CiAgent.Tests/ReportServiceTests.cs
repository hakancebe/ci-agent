using CiAgent.Core;
using Moq;
using Octokit;

namespace CiAgent.Tests;

public class ReportServiceTests
{
    private static ErrorContext SampleContext() => new()
    {
        JobName = "build",
        FailedStepName = "dotnet test",
        FilePath = "Foo.cs",
        LineNumber = 42,
        ErrorMessage = "NullReferenceException"
    };

    private static AnalysisResult SampleResult() => new()
    {
        Summary = "Test derlemesi başarısız oldu.",
        RootCause = "Null referans hatası.",
        SuggestedFix = "Foo.cs:42'de null kontrolü ekle.",
        Confidence = "high"
    };

    // ---------------------------------------------------------------
    // Saf markdown/marker mantığı — GitHub'a hiç dokunmuyor.
    // ---------------------------------------------------------------

    [Fact]
    public void BuildMarker_RunIdiIcerenGizliHtmlYorumuUretir()
    {
        var marker = ReportService.BuildMarker(30797639694);

        Assert.Equal("<!-- ci-agent:30797639694 -->", marker);
    }

    [Fact]
    public void BuildCommentBody_IlkSatirMarkerOlmali()
    {
        var body = ReportService.BuildCommentBody(SampleResult(), SampleContext(), runId: 123);

        Assert.StartsWith(ReportService.BuildMarker(123), body);
    }

    [Fact]
    public void BuildCommentBody_TumBolumleriIcermeli()
    {
        var body = ReportService.BuildCommentBody(SampleResult(), SampleContext(), runId: 123);

        Assert.Contains("### 📋 Özet", body);
        Assert.Contains("### 🔍 Kök Neden", body);
        Assert.Contains("### 🛠️ Önerilen Çözüm", body);
        Assert.Contains("Test derlemesi başarısız oldu.", body);
        Assert.Contains("Null referans hatası.", body);
        Assert.Contains("Foo.cs:42'de null kontrolü ekle.", body);
        Assert.Contains("`build`", body);
        Assert.Contains("`dotnet test`", body);
        Assert.Contains("Foo.cs:42", body);
    }

    [Fact]
    public void BuildJobSummaryBody_PrAtilamadiysaUyariEkler()
    {
        var body = ReportService.BuildJobSummaryBody(SampleResult(), SampleContext(), postedToGitHub: false);

        Assert.Contains("PR/commit yorumu atılamadı", body);
    }

    [Fact]
    public void BuildJobSummaryBody_BasariliysaUyariEklemez()
    {
        var body = ReportService.BuildJobSummaryBody(SampleResult(), SampleContext(), postedToGitHub: true);

        Assert.DoesNotContain("PR/commit yorumu atılamadı", body);
    }

    [Fact]
    public void FindByMarker_EslesenYorumunIdsiniDoner()
    {
        var marker = ReportService.BuildMarker(999);
        var comments = new[]
        {
            (Id: 1L, Body: "alakasız bir yorum"),
            (Id: 2L, Body: marker + "\n## eski analiz sonucu"),
        };

        var result = ReportService.FindByMarker(comments, marker);

        Assert.Equal(2L, result);
    }

    [Fact]
    public void FindByMarker_FarkliRunIdEslesmez()
    {
        // Aynı PR'da farklı run'lardan gelen yorumlar birbirini asla ezmemeli.
        var comments = new[] { (Id: 1L, Body: ReportService.BuildMarker(111) + "\n...") };

        var result = ReportService.FindByMarker(comments, ReportService.BuildMarker(222));

        Assert.Null(result);
    }

    [Fact]
    public void FindByMarker_HicYorumYoksaNullDoner()
    {
        var result = ReportService.FindByMarker(Array.Empty<(long, string)>(), ReportService.BuildMarker(1));

        Assert.Null(result);
    }

    // ---------------------------------------------------------------
    // Octokit'i Moq ile mock'layan uçtan uca ReportAsync testleri.
    //
    // Octokit'in client'ı iç içe interface'lerden oluşuyor:
    // IGitHubClient.Issue (IIssuesClient) .Comment (IIssueCommentsClient)
    // IGitHubClient.Repository (IRepositoriesClient) .Commit (IRepositoryCommitsClient) / .Comment (IRepositoryCommentsClient)
    // Bu yüzden her seviyeyi ayrı bir Mock<T> ile kurup üst seviyenin
    // property'sinden .Returns(...) ile birbirine bağlamamız gerekiyor.
    // ReportService constructor'ı somut GitHubClient yerine IGitHubClient
    // aldığı için gerçek bir ağ çağrısı yapılmadan doğrudan bu mock geçilebiliyor.
    // ---------------------------------------------------------------

    [Fact]
    public async Task ReportAsync_PrBulunduysaVeYorumYoksa_YeniPrYorumuAcar()
    {
        var repoCommitsClient = new Mock<IRepositoryCommitsClient>();
        repoCommitsClient
            .Setup(x => x.PullRequests("owner", "repo", "sha123"))
            .ReturnsAsync(new List<CommitPullRequest> { new(42) }); // GET .../commits/{sha}/pulls -> CommitPullRequest (PullRequest değil!)

        var repositoriesClient = new Mock<IRepositoriesClient>();
        repositoriesClient.Setup(x => x.Commit).Returns(repoCommitsClient.Object);

        var issueCommentsClient = new Mock<IIssueCommentsClient>();
        issueCommentsClient
            .Setup(x => x.GetAllForIssue("owner", "repo", 42))
            .ReturnsAsync(new List<IssueComment>()); // henüz hiç yorum yok

        var issuesClient = new Mock<IIssuesClient>();
        issuesClient.Setup(x => x.Comment).Returns(issueCommentsClient.Object);

        var gitHubClient = new Mock<IGitHubClient>();
        gitHubClient.Setup(x => x.Repository).Returns(repositoriesClient.Object);
        gitHubClient.Setup(x => x.Issue).Returns(issuesClient.Object);

        var service = new ReportService(gitHubClient.Object);

        await service.ReportAsync(SampleResult(), SampleContext(), "owner", "repo", "sha123", runId: 555);

        issueCommentsClient.Verify(
            x => x.Create("owner", "repo", 42, It.Is<string>(b => b.StartsWith(ReportService.BuildMarker(555)))),
            Times.Once);
        issueCommentsClient.Verify(
            x => x.Update(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<long>(), It.IsAny<string>()),
            Times.Never);
    }

    [Fact]
    public async Task ReportAsync_AyniRunTekrarTetiklenirse_VarOlanPrYorumunuGunceller_YeniAcmaz()
    {
        var existingMarker = ReportService.BuildMarker(555);
        var existingComment = new IssueComment(
            id: 99, nodeId: "node", url: "url", htmlUrl: "htmlUrl",
            body: existingMarker + "\n## eski analiz sonucu",
            createdAt: DateTimeOffset.UtcNow, updatedAt: null,
            user: null!, reactions: null!, authorAssociation: default);

        var repoCommitsClient = new Mock<IRepositoryCommitsClient>();
        repoCommitsClient
            .Setup(x => x.PullRequests("owner", "repo", "sha123"))
            .ReturnsAsync(new List<CommitPullRequest> { new(42) });

        var repositoriesClient = new Mock<IRepositoriesClient>();
        repositoriesClient.Setup(x => x.Commit).Returns(repoCommitsClient.Object);

        var issueCommentsClient = new Mock<IIssueCommentsClient>();
        issueCommentsClient
            .Setup(x => x.GetAllForIssue("owner", "repo", 42))
            .ReturnsAsync(new List<IssueComment> { existingComment });

        var issuesClient = new Mock<IIssuesClient>();
        issuesClient.Setup(x => x.Comment).Returns(issueCommentsClient.Object);

        var gitHubClient = new Mock<IGitHubClient>();
        gitHubClient.Setup(x => x.Repository).Returns(repositoriesClient.Object);
        gitHubClient.Setup(x => x.Issue).Returns(issuesClient.Object);

        var service = new ReportService(gitHubClient.Object);

        await service.ReportAsync(SampleResult(), SampleContext(), "owner", "repo", "sha123", runId: 555);

        issueCommentsClient.Verify(x => x.Update("owner", "repo", 99, It.IsAny<string>()), Times.Once);
        issueCommentsClient.Verify(
            x => x.Create(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<long>(), It.IsAny<string>()),
            Times.Never);
    }

    [Fact]
    public async Task ReportAsync_PrBulunamadiysa_CommitYorumunaDuser()
    {
        var repoCommitsClient = new Mock<IRepositoryCommitsClient>();
        repoCommitsClient
            .Setup(x => x.PullRequests("owner", "repo", "sha123"))
            .ReturnsAsync(new List<CommitPullRequest>()); // main'e direkt push — bağlı PR yok

        var commitCommentsClient = new Mock<IRepositoryCommentsClient>();
        commitCommentsClient
            .Setup(x => x.GetAllForCommit("owner", "repo", "sha123"))
            .ReturnsAsync(new List<CommitComment>());

        var repositoriesClient = new Mock<IRepositoriesClient>();
        repositoriesClient.Setup(x => x.Commit).Returns(repoCommitsClient.Object);
        repositoriesClient.Setup(x => x.Comment).Returns(commitCommentsClient.Object);

        var gitHubClient = new Mock<IGitHubClient>();
        gitHubClient.Setup(x => x.Repository).Returns(repositoriesClient.Object);

        var service = new ReportService(gitHubClient.Object);

        await service.ReportAsync(SampleResult(), SampleContext(), "owner", "repo", "sha123", runId: 777);

        commitCommentsClient.Verify(
            x => x.Create("owner", "repo", "sha123", It.Is<NewCommitComment>(c => c.Body.StartsWith(ReportService.BuildMarker(777)))),
            Times.Once);
    }
}
