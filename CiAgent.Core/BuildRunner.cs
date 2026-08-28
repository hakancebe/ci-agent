using System.Diagnostics;
using System.Text;

namespace CiAgent.Core;

public sealed record VerificationResult(bool Succeeded, string Output)
{
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
