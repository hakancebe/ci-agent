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
    /// Kökteki işaret dosyalarına bakar. Birden fazla eşleşirse .NET öncelikli:
    /// agent'ın en çok ölçüldüğü ve doğrulamasının en güvenilir olduğu yol o.
    ///
    /// Yalnızca ilk seviye ve bir alt seviye taranıyor. Tüm ağacı taramak
    /// node_modules gibi klasörlerde dakikalar sürebilirdi; gerçek projelerde
    /// proje tanımı zaten kökte ya da köke yakın duruyor.
    /// </summary>
    public static ProjectEcosystem Detect(string workspaceRoot)
    {
        if (!Directory.Exists(workspaceRoot))
            return ProjectEcosystem.Unknown;

        if (HasAny(workspaceRoot, "*.sln", "*.slnx", "*.csproj"))
            return ProjectEcosystem.DotNet;

        if (HasAny(workspaceRoot, "package.json"))
            return ProjectEcosystem.Node;

        if (HasAny(workspaceRoot, "pytest.ini", "pyproject.toml", "setup.py", "requirements.txt", "tox.ini"))
            return ProjectEcosystem.Python;

        return ProjectEcosystem.Unknown;
    }

    private static bool HasAny(string root, params string[] patterns)
    {
        foreach (var pattern in patterns)
        {
            try
            {
                if (Directory.EnumerateFiles(root, pattern, SearchOption.TopDirectoryOnly).Any())
                    return true;

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
