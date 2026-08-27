using System.Reflection;
using CiAgent.Core;
using Moq;
using Octokit;

namespace CiAgent.Tests;

public class ReportServiceTests
{
    // CommitPullRequest.State'in setter'ı protected (Octokit sınıfı dışarıdan
    // sadece constructor ile doldurulmak üzere tasarlanmış), o yüzden "State = Open"
    // şeklinde object initializer ile atanamıyor. Reflection ile setliyoruz.
    private static CommitPullRequest OpenPullRequest(int number)
    {
        var pr = new CommitPullRequest(number);
        typeof(CommitPullRequest)
            .GetProperty(nameof(CommitPullRequest.State))!
            .SetValue(pr, new StringEnum<ItemState>(ItemState.Open));
        return pr;
    }

    // FindPullRequestNumberAsync'in birden-fazla-PR ayıklama mantığını test edebilmek
    // için Head (GitReference) ve UpdatedAt de gerekiyor; ikisinin de setter'ı protected,
    // OpenPullRequest'teki gibi reflection ile dolduruyoruz.
    private static CommitPullRequest PullRequestWithHead(
        int number, string headSha, DateTimeOffset updatedAt, ItemState state = ItemState.Open)
    {
        var pr = new CommitPullRequest(number);
        var head = new GitReference(null!, null!, null!, null!, headSha, null!, null!);

        typeof(CommitPullRequest).GetProperty(nameof(CommitPullRequest.Head))!.SetValue(pr, head);
        typeof(CommitPullRequest).GetProperty(nameof(CommitPullRequest.UpdatedAt))!.SetValue(pr, updatedAt);
        typeof(CommitPullRequest).GetProperty(nameof(CommitPullRequest.State))!
            .SetValue(pr, new StringEnum<ItemState>(state));

        return pr;
    }

    private static ErrorContext SampleContext() => new()
    {
        JobName = "build",
        FailedStepName = "dotnet test",
        Failures =
        {
            new Failure
            {
                Kind = FailureKind.Test,
                Name = "FooTests.Bar",
                JobName = "build",
                StepName = "dotnet test",
                FilePath = "Foo.cs",
                LineNumber = 42,
                Message = "NullReferenceException"
            }
        }
    };

    private static AnalysisResult SampleResult() => new()
    {
        Summary = "Test derlemesi başarısız oldu.",
        Analyses =
        {
            new Analysis
            {
                Title = "Null referans",
                RootCause = "Null referans hatası.",
                SuggestedFix = "Foo.cs:42'de null kontrolü ekle.",
                Confidence = "high"
            }
        }
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
        Assert.Contains("**🛠️ Önerilen Çözüm**", body);
        Assert.Contains("Test derlemesi başarısız oldu.", body);
        Assert.Contains("Null referans hatası.", body);
        Assert.Contains("Foo.cs:42'de null kontrolü ekle.", body);
        Assert.Contains("`build`", body);
        Assert.Contains("`dotnet test`", body);
        Assert.Contains("Foo.cs:42", body);
    }

    [Fact]
    public void BuildCommentBody_AnalizEksikVeriyleYapildiysaUyariGosterir()
    {
        var result = new AnalysisResult
        {
            Summary = "Test başarısız.",
            Analyses =
            {
                new Analysis
                {
                    Title = "Null referans",
                    RootCause = "Null referans.",
                    SuggestedFix = "Null kontrolü ekle.",
                    Confidence = "medium"
                }
            },
            ReductionNote = "Prompt 50.000 karakter limitine sığması için şunlar çıkarıldı: ham log kesiti."
        };

        var body = ReportService.BuildCommentBody(result, SampleContext(), runId: 123);

        // Uyarı, kök nedenden ÖNCE görünmeli - okuyucu analizin eksik veriye
        // dayandığını sonuçları okumadan bilmeli.
        Assert.Contains("ham log kesiti", body);
        Assert.True(body.IndexOf("ham log kesiti", StringComparison.Ordinal)
                    < body.IndexOf("### 🔍 Kök Neden", StringComparison.Ordinal));
    }

    [Fact]
    public void BuildCommentBody_TamPromptGittiyseUyariGostermez()
    {
        var body = ReportService.BuildCommentBody(SampleResult(), SampleContext(), runId: 123);

        Assert.DoesNotContain("çıkarıldı", body);
    }

