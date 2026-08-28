using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CiAgent.Core;

/// <summary>
/// Çalışma dizinindeki git deposuna commit atar ve push eder.
///
/// Bilerek yapılmayanlar: force-push YOK, rebase YOK, dal değiştirme YOK.
/// Agent yalnızca zaten checkout edilmiş dalın üzerine normal bir commit ekler;
/// insanın yazdığı geçmişi asla yeniden yazamaz.
/// </summary>
public sealed class GitWorkspace
{
    private readonly string _root;
    private readonly ILogger _log;

    public GitWorkspace(string workspaceRoot, ILogger? logger = null)
    {
        _root = workspaceRoot;
        _log = logger ?? NullLogger.Instance;
    }

    /// <summary>
    /// Verilen dosyaları commit'ler ve push eder. Hiçbir şey değişmemişse
    /// (ör. dosya zaten o hâldeydi) boş commit atmaz, false döner.
    /// </summary>
    public async Task<bool> CommitAndPushAsync(
        IEnumerable<string> files, string commitMessage, string branch)
    {
        var paths = files.Distinct().ToList();
        if (paths.Count == 0)
            return false;

        // Yalnızca agent'ın dokunduğu dosyalar ekleniyor - `git add -A` runner'da
        // oluşan build çıktılarını da sürükleyebilirdi.
        foreach (var path in paths)
        {
            var add = await RunGitAsync("add", "--", path);
            if (!add.Ok)
            {
                _log.LogError("git add başarısız ({Path}): {Output}", path, add.Output);
                return false;
            }
        }

        var staged = await RunGitAsync("diff", "--cached", "--quiet");
        if (staged.Ok)
        {
            // exit 0 = staged fark yok.
            _log.LogWarning("Commit atılacak bir değişiklik yok.");
            return false;
        }

        var commit = await RunGitAsync("commit", "-m", commitMessage);
        if (!commit.Ok)
        {
            _log.LogError("git commit başarısız: {Output}", commit.Output);
            return false;
        }

        var push = await RunGitAsync("push", "origin", $"HEAD:{branch}");
        if (!push.Ok)
        {
            _log.LogError("git push başarısız: {Output}", push.Output);
            return false;
        }

        _log.LogInformation("{Count} dosya commit edildi ve {Branch} dalına push edildi.",
            paths.Count, branch);
        return true;
    }

    /// <summary>
    /// Commit'in agent adına görünmesi için kimlik ayarlar. Runner'da global git
    /// kimliği olmadığı için commit bu olmadan patlar.
    /// </summary>
    public async Task ConfigureIdentityAsync(string name, string email)
    {
        await RunGitAsync("config", "user.name", name);
        await RunGitAsync("config", "user.email", email);
    }

    private async Task<(bool Ok, string Output)> RunGitAsync(params string[] arguments)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "git",
                WorkingDirectory = _root,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            }
        };

        // ArgumentList kullanılıyor: tek string'e birleştirmek boşluk/tırnak içeren
        // dosya adlarında kabuk enjeksiyonuna açık olurdu.
        foreach (var arg in arguments)
            process.StartInfo.ArgumentList.Add(arg);

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
