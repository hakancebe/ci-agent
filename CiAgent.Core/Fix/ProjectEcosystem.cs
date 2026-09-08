namespace CiAgent.Core;

/// <summary>
/// /fix'in düzeltebildiği ekosistemler.
///
/// Liste kasıtlı olarak KISA. Bir dilin buraya girmesinin şartı, agent'ın o dilde
/// düzeltmeyi DOĞRULAYABİLMESİ: klonlanan repoda testleri çalıştırıp geçtiğini
/// görmek. Doğrulayamadığımız bir dilde /fix, "LLM ne derse onu commit et"
/// olurdu — projenin baştan reddettiği şey.
///
/// Bu yüzden <see cref="FixPolicy"/>'nin izin verdiği uzantılar ile buradaki
/// liste TEK KAYNAKTAN türüyor (<see cref="ExtensionOf"/>). İkisi ayrışırsa
/// agent doğrulayamadığı bir dosyayı düzenlemeye kalkardı.
/// </summary>
public enum ProjectEcosystem
{
    /// <summary>Tanınan bir proje işareti yok; doğrulama yapılamaz.</summary>
    Unknown,

    /// <summary>*.sln / *.csproj — `dotnet build` + `dotnet test`.</summary>
    DotNet,

    /// <summary>package.json — `npm ci` + `npm test`.</summary>
    Node,

    /// <summary>pytest.ini / pyproject.toml / requirements.txt — `pytest`.</summary>
    Python
}

/// <summary>
/// Klonlanmış bir repoda hangi ekosistemin olduğunu, kökteki işaret dosyalarına
/// bakarak söyler.
///
/// Neden dosya işareti, dil tahmini değil: repoda .py dosyası bulunması onu bir
/// Python PROJESİ yapmaz (bir build script'i olabilir). Testleri çalıştırabilmek
/// için gereken şey proje tanımı — package.json, csproj, pyproject.toml.
/// </summary>
public static class EcosystemDetector
{
    /// <summary>
    /// Bu ekosistemde /fix'in düzenlemesine izin verilen dosya uzantıları.
    /// <see cref="ProjectEcosystem.Unknown"/> için boş — doğrulanamayan yerde
    /// düzenleme de yok.
    /// </summary>
    public static IReadOnlyList<string> ExtensionOf(ProjectEcosystem ecosystem) => ecosystem switch
    {
        ProjectEcosystem.DotNet => [".cs"],
        ProjectEcosystem.Node => [".js", ".mjs", ".cjs", ".jsx", ".ts", ".tsx"],
        ProjectEcosystem.Python => [".py"],
        _ => []
    };

    /// <summary>Tüm desteklenen uzantılar — hangi ekosistem olursa olsun.</summary>
    public static IReadOnlyList<string> AllEditableExtensions { get; } =
        Enum.GetValues<ProjectEcosystem>()
            .SelectMany(ExtensionOf)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>
    /// Bir dosyanın hangi ekosisteme ait olduğu — uzantısından.
    ///
    /// Doğrulamanın çıkış noktası BU, deponun genel türü değil. Fark önemli:
    /// çok dilli bir repoda (pilot repo tam olarak öyle — kökte CiPilot.sln,
    /// yanında py-app/ ve node-app/) depoya bakan bir tespit ".NET" der,
    /// `dotnet test` çalıştırır, o da geçer — Python testi hâlâ kırıkken
    /// "doğrulandı" denmiş olurdu. Doğrulanması gereken şey DEĞİŞİKLİK.
    /// </summary>
    public static ProjectEcosystem EcosystemOf(string filePath)
    {
        foreach (var ecosystem in Enum.GetValues<ProjectEcosystem>())
        {
            if (ecosystem == ProjectEcosystem.Unknown)
                continue;

            if (ExtensionOf(ecosystem).Any(
                    ext => filePath.EndsWith(ext, StringComparison.OrdinalIgnoreCase)))
                return ecosystem;
        }

        return ProjectEcosystem.Unknown;
    }

