using CiAgent.Core;

namespace CiAgent.Tests;

/// <summary>
/// Klonlama, Actions'ın `actions/checkout` adımının yerini alan parça — yani
/// bulutta /fix'in ilk adımı. Bozulursa hiçbir düzeltme çalışmaz.
///
/// Bu testler GERÇEKTEN `git` çalıştırıyor ama ağa ÇIKMIYOR: kaynak olarak diskte
/// üretilen yerel bir depo kullanılıyor. Böylece "git komutu doğru mu kuruldu",
/// "dal gerçekten checkout edildi mi", "hata durumunda ne dönüyor" soruları
/// GitHub'a hiç dokunmadan cevaplanıyor.
/// </summary>
public class GitClonerTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"ci-agent-clone-tests-{Guid.NewGuid():N}");

    public GitClonerTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch { /* temizlik hatası testi düşürmemeli */ }
    }

    /// <summary>Belirtilen dalda tek commit'i olan yerel bir git deposu üretir.</summary>
    private string CreateSourceRepo(string branch, string fileName = "README.md")
    {
        var path = Path.Combine(_root, $"source-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);

        Git(path, "init", "--initial-branch", branch);
        Git(path, "config", "user.email", "test@example.com");
        Git(path, "config", "user.name", "Test");
        File.WriteAllText(Path.Combine(path, fileName), "merhaba\n");
        Git(path, "add", "-A");
        Git(path, "commit", "-m", "ilk commit");

        return path;
    }

    private static void Git(string workingDirectory, params string[] args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var p = System.Diagnostics.Process.Start(psi)!;
        p.WaitForExit();

        if (p.ExitCode != 0)
            throw new InvalidOperationException(
                $"test kurulumu için git {string.Join(' ', args)} başarısız: {p.StandardError.ReadToEnd()}");
    }

    /// <summary>
    /// Yerel depoyu GitCloner'a "uzak" gibi gösterir.
    ///
    /// file:// ÖNEKİ ŞART, düz dosya yolu DEĞİL: git, düz yerel yolla klonlarken
    /// --depth'i sessizce yok sayıp hardlink klonu yapıyor. Düz yol kullanan bir
    /// test "sığ klon" iddiasını doğruluyormuş gibi görünüp aslında hiç
    /// doğrulamazdı — file:// git'i gerçek transport yoluna sokuyor.
    /// </summary>
    private static Task<bool> CloneLocalAsync(string sourcePath, string branch, string target)
        => new GitCloner().CloneFromUrlAsync(
            // file:// protokolünde kimlik doğrulama yok, token'a gerek kalmıyor.
            new Uri(Path.GetFullPath(sourcePath)).AbsoluteUri, branch, target);

    [Fact]
    public async Task CloneAsync_ChecksOutRequestedBranch()
    {
        var source = CreateSourceRepo("feature/deneme");
        var target = Path.Combine(_root, "hedef");

        var ok = await CloneLocalAsync(source, "feature/deneme", target);

        Assert.True(ok);
        Assert.True(File.Exists(Path.Combine(target, "README.md")));
        Assert.True(Directory.Exists(Path.Combine(target, ".git")));
    }

    [Fact]
    public async Task CloneAsync_ReturnsFalseForMissingBranch()
    {
        // Var olmayan dal: git patlar, biz de exception fırlatmadan false dönmeliyiz —
        // çağıran taraf bunu PR'a anlamlı bir mesaj olarak yazacak.
        var source = CreateSourceRepo("main");
        var target = Path.Combine(_root, "hedef-yok");

        var ok = await CloneLocalAsync(source, "olmayan-dal", target);

        Assert.False(ok);
    }

    [Fact]
    public async Task CloneAsync_ReplacesExistingDirectory()
    {
        // Aynı container'da ikinci bir /fix çalışırsa ya da önceki çalışma yarıda
        // kaldıysa hedef dizin dolu olabilir; üstüne klonlamak yerine temizlenmeli.
        var source = CreateSourceRepo("main");
        var target = Path.Combine(_root, "dolu-dizin");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "eski-artik.txt"), "önceki çalışmadan kalan");

        var ok = await CloneLocalAsync(source, "main", target);

        Assert.True(ok);
        Assert.False(File.Exists(Path.Combine(target, "eski-artik.txt")));
        Assert.True(File.Exists(Path.Combine(target, "README.md")));
    }

    [Fact]
    public void Cleanup_RemovesDirectory()
    {
        var dir = Path.Combine(_root, "silinecek");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "dosya.txt"), "x");

        new GitCloner().Cleanup(dir);

        Assert.False(Directory.Exists(dir));
    }

    [Fact]
    public void Cleanup_MissingDirectoryIsNotAnError()
    {
        // Klonlama hiç yapılmadıysa temizlik çağrısı yine de gelir (finally bloğu);
        // patlamamalı.
        new GitCloner().Cleanup(Path.Combine(_root, "hic-olmayan"));
    }

    [Fact]
    public async Task CloneAsync_ShallowCloneHasSingleCommit()
    {
        // --depth 1 iddiası: agent geçmişe hiç bakmıyor, sığ klon yeterli.
        // Derinlik kayarsa büyük repolarda /fix belirgin şekilde yavaşlar.
        var source = CreateSourceRepo("main");
        File.WriteAllText(Path.Combine(source, "ikinci.txt"), "ikinci\n");
        Git(source, "add", "-A");
        Git(source, "commit", "-m", "ikinci commit");

        var target = Path.Combine(_root, "sig-klon");
        Assert.True(await CloneLocalAsync(source, "main", target));

        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "git", WorkingDirectory = target,
            RedirectStandardOutput = true, UseShellExecute = false
        };
        psi.ArgumentList.Add("rev-list");
        psi.ArgumentList.Add("--count");
        psi.ArgumentList.Add("HEAD");

        using var p = System.Diagnostics.Process.Start(psi)!;
        var count = (await p.StandardOutput.ReadToEndAsync()).Trim();
        await p.WaitForExitAsync();

        Assert.Equal("1", count);
    }
}