    [Fact]
    public void BuildCommentBody_BirdenFazlaKokNedeniNumaraliBolumlerHalindeYazar()
    {
        var result = new AnalysisResult
        {
            Summary = "İki bağımsız sorun var.",
            Analyses =
            {
                new Analysis
                {
                    Title = "Eksik NuGet paketi", RootCause = "Paket bulunamadı",
                    SuggestedFix = "Referansı kaldır", Confidence = "high"
                },
                new Analysis
                {
                    Title = "Calculator.Add hatalı", RootCause = "Yanlış operatör",
                    SuggestedFix = "return a + b", Confidence = "medium"
                }
            }
        };

        var body = ReportService.BuildCommentBody(result, SampleContext(), runId: 123);

        Assert.Contains("### 🔍 Kök Neden 1/2 — Eksik NuGet paketi", body);
        Assert.Contains("### 🔍 Kök Neden 2/2 — Calculator.Add hatalı", body);
        // Her analiz kendi güven düzeyini taşımalı - tek bir üst seviye rozet yok.
        Assert.Contains("🟢 Yüksek", body);
        Assert.Contains("🟡 Orta", body);
    }

    [Fact]
    public void BuildCommentBody_TekKokNedendeNumaralandirmaYapmaz()
    {
        var body = ReportService.BuildCommentBody(SampleResult(), SampleContext(), runId: 123);

        Assert.Contains("### 🔍 Kök Neden", body);
        Assert.DoesNotContain("Kök Neden 1/1", body);
    }