    /// <summary>
    /// Bu dosyanın testleri hangi dizinde çalıştırılmalı?
    ///
    /// Dosyanın klasöründen başlayıp <paramref name="workspaceRoot"/>'a kadar
    /// yukarı çıkarak o ekosistemin proje işaretini arar. Bulamazsa dosyanın
    /// kendi klasörünü döner — pilot repodaki py-app/ tam olarak bu durum:
    /// proje tanım dosyası yok ama testler o klasörde çalışıyor (CI de
    /// `cd py-app && pytest` diyor).
    ///
    /// Kökten çalıştırmak yanlış olurdu: Python'da import yolları, Node'da
    /// package.json'ın yeri o klasöre göre çözülüyor.
    /// </summary>
    public static string ProjectRootFor(
        string workspaceRoot, string relativeFilePath, ProjectEcosystem ecosystem)
    {
        var markers = MarkersOf(ecosystem);
        if (markers.Count == 0)
            return workspaceRoot;

        var fullRoot = Path.GetFullPath(workspaceRoot);
        var directory = Path.GetDirectoryName(
            Path.GetFullPath(Path.Combine(fullRoot, relativeFilePath)));

        var fileDirectory = directory;

        while (directory is not null && directory.StartsWith(fullRoot, StringComparison.Ordinal))
        {
            if (markers.Any(m => HasAny(directory, topLevelOnly: true, m)))
                return directory;

            if (string.Equals(directory, fullRoot, StringComparison.Ordinal))
                break;

            directory = Path.GetDirectoryName(directory);
        }

        return fileDirectory ?? fullRoot;
    }

    private static IReadOnlyList<string> MarkersOf(ProjectEcosystem ecosystem) => ecosystem switch
    {
        ProjectEcosystem.DotNet => ["*.sln", "*.slnx", "*.csproj"],
        ProjectEcosystem.Node => ["package.json"],
        ProjectEcosystem.Python => ["pytest.ini", "pyproject.toml", "setup.py", "requirements.txt", "tox.ini"],
        _ => []
    };

    /// <summary>
    /// Kökteki işaret dosyalarına bakar. Birden fazla eşleşirse .NET öncelikli.
    ///
    /// Yalnızca ilk seviye ve bir alt seviye taranıyor. Tüm ağacı taramak
    /// node_modules gibi klasörlerde dakikalar sürebilirdi; gerçek projelerde
    /// proje tanımı zaten kökte ya da köke yakın duruyor.
    ///
    /// NOT: /fix bunu KULLANMIYOR — orada doğrulanan şey değişikliğin kendisi
    /// (<see cref="EcosystemOf"/>). Bu metot "depoda genel olarak ne var"
    /// sorusu için duruyor.
    /// </summary>
    public static ProjectEcosystem Detect(string workspaceRoot)
    {
        if (!Directory.Exists(workspaceRoot))
            return ProjectEcosystem.Unknown;

        foreach (var ecosystem in Enum.GetValues<ProjectEcosystem>())
        {
            if (ecosystem == ProjectEcosystem.Unknown)
                continue;

            if (HasAny(workspaceRoot, topLevelOnly: false, MarkersOf(ecosystem).ToArray()))
                return ecosystem;
        }

        return ProjectEcosystem.Unknown;
    }

    private static bool HasAny(string root, bool topLevelOnly, params string[] patterns)
    {
        foreach (var pattern in patterns)
        {
            try
            {
                if (Directory.EnumerateFiles(root, pattern, SearchOption.TopDirectoryOnly).Any())
                    return true;

                if (topLevelOnly)
                    continue;

                foreach (var dir in Directory.EnumerateDirectories(root))
                {
                    if (IsSkippable(dir))
                        continue;

                    if (Directory.EnumerateFiles(dir, pattern, SearchOption.TopDirectoryOnly).Any())
                        return true;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Okunamayan bir klasör tespiti durdurmamalı; diğer desenlere devam.
            }
        }

        return false;
    }

    private static bool IsSkippable(string directory)
    {
        var name = Path.GetFileName(directory);
        return name.StartsWith('.')
            || name is "node_modules" or "bin" or "obj" or "venv" or "vendor";
    }
}
