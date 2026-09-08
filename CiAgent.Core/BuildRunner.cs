using System.Diagnostics;
using System.Text;

namespace CiAgent.Core;

public sealed record VerificationResult(bool Succeeded, string Output, bool Attempted = true)
{
    /// <summary>
    /// Doğrulama HİÇ çalıştırılamadı (tanınan bir proje yok, ya da o ekosistemin
    /// aracı bu image'da kurulu değil). "Çalıştı ve kaldı"dan ayrı tutuluyor,
    /// çünkü kullanıcıya söylenecek şey tamamen farklı: birinde düzeltme yanlış,
    /// diğerinde düzeltmenin doğru olup olmadığını BİLMİYORUZ.
    ///
    /// Bilinmeyen durumda değişiklik yine de geri alınıyor. Doğrulanmamış bir
    /// düzeltmeyi commit'lemek, /fix'in var oluş sebebini ortadan kaldırırdı.
    /// </summary>
    public static VerificationResult NotAttempted(string reason) => new(false, reason, Attempted: false);

    /// <summary>
    /// LLM'e geri beslenecek kısaltılmış çıktı. Tam build+test logu on binlerce
    /// karakter olabiliyor; asıl bilgi sondaki hata satırlarında.
    /// </summary>
    public string Tail(int maxChars = 4_000) =>
        Output.Length <= maxChars ? Output : "…\n" + Output[^maxChars..];
}

/// <summary>
/// Düzeltmenin gerçekten işe yarayıp yaramadığını söyleyen katman. Bu olmadan
/// /fix "LLM ne derse onu commit et" olurdu.
/// </summary>
public interface IVerificationRunner
{
    Task<VerificationResult> VerifyAsync(string workingDirectory);
}

/// <summary>
/// `dotnet build` + `dotnet test` çalıştırır. Build patlarsa teste hiç geçmez -
/// derlenmeyen kodun test çıktısı zaten yanıltıcı olur.
/// </summary>
public sealed class DotnetVerificationRunner : IVerificationRunner
{
    private readonly TimeSpan _timeout;

    public DotnetVerificationRunner(TimeSpan? timeout = null)
        => _timeout = timeout ?? TimeSpan.FromMinutes(10);

    public async Task<VerificationResult> VerifyAsync(string workingDirectory)
    {
        var build = await RunAsync("build --nologo", workingDirectory);
        if (!build.Succeeded)
            return new VerificationResult(false, "=== dotnet build ===\n" + build.Output);

        var test = await RunAsync("test --nologo", workingDirectory);
        return new VerificationResult(
            test.Succeeded,
            "=== dotnet build ===\nBaşarılı.\n\n=== dotnet test ===\n" + test.Output);
    }

    private async Task<VerificationResult> RunAsync(string arguments, string workingDirectory)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = arguments,
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            }
        };

        var output = new StringBuilder();
        // Çıktı olay bazlı toplanıyor: senkron okuma, boru dolduğunda süreci
        // kilitler (build logları bunu rahatlıkla aşıyor).
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (output) output.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (output) output.AppendLine(e.Data); };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var cts = new CancellationTokenSource(_timeout);
        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            lock (output) output.AppendLine($"[{_timeout.TotalMinutes:0} dakika zaman aşımı, süreç sonlandırıldı]");
            return new VerificationResult(false, output.ToString());
        }

        lock (output)
            return new VerificationResult(process.ExitCode == 0, output.ToString());
    }

    private static void TryKill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch { /* süreç zaten bitmiş olabilir; zaman aşımını maskelememek için yutuluyor */ }
    }
}

/// <summary>
/// Klonlanan repoyu tanıyıp o ekosistemin doğrulama komutlarını çalıştırır.
///
/// Neden tek bir runner değil de seçim: /fix'in tek gerçek güvencesi
/// "değişiklikten SONRA testler geçiyor" cümlesi. O cümleyi kurabilmek için
/// testleri her ekosistemin kendi aracıyla çalıştırmak gerekiyor —
/// `dotnet test`, `npm test`, `pytest`. Ortak bir komut yok.
///
/// Tanınmayan bir proje ya da kurulu olmayan bir araç sessizce "başarılı"
/// sayılmıyor; <see cref="VerificationResult.NotAttempted"/> dönüyor ve
/// değişiklik geri alınıyor.
/// </summary>
public sealed class ProjectVerificationRunner : IVerificationRunner
{
    private readonly TimeSpan _timeout;
    private readonly Func<string, ProjectEcosystem> _detect;

    public ProjectVerificationRunner(
        TimeSpan? timeout = null, Func<string, ProjectEcosystem>? detect = null)
    {
        _timeout = timeout ?? TimeSpan.FromMinutes(10);
        _detect = detect ?? EcosystemDetector.Detect;
    }

