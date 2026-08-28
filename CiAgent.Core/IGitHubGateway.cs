using Octokit;

namespace CiAgent.Core;

/// <summary>
/// Pipeline'ın GitHub'dan ihtiyaç duyduğu her şey. <see cref="GitHubService"/>
/// bunu implemente ediyor; arayüz olmasının tek sebebi CiAnalysisPipeline'ın
/// ağa hiç çıkmadan test edilebilmesi — GitHubService'in kendi içindeki
/// HttpClient (DownloadJobLogAsync) mock'lanamıyor.
/// </summary>
public interface IGitHubGateway
{
    Task<IReadOnlyList<WorkflowJob>> GetJobsAsync(string owner, string repo, long runId);

    Task<IReadOnlyList<CheckRunAnnotation>> GetAnnotationsAsync(string owner, string repo, long jobId);

    Task<string> DownloadJobLogAsync(string owner, string repo, long jobId);

    /// <summary>Dosya bulunamazsa null döner; ağ/izin hataları yukarı fırlar.</summary>
    Task<string?> GetFileContentAsync(string owner, string repo, string path, string ref_);
}
