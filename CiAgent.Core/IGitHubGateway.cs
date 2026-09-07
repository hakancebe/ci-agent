using Octokit;

namespace CiAgent.Core;

/// <summary>
/// Bir run'ın hangi workflow'dan geldiği: görünen ad ("CD") ve repodaki dosya
/// yolu (".github/workflows/cd.yml").
///
/// Octokit'in <c>WorkflowRun</c> tipi yerine bu dar kayıt kullanılıyor: testlerde
/// sahtesini kurmak için 20 argümanlık bir constructor doldurmak gerekmesin.
/// </summary>
public sealed record WorkflowInfo(string? Name, string? Path);

/// <summary>
/// Pipeline'ın GitHub'dan ihtiyaç duyduğu her şey. <see cref="GitHubService"/>
/// bunu implemente ediyor; arayüz olmasının tek sebebi CiAnalysisPipeline'ın
/// ağa hiç çıkmadan test edilebilmesi — GitHubService'in kendi içindeki
/// HttpClient (DownloadJobLogAsync) mock'lanamıyor.
/// </summary>
public interface IGitHubGateway
{
    Task<IReadOnlyList<WorkflowJob>> GetJobsAsync(string owner, string repo, long runId);

    /// <summary>
    /// Run'ın workflow adı ve dosya yolu. Bulunamazsa/çağrı başarısızsa null —
    /// analiz bu bilgi olmadan da yapılabilmeli.
    /// </summary>
    Task<WorkflowInfo?> GetWorkflowInfoAsync(string owner, string repo, long runId);

    Task<IReadOnlyList<CheckRunAnnotation>> GetAnnotationsAsync(string owner, string repo, long jobId);

    Task<string> DownloadJobLogAsync(string owner, string repo, long jobId);

    /// <summary>Dosya bulunamazsa null döner; ağ/izin hataları yukarı fırlar.</summary>
    Task<string?> GetFileContentAsync(string owner, string repo, string path, string ref_);

    /// <summary>
    /// Repodaki tüm dosya yolları (tek recursive çağrı). Patlayan bir testin test
    /// ettiği uygulama dosyasını ADINDAN bulabilmek için gerekli — o dosyanın yolu
    /// hata mesajında geçmiyor. Repo bulunamazsa boş liste.
    /// </summary>
    Task<IReadOnlyList<string>> ListFilePathsAsync(string owner, string repo, string ref_);
}