    public async Task<VerificationResult> VerifyAsync(string workingDirectory)
    {
        var ecosystem = _detect(workingDirectory);

        return ecosystem switch
        {
            ProjectEcosystem.DotNet => await VerifyDotnetAsync(workingDirectory),
            ProjectEcosystem.Node => await VerifyNodeAsync(workingDirectory),
            ProjectEcosystem.Python => await VerifyPythonAsync(workingDirectory),
            _ => VerificationResult.NotAttempted(
                "Klonlanan depoda tanınan bir proje tanımı bulunamadı "
                + "(*.sln, *.csproj, package.json, pyproject.toml, requirements.txt).")
        };
    }

    private async Task<VerificationResult> VerifyDotnetAsync(string cwd)
    {
        // Build patlarsa teste hiç geçilmiyor: derlenmeyen kodun test çıktısı
        // zaten yanıltıcı olur.
        var build = await RunAsync("dotnet", "build --nologo", cwd);
        if (!build.Attempted)
            return build;

        if (!build.Succeeded)
            return new VerificationResult(false, "=== dotnet build ===\n" + build.Output);

        var test = await RunAsync("dotnet", "test --nologo", cwd);
        return test.Attempted
            ? new VerificationResult(test.Succeeded,
                "=== dotnet build ===\nBaşarılı.\n\n=== dotnet test ===\n" + test.Output)
            : test;
    }

    private async Task<VerificationResult> VerifyNodeAsync(string cwd)
    {
        // `npm ci` kilit dosyası ister; yoksa `npm install`. Bağımlılığı olmayan
        // küçük projelerde ikisi de gereksiz ama zararsız ve hızlı.
        var installCommand = File.Exists(Path.Combine(cwd, "package-lock.json")) ? "ci" : "install";

        var install = await RunAsync("npm", $"{installCommand} --no-audit --no-fund", cwd);
        if (!install.Attempted)
            return install;

        if (!install.Succeeded)
            return new VerificationResult(false, $"=== npm {installCommand} ===\n" + install.Output);

        var test = await RunAsync("npm", "test", cwd);
        return test.Attempted
            ? new VerificationResult(test.Succeeded,
                $"=== npm {installCommand} ===\nBaşarılı.\n\n=== npm test ===\n" + test.Output)
            : test;
    }

    private async Task<VerificationResult> VerifyPythonAsync(string cwd)
    {
        // requirements.txt varsa kuruluyor; yoksa atlanıyor. Kurulum başarısız
        // olsa bile testler yine de denenmiyor — eksik bağımlılıkla çıkan test
        // hatası düzeltmeyle ilgisiz olurdu ve modeli yanlış yöne sürerdi.
        if (File.Exists(Path.Combine(cwd, "requirements.txt")))
        {
            // --break-system-packages şart: Debian bookworm sistem Python'unu
            // "externally managed" işaretliyor (PEP 668) ve pip yazmayı reddediyor.
            // Container tek kullanımlık, dolayısıyla sistem paketlerini kirletme
            // endişesi burada geçerli değil.
            var install = await RunAsync(
                "pip3", "install --quiet --break-system-packages -r requirements.txt", cwd);
            if (!install.Attempted)
                return install;

            if (!install.Succeeded)
                return new VerificationResult(false, "=== pip install ===\n" + install.Output);
        }

        var test = await RunAsync("python3", "-m pytest -q", cwd);
        return test.Attempted
            ? new VerificationResult(test.Succeeded, "=== pytest ===\n" + test.Output)
            : test;
    }

    private async Task<VerificationResult> RunAsync(string fileName, string arguments, string cwd)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                WorkingDirectory = cwd,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            }
        };

        var output = new StringBuilder();
        // Çıktı olay bazlı toplanıyor: senkron okuma, boru dolduğunda süreci
        // kilitler (build logları bunu rahatlıkla aşıyor).
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (output) output.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (output) output.AppendLine(e.Data); };

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            // Araç bu image'da kurulu değil. "Test patladı" DEĞİL — bilinmiyor.
            return VerificationResult.NotAttempted(
                $"'{fileName}' çalıştırılamadı ({ex.Message}). Bu ekosistemin aracı "
                + "agent container'ında kurulu değil, düzeltme doğrulanamıyor.");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var cts = new CancellationTokenSource(_timeout);
        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            lock (output) output.AppendLine($"[{_timeout.TotalMinutes:0} dakika zaman aşımı, süreç sonlandırıldı]");
            return new VerificationResult(false, output.ToString());
        }

        lock (output)
            return new VerificationResult(process.ExitCode == 0, output.ToString());
    }

    private static void TryKill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch { /* süreç zaten bitmiş olabilir; zaman aşımını maskelememek için yutuluyor */ }
    }
}
