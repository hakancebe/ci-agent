using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CiAgent.Core;

/// <summary>
/// PR dalını geçici bir dizine klonlar.
///
/// Bu sınıf, GitHub Actions'taki `actions/checkout` adımının yerini alıyor.
/// Actions modelinde runner'a kod hazır geliyordu; bulutta koşan bir container'da
/// böyle bir sihir yok — repoyu kendimiz çekmek zorundayız.
///
/// Klonlama SIĞ (--depth 1): agent yalnızca dalın ucuna bir commit ekliyor, geçmişe
/// hiç bakmıyor. Tam geçmiş çekmek büyük repolarda dakikalar sürerdi ve tek
/// kazancı kullanılmayan veri olurdu.
/// </summary>
public sealed class GitCloner
{
    private readonly ILogger _log;

    public GitCloner(ILogger? logger = null) => _log = logger ?? NullLogger.Instance;

    /// <summary>
    /// Verilen dalı hedef dizine klonlar. Dizin varsa önce silinir — yarım kalmış
    /// bir önceki çalışmanın artığı üstüne klonlamaktan iyidir.
    /// </summary>
    /// <param name="token">
    /// Installation token. Remote URL'ine gömülüyor ki sonraki `git push` ek bir
    /// kimlik ayarı gerektirmesin (GitWorkspace'i değiştirmeden çalışsın).
    ///
    /// Bunun bedeli: token, klonun .git/config dosyasına yazılıyor. Kabul edilebilir
    /// çünkü (a) container tek kullanımlık ve iş bitince ölüyor, (b) token 1 saat
    /// ömürlü, (c) git çıktısı loglanmadan önce Masker'dan geçiyor. Yine de dizin
    /// iş sonunda siliniyor - bkz. Cleanup.
    /// </param>
    public Task<bool> CloneAsync(
        string owner, string repo, string branch, string token, string targetDirectory)
    {
        _log.LogInformation("Klonlanıyor: {Owner}/{Repo} dalı {Branch} → {Dir}",
            owner, repo, branch, targetDirectory);

        return CloneFromUrlAsync(BuildAuthenticatedUrl(owner, repo, token), branch, targetDirectory);
    }

    /// <summary>
    /// Token'lı klonlama URL'i. Ayrı ve saf bir metod olmasının sebebi test:
    /// URL'in şekli (x-access-token kullanıcı adı, .git uzantısı) yanlış olursa
    /// klonlama sessizce kimlik doğrulamasız denenir ve private repo'da 404 alır —
    /// teşhisi zor bir hata. Burada sabitleniyor.
    /// </summary>
    internal static string BuildAuthenticatedUrl(string owner, string repo, string token)
        // x-access-token, GitHub App installation token'ları için beklenen
        // kullanıcı adı; parola alanına token'ın kendisi geliyor.
        => $"https://x-access-token:{token}@github.com/{owner}/{repo}.git";

    /// <summary>
    /// Asıl klonlama. URL'den bağımsız olduğu için testlerde yerel bir depoya
    /// (file yolu) yönlendirilebiliyor — git komutunun doğru kurulduğu ve sığ
    /// klonun gerçekten sığ olduğu ağa çıkmadan doğrulanabiliyor.
    /// </summary>
    internal async Task<bool> CloneFromUrlAsync(string url, string branch, string targetDirectory)
    {
        if (Directory.Exists(targetDirectory))
        {
            // Yarım kalmış bir önceki çalışmanın artığının üstüne klonlamak yerine
            // temiz sayfa açıyoruz; git zaten dolu bir dizine klonlamayı reddederdi.
            _log.LogInformation("Var olan çalışma dizini siliniyor: {Dir}", targetDirectory);
            Cleanup(targetDirectory);
        }

        var parent = Path.GetDirectoryName(targetDirectory.TrimEnd(Path.DirectorySeparatorChar));
        if (!string.IsNullOrEmpty(parent))
            Directory.CreateDirectory(parent);

        var result = await RunGitAsync(
            workingDirectory: parent ?? ".",
            "clone", "--depth", "1", "--branch", branch, "--single-branch",
            url, targetDirectory);

        if (!result.Ok)
        {
            // Çıktı Masker'dan geçiyor: git, başarısız bir klonda remote URL'ini
            // hata mesajına basıyor ve o URL'in içinde token var.
            _log.LogError("git clone başarısız: {Output}", Masker.Mask(result.Output));
            return false;
        }

        _log.LogInformation("Klonlama tamamlandı.");
        return true;
    }

    /// <summary>
    /// Çalışma dizinini siler. İş bitince çağrılmalı: içinde token'lı bir
    /// .git/config ve üçüncü tarafın kodu var, ikisi de container'da gereksiz
    /// yere durmamalı.
    ///
    /// Silme hatası yutuluyor — temizlik yapılamadı diye zaten tamamlanmış bir
    /// düzeltmeyi başarısız saymak yanlış olurdu.
    /// </summary>
    public void Cleanup(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Çalışma dizini silinemedi: {Dir}", directory);
        }
    }

    private static async Task<(bool Ok, string Output)> RunGitAsync(
        string workingDirectory, params string[] arguments)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "git",
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            }
        };

        // ArgumentList: token içeren URL'i tek bir komut satırına birleştirmek
        // hem kabuk enjeksiyonuna hem de kaçış hatalarına açık olurdu.
        foreach (var arg in arguments)
            process.StartInfo.ArgumentList.Add(arg);

        // Git'in kimlik sorması ölümcül: container'da kimse cevap veremez, süreç
        // sonsuza kadar beklerdi. Bu iki değişken "sorma, hemen başarısız ol" diyor.
        process.StartInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
        process.StartInfo.Environment["GCM_INTERACTIVE"] = "never";

        var output = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (output) output.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (output) output.AppendLine(e.Data); };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        await process.WaitForExitAsync();

        lock (output)
            return (process.ExitCode == 0, output.ToString().Trim());
    }
}
