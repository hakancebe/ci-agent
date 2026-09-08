using System.Diagnostics;
using System.Text;
using System.Text.Json;

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
    /// <param name="workspaceRoot">Klonlanmış deponun kökü.</param>
    /// <param name="editedPaths">
    /// Bu turda DEĞİŞTİRİLEN dosyalar, depo köküne göre. Doğrulanacak ekosistem
    /// buradan çıkıyor — deponun genel türünden değil. Çok dilli bir repoda ikisi
    /// farklı olabilir ve fark sessiz bir "doğrulandı" yalanı üretir.
    /// </param>
    Task<VerificationResult> VerifyAsync(
        string workspaceRoot, IReadOnlyCollection<string> editedPaths);
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

    public ProjectVerificationRunner(TimeSpan? timeout = null)
        => _timeout = timeout ?? TimeSpan.FromMinutes(10);

    /// <summary>
    /// Değiştirilen dosyaları ekosistem + proje klasörüne göre gruplayıp her grubu
    /// kendi araçlarıyla doğrular. HEPSİ geçmeden sonuç başarılı sayılmıyor.
    ///
    /// Neden gruplama: bir /fix turu birden fazla dile dokunabilir. Tek bir
    /// ekosistem seçip onu çalıştırmak, dokunulan diğer dilin testlerini hiç
    /// çalıştırmadan "doğrulandı" demek olurdu.
    /// </summary>
    public async Task<VerificationResult> VerifyAsync(
        string workspaceRoot, IReadOnlyCollection<string> editedPaths)
    {
        if (editedPaths.Count == 0)
            return VerificationResult.NotAttempted("Doğrulanacak bir değişiklik yok.");

        var groups = editedPaths
            .Select(path => (Path: path, Ecosystem: EcosystemDetector.EcosystemOf(path)))
            .Where(x => x.Ecosystem != ProjectEcosystem.Unknown)
            .Select(x => (
                x.Ecosystem,
                Root: EcosystemDetector.ProjectRootFor(workspaceRoot, x.Path, x.Ecosystem)))
            .Distinct()
            .ToList();

        if (groups.Count == 0)
        {
            return VerificationResult.NotAttempted(
                "Değiştirilen dosyaların hiçbiri doğrulanabilir bir ekosisteme ait değil: "
                + string.Join(", ", editedPaths));
        }

        var combined = new StringBuilder();

        foreach (var (ecosystem, root) in groups)
        {
            var result = await VerifyOneAsync(ecosystem, root);

            combined.AppendLine($"### {ecosystem} — {Path.GetRelativePath(workspaceRoot, root)}");
            combined.AppendLine(result.Output);
            combined.AppendLine();

            // İlk başarısızlıkta duruyoruz: kalanları çalıştırmak zaman harcar ve
            // sonucu değiştirmez, çünkü hepsinin geçmesi gerekiyor.
            if (!result.Attempted)
                return VerificationResult.NotAttempted(combined.ToString());

            if (!result.Succeeded)
                return new VerificationResult(false, combined.ToString());
        }

        return new VerificationResult(true, combined.ToString());
    }

    private async Task<VerificationResult> VerifyOneAsync(ProjectEcosystem ecosystem, string root) =>
        ecosystem switch
        {
            ProjectEcosystem.DotNet => await VerifyDotnetAsync(root),
            ProjectEcosystem.Node => await VerifyNodeAsync(root),
            ProjectEcosystem.Python => await VerifyPythonAsync(root),
            _ => VerificationResult.NotAttempted($"'{ecosystem}' için doğrulama yolu yok.")
        };

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
        // package.json yoksa ya da içinde test script'i tanımlı değilse, Node'un
        // KENDİ test koşucusu doğrudan çağrılıyor. npm'e zorlamak yanlış negatif
        // üretiyordu: pilot repodaki node-app/ klasöründe package.json yok (CI de
        // `node --test node-app/` diyor), o yüzden `npm install` ENOENT ile
        // patlıyor ve doğru bir düzeltme bile "testler geçmedi" sayılıyordu.
        if (!HasNpmTestScript(cwd))
        {
            var direct = await RunAsync("node", "--test", cwd);
            return direct.Attempted
                ? new VerificationResult(direct.Succeeded, "=== node --test ===\n" + direct.Output)
                : direct;
        }

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

    /// <summary>
    /// package.json var VE içinde çalıştırılabilir bir "test" script'i tanımlı mı?
    /// Bozuk ya da okunamayan dosya "yok" sayılıyor — o durumda Node'un yerleşik
    /// koşucusuna düşmek, npm'i patlatmaktan iyi.
    /// </summary>
    private static bool HasNpmTestScript(string cwd)
    {
        var path = Path.Combine(cwd, "package.json");
        if (!File.Exists(path))
            return false;

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));

            return document.RootElement.TryGetProperty("scripts", out var scripts)
                && scripts.TryGetProperty("test", out var test)
                && !string.IsNullOrWhiteSpace(test.GetString());
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return false;
        }
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
