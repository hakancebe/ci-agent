using CiAgent.Core;

namespace CiAgent.Tests;

/// <summary>
/// /fix'in .NET dışına açılmasının temel taşı. Bu tespit yanlış olursa agent
/// yanlış aracı çalıştırır, doğrulama anlamsız çıktı üretir ve "düzeltildi"
/// kararı dayanaksız kalır.
/// </summary>
public class EcosystemDetectorTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "ciagent-eco-" + Guid.NewGuid().ToString("N"));

    public EcosystemDetectorTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* temizlik best-effort */ }
    }

    private string Touch(string relativePath)
    {
        var full = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, "");
        return full;
    }

    [Theory]
    [InlineData("App.sln", ProjectEcosystem.DotNet)]
    [InlineData("App.slnx", ProjectEcosystem.DotNet)]
    [InlineData("Core.csproj", ProjectEcosystem.DotNet)]
    [InlineData("package.json", ProjectEcosystem.Node)]
    [InlineData("pyproject.toml", ProjectEcosystem.Python)]
    [InlineData("requirements.txt", ProjectEcosystem.Python)]
    [InlineData("pytest.ini", ProjectEcosystem.Python)]
    [InlineData("setup.py", ProjectEcosystem.Python)]
    public void Detect_RecognizesProjectMarkers(string marker, ProjectEcosystem expected)
    {
        Touch(marker);

        Assert.Equal(expected, EcosystemDetector.Detect(_root));
    }

    [Fact]
    public void Detect_FindsMarkersOneLevelDown()
    {
        // Gerçek repolarda proje tanımı sık sık bir alt klasörde (src/, app/).
        Touch("src/Core.csproj");

        Assert.Equal(ProjectEcosystem.DotNet, EcosystemDetector.Detect(_root));
    }

    [Fact]
    public void Detect_ReturnsUnknown_WhenNoProjectMarkerExists()
    {
        // Sadece kaynak dosyası bulunması proje YAPMAZ: bir build script'i de
        // .py uzantılı olabilir. Testleri çalıştırmak için proje tanımı gerekiyor.
        Touch("scripts/deploy.py");
        Touch("README.md");

        Assert.Equal(ProjectEcosystem.Unknown, EcosystemDetector.Detect(_root));
    }

    [Fact]
    public void Detect_PrefersDotNet_WhenARepoHoldsSeveralEcosystems()
    {
        // Pilot repo tam olarak böyle: .NET + Node + Python bir arada. .NET
        // öncelikli çünkü doğrulaması en çok ölçülen ve en güvenilir yol.
        Touch("App.slnx");
        Touch("node-app/package.json");
        Touch("py-app/pyproject.toml");

        Assert.Equal(ProjectEcosystem.DotNet, EcosystemDetector.Detect(_root));
    }

    [Fact]
    public void Detect_IgnoresVendorDirectories()
    {
        // Bir bağımlılığın kendi package.json'ı repoyu Node projesi yapmaz.
        Touch("node_modules/some-pkg/package.json");

        Assert.Equal(ProjectEcosystem.Unknown, EcosystemDetector.Detect(_root));
    }

    [Fact]
    public void Detect_ReturnsUnknown_WhenTheDirectoryDoesNotExist()
    {
        Assert.Equal(ProjectEcosystem.Unknown,
            EcosystemDetector.Detect(Path.Combine(_root, "yok")));
    }

    // --- Uzantı listesi ile araç listesi aynı kaynaktan --------------------

    [Fact]
    public void EveryEditableExtension_BelongsToAVerifiableEcosystem()
    {
        // Bu testin koruduğu değişmez: /fix'in düzenleyebildiği her dosya türü
        // için agent'ın çalıştırabileceği bir doğrulama yolu OLMALI. Ayrışırsa
        // doğrulanmamış düzeltme commit'lenebilir hale gelir.
        foreach (var extension in EcosystemDetector.AllEditableExtensions)
        {
            var owner = Enum.GetValues<ProjectEcosystem>()
                .Where(e => e != ProjectEcosystem.Unknown)
                .Any(e => EcosystemDetector.ExtensionOf(e).Contains(extension));

            Assert.True(owner, $"'{extension}' hiçbir doğrulanabilir ekosisteme ait değil.");
        }
    }

    [Fact]
    public void UnknownEcosystem_AllowsNoEditing()
    {
        Assert.Empty(EcosystemDetector.ExtensionOf(ProjectEcosystem.Unknown));
    }
}
