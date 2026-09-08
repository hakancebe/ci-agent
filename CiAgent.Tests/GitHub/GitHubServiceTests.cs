using System.Net;
using System.Text;
using CiAgent.Core;
using Moq;
using Octokit;

namespace CiAgent.Tests;

// GitHubService normalde token'la gerçek bir GitHubClient kurar; testler için
// internal IGitHubClient ctor'u kullanılıyor (ReportServiceTests'teki mock kurulum
// pattern'i örnek alındı): IGitHubClient.Repository (IRepositoriesClient) .Content
// (IRepositoryContentsClient) - her seviye ayrı bir Mock<T> ile kurulup üst seviyenin
// property'sinden .Returns(...) ile birbirine bağlanıyor.
public class GitHubServiceTests
{
    private static RepositoryContent FileContent(string name, string path, string textContent)
    {
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(textContent));
        return new RepositoryContent(
            name: name, path: path, sha: "sha", size: textContent.Length,
            type: ContentType.File, downloadUrl: "http://x", url: "http://x", gitUrl: "http://x",
            htmlUrl: "http://x", encoding: "base64", encodedContent: encoded, target: null, submoduleGitUrl: null);
    }

    private static (GitHubService Service, Mock<IRepositoryContentsClient> ContentsClient) BuildService()
    {
        var contentsClient = new Mock<IRepositoryContentsClient>();

        var repositoriesClient = new Mock<IRepositoriesClient>();
        repositoriesClient.Setup(x => x.Content).Returns(contentsClient.Object);

        var gitHubClient = new Mock<IGitHubClient>();
        gitHubClient.Setup(x => x.Repository).Returns(repositoriesClient.Object);

        // GitHubService(IGitHubClient) ctor'u internal - InternalsVisibleTo("CiAgent.Tests")
        // sayesinde burada doğrudan çağrılabiliyor.
        var service = new GitHubService(gitHubClient.Object);
        return (service, contentsClient);
    }

    [Fact]
    public async Task GetFileContentAsync_ReturnsDecodedContent_WhenFileExists()
    {
        var (service, contentsClient) = BuildService();

        contentsClient
            .Setup(x => x.GetAllContentsByRef("owner", "repo", "src/Foo.cs", "sha123"))
            .ReturnsAsync(new List<RepositoryContent> { FileContent("Foo.cs", "src/Foo.cs", "public class Foo {}") });

        var result = await service.GetFileContentAsync("owner", "repo", "src/Foo.cs", "sha123");

        Assert.Equal("public class Foo {}", result);
    }

    [Fact]
    public async Task GetFileContentAsync_ReturnsNull_WhenFileNotFound()
    {
        var (service, contentsClient) = BuildService();

        contentsClient
            .Setup(x => x.GetAllContentsByRef("owner", "repo", "src/Missing.cs", "sha123"))
            .ThrowsAsync(new NotFoundException("Not Found", HttpStatusCode.NotFound));

        var result = await service.GetFileContentAsync("owner", "repo", "src/Missing.cs", "sha123");

        Assert.Null(result);
    }

    [Fact]
    public async Task GetFileContentAsync_PropagatesOtherExceptions_InsteadOfSwallowingThem()
    {
        var (service, contentsClient) = BuildService();

        contentsClient
            .Setup(x => x.GetAllContentsByRef("owner", "repo", "src/Foo.cs", "sha123"))
            .ThrowsAsync(new HttpRequestException("network patladı"));

        await Assert.ThrowsAsync<HttpRequestException>(
            () => service.GetFileContentAsync("owner", "repo", "src/Foo.cs", "sha123"));
    }

    [Fact]
    public async Task GetFileContentAsync_ReturnsNull_WhenApiReturnsEmptyList()
    {
        var (service, contentsClient) = BuildService();

        contentsClient
            .Setup(x => x.GetAllContentsByRef("owner", "repo", "src/Foo.cs", "sha123"))
            .ReturnsAsync(new List<RepositoryContent>());

        var result = await service.GetFileContentAsync("owner", "repo", "src/Foo.cs", "sha123");

        Assert.Null(result);
    }
}
