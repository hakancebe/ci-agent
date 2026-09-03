using System.Net.Http.Headers;
using System.Text;
using Octokit;

namespace CiAgent.Core;

public class GitHubService : IGitHubGateway
{
  private readonly IGitHubClient _client;
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

  // Testler için: gerçek ağa hiç çıkmayan bir IGitHubClient (Moq) enjekte edilebilir
  // (bkz. GitHubServiceTests, ReportServiceTests'teki mock kurulum pattern'i örnek alındı).
  // _http bu ctor'la kurulmaz - sadece Content API'ye dokunan testlerde kullanılır,
  // DownloadJobLogAsync bu şekilde üretilen bir örnekle çağrılmamalı.
  internal GitHubService(IGitHubClient client)
  {
    _client = client;
    _http = null!;
  }

  // ReportService gibi diğer servislerin aynı authenticated client'ı paylaşması için.
  public IGitHubClient Client => _client;

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

  // --- /fix için gerekenler ------------------------------------------

  /// <summary>PR'ın dalı, HEAD commit'i ve fork'tan gelip gelmediği.</summary>
  public async Task<PullRequestInfo> GetPullRequestInfoAsync(
    string owner, string repo, int prNumber)
  {
    var pr = await _client.PullRequest.Get(owner, repo, prNumber);
    return PullRequestInfo.From(pr);
  }

  /// <summary>
  /// Bir dal üzerindeki EN SON başarısız workflow run'ı. /fix'in hangi hatayı
  /// düzelteceğini bilmesi için gerekiyor: yorum olayı bu bilgiyi taşımıyor.
  /// </summary>
  public async Task<long?> FindLatestFailedRunAsync(string owner, string repo, string branch)
  {
    var response = await _client.Actions.Workflows.Runs.List(
      owner, repo, new WorkflowRunsRequest { Branch = branch });

    return SelectLatestFailedRun(response.WorkflowRuns);
  }

  /// <summary>
  /// Seçim mantığı ayrı: API sırasına güvenilmiyor, en yeni başarısız run
  /// açıkça CreatedAt'e göre seçiliyor.
  /// </summary>
  internal static long? SelectLatestFailedRun(IEnumerable<WorkflowRun> runs) =>
    runs.Where(r => r.Conclusion?.StringValue == "failure")
        .OrderByDescending(r => r.CreatedAt)
        .Select(r => (long?)r.Id)
        .FirstOrDefault();

  // "Koda bakma" özelliği: ErrorContext'te FilePath+LineNumber ikisi de doluysa
  // (compile/test hataları) LLM prompt'una eklenecek kod kesiti için bu dosyanın
  // içeriği çekilir. Dosya bulunamazsa (silinmiş, yanlış path, vb.) null dönülür -
  // exception dışa sızdırılmaz, çağıran taraf (Program.cs) kod kesiti olmadan devam
  // eder. Ağ/izin gibi diğer hatalar ise olduğu gibi yukarı fırlatılır; Program.cs
  // zaten bunu try-catch ile ele alıyor.
  public async Task<string?> GetFileContentAsync(string owner, string repo, string path, string ref_)
  {
    IReadOnlyList<RepositoryContent> contents;
    try
    {
      contents = await _client.Repository.Content.GetAllContentsByRef(owner, repo, path, ref_);
    }
    catch (NotFoundException)
    {
      return null;
    }

    if (contents.Count == 0)
      return null;

    var file = contents[0];

    // Octokit .Content, .EncodedContent'i base64'ten zaten decode edilmiş halde
    // döner. EncodedContent'e sadece Content boşsa (beklenmedik/farklı bir durum
    // için savunma amaçlı) düşüyoruz.
    if (!string.IsNullOrEmpty(file.Content))
      return file.Content;

    if (!string.IsNullOrEmpty(file.EncodedContent))
      return Encoding.UTF8.GetString(Convert.FromBase64String(file.EncodedContent));

    return null;
  }
}