    [Fact]
    public void BuildCommentBody_TekrarlananHatalariTekSatirdaSayarak_TumHatalariListeler()
    {
        var context = new ErrorContext
        {
            JobName = "build (ubuntu), build (windows)",
            FailedStepName = "Test",
            Failures =
            {
                new Failure { Kind = FailureKind.Test, Name = "CalcTests.Add", JobName = "build (ubuntu)",
                              FilePath = "src/Calc.cs", LineNumber = 12, Message = "Values differ" },
                new Failure { Kind = FailureKind.Test, Name = "CalcTests.Add", JobName = "build (windows)",
                              FilePath = "src/Calc.cs", LineNumber = 12, Message = "Values differ" },
                new Failure { Kind = FailureKind.Restore, JobName = "deploy",
                              Message = "NU1101: paket yok" }
            }
        };

        var body = ReportService.BuildCommentBody(SampleResult(), context, runId: 123);

        // 2 farklı hata, toplam 3 tekrar.
        Assert.Contains("2 farklı hata (3 tekrar)", body);
        // Katlanmış detay bloğunda her iki hata da görünmeli.
        Assert.Contains("<details>", body);
        Assert.Contains("CalcTests.Add", body);
        Assert.Contains("NU1101: paket yok", body);
        Assert.Contains("aynı hata 2 kez: build (ubuntu), build (windows)", body);
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
            .ReturnsAsync(new List<CommitPullRequest> { OpenPullRequest(42) }); // GET .../commits/{sha}/pulls -> CommitPullRequest (PullRequest değil!)

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
            .ReturnsAsync(new List<CommitPullRequest> { OpenPullRequest(42) });

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
    public async Task ReportAsync_BirdenFazlaPrDonerse_HeadSHAsiTamEslesenTercihEdilir()
    {
        var now = DateTimeOffset.UtcNow;
        // PR 10: bu SHA sadece atası (stacked PR / rebase sonrası eski geçmiş) - HEAD'i farklı bir commit.
        var ancestorOnlyPr = PullRequestWithHead(10, headSha: "baska-bir-sha", updatedAt: now.AddMinutes(5));
        // PR 42: bu SHA'nın gerçek HEAD'i olduğu PR.
        var exactMatchPr = PullRequestWithHead(42, headSha: "sha123", updatedAt: now);

        var repoCommitsClient = new Mock<IRepositoryCommitsClient>();
        repoCommitsClient
            .Setup(x => x.PullRequests("owner", "repo", "sha123"))
            // Bilerek ancestor-only olanı önce koyduk: eski "FirstOrDefault" mantığı
            // yanlışlıkla PR 10'u seçerdi.
            .ReturnsAsync(new List<CommitPullRequest> { ancestorOnlyPr, exactMatchPr });

        var repositoriesClient = new Mock<IRepositoriesClient>();
        repositoriesClient.Setup(x => x.Commit).Returns(repoCommitsClient.Object);

        var issueCommentsClient = new Mock<IIssueCommentsClient>();
        issueCommentsClient
            .Setup(x => x.GetAllForIssue("owner", "repo", 42))
            .ReturnsAsync(new List<IssueComment>());

        var issuesClient = new Mock<IIssuesClient>();
        issuesClient.Setup(x => x.Comment).Returns(issueCommentsClient.Object);

        var gitHubClient = new Mock<IGitHubClient>();
        gitHubClient.Setup(x => x.Repository).Returns(repositoriesClient.Object);
        gitHubClient.Setup(x => x.Issue).Returns(issuesClient.Object);

        var service = new ReportService(gitHubClient.Object);

        await service.ReportAsync(SampleResult(), SampleContext(), "owner", "repo", "sha123", runId: 555);

        issueCommentsClient.Verify(
            x => x.Create("owner", "repo", 42, It.IsAny<string>()), Times.Once);
        issueCommentsClient.Verify(
            x => x.Create("owner", "repo", 10, It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task ReportAsync_AyniHeadShayiPaylasanBirdenFazlaAcikPrVarsa_EnSonGuncelleneniSecer()
    {
        var now = DateTimeOffset.UtcNow;
        // İki branch tam olarak aynı commit'te (örn. biri diğerinden hiç yeni commit
        // eklemeden dallanmış), ikisinin de HEAD'i "sha123". Eski satırı önce koyuyoruz
        // ki test, sadece API sırasına değil UpdatedAt'e göre seçildiğini kanıtlasın.
        var olderPr = PullRequestWithHead(10, headSha: "sha123", updatedAt: now.AddHours(-1));
        var newerPr = PullRequestWithHead(42, headSha: "sha123", updatedAt: now);

        var repoCommitsClient = new Mock<IRepositoryCommitsClient>();
        repoCommitsClient
            .Setup(x => x.PullRequests("owner", "repo", "sha123"))
            .ReturnsAsync(new List<CommitPullRequest> { olderPr, newerPr });

        var repositoriesClient = new Mock<IRepositoriesClient>();
        repositoriesClient.Setup(x => x.Commit).Returns(repoCommitsClient.Object);

        var issueCommentsClient = new Mock<IIssueCommentsClient>();
        issueCommentsClient
            .Setup(x => x.GetAllForIssue("owner", "repo", 42))
            .ReturnsAsync(new List<IssueComment>());

        var issuesClient = new Mock<IIssuesClient>();
        issuesClient.Setup(x => x.Comment).Returns(issueCommentsClient.Object);

        var gitHubClient = new Mock<IGitHubClient>();
        gitHubClient.Setup(x => x.Repository).Returns(repositoriesClient.Object);
        gitHubClient.Setup(x => x.Issue).Returns(issuesClient.Object);

        var service = new ReportService(gitHubClient.Object);

        await service.ReportAsync(SampleResult(), SampleContext(), "owner", "repo", "sha123", runId: 555);

        issueCommentsClient.Verify(
            x => x.Create("owner", "repo", 42, It.IsAny<string>()), Times.Once);
        issueCommentsClient.Verify(
            x => x.Create("owner", "repo", 10, It.IsAny<string>()), Times.Never);
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

    [Fact]
    public void BuildCommentBody_RendersSkippedNotice_InsteadOfAnalysis()
    {
        var result = AnalysisResult.ForSkipped(promptChars: 62_063, maxChars: 50_000);
        var context = new ErrorContext
        {
            JobName = "build-test",
            FailedStepName = "Test",
            Failures =
            {
                new Failure
                {
                    Kind = FailureKind.Test, Name = "CalculatorTests.Add",
                    FilePath = "src/Calculator.cs", LineNumber = 42,
                    Message = "Values differ"
                }
            }
        };

        var body = ReportService.BuildCommentBody(result, context, runId: 999);

        // Marker/idempotency korunmalı - dedup mantığı buna bakıyor.
        Assert.StartsWith(ReportService.BuildMarker(999), body.TrimStart());

        // Atlandığı açıkça yazmalı, sayılar gövdede olmalı.
        Assert.Contains("Otomatik Analiz Atlandı", body);
        Assert.Contains("62.063", body);
        Assert.Contains("50.000", body);
        Assert.Contains("Elle inceleme gerekiyor", body);

        // Analiz başlıkları HİÇ görünmemeli - uydurma kök neden izlenimi vermesin.
        Assert.DoesNotContain("Kök Neden", body);
        Assert.DoesNotContain("Önerilen Çözüm", body);
        Assert.DoesNotContain("Güven düzeyi", body);
    }

    [Fact]
    public void BuildJobSummaryBody_RendersSkippedNotice_InsteadOfAnalysis()
    {
        var result = AnalysisResult.ForSkipped(promptChars: 62_063, maxChars: 50_000);
        var context = new ErrorContext { JobName = "build-test", FailedStepName = "Test" };

        var body = ReportService.BuildJobSummaryBody(result, context, postedToGitHub: true);

        Assert.Contains("Otomatik Analiz Atlandı", body);
        Assert.Contains("62.063", body);
        Assert.Contains("50.000", body);
        Assert.Contains("Elle inceleme gerekiyor", body);
        Assert.DoesNotContain("Kök Neden", body);
    }
}
