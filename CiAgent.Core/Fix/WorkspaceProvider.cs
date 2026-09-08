using Microsoft.Extensions.Logging;

namespace CiAgent.Core;

/// <summary>
/// Düzeltilecek kodun nereden geleceğini soyutlar.
///
/// Neden bir arayüz? Agent iki farklı dünyada koşuyor ve kod bu ikisine farklı
/// yollarla ulaşıyor:
///
///   Actions      → runner `actions/checkout` ile kodu zaten indirmiş durumda
///   Container App Job → container boş geliyor, repoyu kendimiz klonluyoruz
///
/// Kritik nokta: hazırlık FixCoordinator'ın İÇİNDEN, yetki ve fork kontrolleri
/// GEÇTİKTEN SONRA tetikleniyor. Klonlamayı çağıran tarafa bıraksaydık, yetkisiz
/// bir yorum için bile repo klonlanırdı — "yetkisiz yoruma tek bir API çağrısı
/// bile harcama" kuralı çöpe giderdi.
/// </summary>
public interface IWorkspaceProvider
{
    /// <summary>
    /// Kodu hazırlar ve çalışma dizininin yolunu döner. Hazırlanamazsa null.
    /// </summary>
    Task<string?> PrepareAsync(string owner, string repo, string branch);

    /// <summary>İş bitince çağrılır. Hazırlık bir şey yaratmadıysa hiçbir şey yapmaz.</summary>
    void Cleanup();
}

/// <summary>
/// Kod zaten diskte: Actions runner'ının checkout ettiği dizin, ya da lokal
/// geliştirmede elle verilen bir yol. Hiçbir şey indirmez, hiçbir şey silmez —
/// silmek burada felaket olurdu, çünkü dizin bize ait değil.
/// </summary>
public sealed class ExistingWorkspaceProvider : IWorkspaceProvider
{
    private readonly string _path;

    public ExistingWorkspaceProvider(string path) => _path = path;

    public Task<string?> PrepareAsync(string owner, string repo, string branch)
        => Task.FromResult<string?>(Directory.Exists(_path) ? _path : null);

    public void Cleanup() { }
}

/// <summary>
/// Repoyu geçici bir dizine klonlar ve iş bitince siler. Container Apps Job'ın
/// kullandığı yol.
/// </summary>
public sealed class CloningWorkspaceProvider : IWorkspaceProvider
{
    private readonly GitCloner _cloner;
    private readonly string _token;
    private readonly string _targetDirectory;
    private bool _cloned;

    public CloningWorkspaceProvider(string token, string targetDirectory, ILogger? logger = null)
    {
        _cloner = new GitCloner(logger);
        _token = token;
        _targetDirectory = targetDirectory;
    }

    public async Task<string?> PrepareAsync(string owner, string repo, string branch)
    {
        _cloned = await _cloner.CloneAsync(owner, repo, branch, _token, _targetDirectory);
        return _cloned ? _targetDirectory : null;
    }

    /// <summary>
    /// Klonu siler. Dizinde hem üçüncü tarafın kodu hem de .git/config içinde
    /// installation token'ı var; container ölmeden önce ikisini de bırakmıyoruz.
    /// </summary>
    public void Cleanup()
    {
        if (_cloned)
            _cloner.Cleanup(_targetDirectory);
    }
}
