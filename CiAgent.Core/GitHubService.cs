using System.Net.Http.Headers;
using Octokit;

namespace CiAgent.Core;

public class GitHubService
{
  private readonly GitHubClient _client;
  private readonly HttpClient _http;
  public GitHubService(String token)
  {
    _client = new GitHubClient(new Octokit.ProductHeaderValue("ci-agent"))
    {
      Credentials = new Credentials(token)
    };
    _http = new HttpClient();
    _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    _http.DefaultRequestHeaders.UserAgent.ParseAdd("ci-agent");
    _http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

  }

  // ReportService gibi diğer servislerin aynı authenticated client'ı paylaşması için.
  public GitHubClient Client => _client;

  public Task<WorkflowRun> GetRunAsync(string owner, string repo, long runId)
   => _client.Actions.Workflows.Runs.Get(owner, repo, runId);

  public async Task<IReadOnlyList<WorkflowJob>> GetJobsAsync(string owner, string repo, long runId)
  {
    var response = await _client.Actions.Workflows.Jobs.List(owner, repo, runId);
    return response.Jobs;
  }

  public Task<IReadOnlyList<CheckRunAnnotation>> GetAnnotationsAsync(string owner, string repo, long jobId)
  => _client.Check.Run.GetAllAnnotations(owner, repo, jobId);

  public async Task<string> DownloadJobLogAsync(string owner, string repo, long jobId)
  {
    var url = $"https://api.github.com/repos/{owner}/{repo}/actions/jobs/{jobId}/logs";
    using var response = await _http.GetAsync(url);
    response.EnsureSuccessStatusCode();
    return await response.Content.ReadAsStringAsync();
  }
}