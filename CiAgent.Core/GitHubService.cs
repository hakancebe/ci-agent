using System.Net.Http.Headers;
using System.Text;
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

  // Job logu tamamen belleğe alınıyor; base64 gömülü bir adım logu yüzlerce MB
  // olabildiği için sert bir tavan şart. Sınıra takılırsa akışı kesmiyoruz -
  // elimizdeki kadarıyla analize devam etmek hiç analiz etmemekten iyi.
  private const int MaxLogChars = 10_000_000;

  public async Task<string> DownloadJobLogAsync(string owner, string repo, long jobId)
  {
    var url = $"https://api.github.com/repos/{owner}/{repo}/actions/jobs/{jobId}/logs";
    using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
    response.EnsureSuccessStatusCode();

    using var stream = await response.Content.ReadAsStreamAsync();
    // StreamReader UTF-8 çözümlemesini Decoder ile yaptığı için sınıra denk gelen
    // çok baytlı karakter bölünmüyor - manuel hizalamaya gerek yok.
    using var reader = new StreamReader(stream);

    var buffer = new char[16 * 1024];
    var sb = new StringBuilder(capacity: 64 * 1024);
    var total = 0;

    while (total < MaxLogChars)
    {
      var want = Math.Min(buffer.Length, MaxLogChars - total);
      var read = await reader.ReadAsync(buffer.AsMemory(0, want));
      if (read == 0) break;

      sb.Append(buffer, 0, read);
      total += read;
    }

    // Tam sınırda biten bir log için yanlış uyarı basmamak adına gerçekten
    // devamı var mı diye tek karakter daha yokluyoruz.
    if (total >= MaxLogChars && await reader.ReadAsync(buffer.AsMemory(0, 1)) > 0)
      Console.Error.WriteLine(
        $"UYARI: {owner}/{repo} job {jobId} logu {MaxLogChars:N0} karakter sınırında kırpıldı, analiz eksik loga dayanıyor.");

    return sb.ToString();
  }
}