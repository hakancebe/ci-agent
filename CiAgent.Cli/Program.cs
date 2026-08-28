using CiAgent.Cli;
using CiAgent.Core;
using Microsoft.Extensions.Configuration;

// Bu dosyanın tek işi: yapılandırmayı bağlamak, hedefi çözmek ve pipeline'ın
// sonucunu exit code'a çevirmek. Analiz akışının kendisi CiAnalysisPipeline'da
// (CiAgent.Core) — orada test edilebiliyor.

// --- Bayraklar ve konumsal argümanlar ayrıştırılıyor ---
// Bayraklar konumsal argümanlardan ayrılmalı, yoksa `dotnet run -- --dry-run` çağrısında
// "--dry-run" owner sanılır. Bilinmeyen bayrakta HATA veriyoruz: "--dryrun" gibi bir
// yazım hatası sessizce GERÇEK bir çalıştırmaya (ve PR'a yorum atmaya) dönüşmemeli.
var knownFlags = new[] { "--dry-run", "--help", "-h" };
var flags = args.Where(a => a.StartsWith('-')).ToList();
var positional = args.Where(a => !a.StartsWith('-')).ToArray();

var unknownFlags = flags.Where(f => !knownFlags.Contains(f, StringComparer.OrdinalIgnoreCase)).ToList();
if (unknownFlags.Count > 0)
{
    Console.Error.WriteLine($"HATA: Bilinmeyen bayrak: {string.Join(", ", unknownFlags)}");
    Console.Error.WriteLine($"Kullanılabilir bayraklar: {string.Join(", ", knownFlags)}");
    return 1;
}

if (flags.Any(f => f is "--help" or "-h"))
{
    Console.WriteLine("""
        Kullanım: ci-agent [owner] [repo] [runId] [--dry-run]

          owner/repo/runId  Hedef. Verilmezse CI_AGENT_OWNER / CI_AGENT_REPO /
                            CI_AGENT_RUN_ID env var'larına, lokalde varsayılanlara düşer.
                            CI'da (GITHUB_ACTIONS=true) üçü de zorunludur.

          --dry-run         Analizi yapar ama GitHub'a HİÇBİR ŞEY yazmaz; yazılacak olan
                            yorumu konsola basar. Azure OpenAI çağrısı yine de yapılır.
                            CI_AGENT_DRY_RUN=true ile de açılabilir.
        """);
    return 0;
}

// Bayrak ya da env var — hangisi verilirse dry-run açılır.
var dryRun = flags.Contains("--dry-run", StringComparer.OrdinalIgnoreCase)
    || string.Equals(Environment.GetEnvironmentVariable("CI_AGENT_DRY_RUN"), "true",
                     StringComparison.OrdinalIgnoreCase);

// Yapılandırma zinciri:
// 1. Önce Environment Variable'lara bakar
// 2. Ardından User Secrets'a bakar (yerelde varsa üzerine yazar)
var config = new ConfigurationBuilder()
    .AddEnvironmentVariables()
    .AddUserSecrets<Program>()
    .Build();

// --- Secret'lar: Öncelik sırasıyla okunur ---
var githubToken = config["GITHUB_TOKEN"] ?? config["GitHub:Token"];
var azureEndpoint = config["AZURE_OPENAI_ENDPOINT"] ?? config["AzureOpenAI:Endpoint"];
var azureKey = config["AZURE_OPENAI_KEY"] ?? config["AzureOpenAI:ApiKey"];
var azureDeployment = config["AZURE_OPENAI_DEPLOYMENT"] ?? config["AzureOpenAI:DeploymentName"];

var missing = new List<string>();
if (string.IsNullOrWhiteSpace(githubToken)) missing.Add("GITHUB_TOKEN");
if (string.IsNullOrWhiteSpace(azureEndpoint)) missing.Add("AZURE_OPENAI_ENDPOINT");
if (string.IsNullOrWhiteSpace(azureKey)) missing.Add("AZURE_OPENAI_KEY");
if (string.IsNullOrWhiteSpace(azureDeployment)) missing.Add("AZURE_OPENAI_DEPLOYMENT");

if (missing.Count > 0)
{
    Console.Error.WriteLine($"HATA: Şu env var'lar eksik: {string.Join(", ", missing)}");
    Console.Error.WriteLine(
        "GITHUB_TOKEN GitHub API çağrıları içindir, AZURE_OPENAI_ENDPOINT / AZURE_OPENAI_KEY / "
        + "AZURE_OPENAI_DEPLOYMENT ise Azure OpenAI için ayrı secret'lardır.");
    return 1;
}

// --- Mod seçimi ---
// İki giriş noktası var: varsayılan "analiz" (CI patlayınca çalışır) ve
// "fix" (PR'a /fix yorumu yazılınca çalışır). Fix modu kendi hedefini
// yorumun payload'ından aldığı için aşağıdaki owner/repo/runId çözümlemesine
// girmiyor — başarısız run'ı kendisi buluyor.
if (string.Equals(Environment.GetEnvironmentVariable("CI_AGENT_MODE"), "fix",
                  StringComparison.OrdinalIgnoreCase))
{
    return await FixMode.RunAsync(githubToken!, azureEndpoint!, azureKey!, azureDeployment!);
}

// --- Hedef owner/repo/run ID ---
// Öncelik sırası: komut satırı argümanı > env var > varsayılan.
// Lokal test için `dotnet run -- owner repo runId` aynen çalışır; CI'da workflow
// bu değerleri workflow_run payload'ından env var olarak besler.
//
// CI'da (GITHUB_ACTIONS=true) varsayılana düşmek TEHLİKELİ: hedef belirtilmemişse
// agent sessizce BAŞKA bir repoyu/run'ı analiz edip yanlış PR'a yorum atabilir.
// Bu yüzden CI'da üçü de zorunlu; varsayılanlar yalnızca lokal deneme kolaylığı.
var isCi = string.Equals(
    Environment.GetEnvironmentVariable("GITHUB_ACTIONS"), "true", StringComparison.OrdinalIgnoreCase);

var missingTargets = new List<string>();
var usedFallback = false;

string Resolve(int index, string envName, string fallback)
{
    if (positional.Length > index && !string.IsNullOrWhiteSpace(positional[index]))
        return positional[index];

    var fromEnv = Environment.GetEnvironmentVariable(envName);
    if (!string.IsNullOrWhiteSpace(fromEnv))
        return fromEnv;

    if (isCi)
    {
        missingTargets.Add(envName);
        return "";
    }

    usedFallback = true;
    return fallback;
}

var owner = Resolve(0, "CI_AGENT_OWNER", "hakancebe");
var repo = Resolve(1, "CI_AGENT_REPO", "ci-agent-pilot");
var runIdRaw = Resolve(2, "CI_AGENT_RUN_ID", "32977225843");

// Lokalde hedef verilmediyse varsayılanlara düşüyoruz — ama User Secrets dolu olduğu
// için bu SESSİZCE gerçek bir çalıştırmaya, yani başka bir repoya yorum atmaya dönüşebilir.
// Bu yüzden ne olacağını açıkça yazıyoruz ve --dry-run'ı hatırlatıyoruz.
if (usedFallback && !dryRun)
{
    Console.WriteLine();
    Console.WriteLine($"!!! DİKKAT: Hedef verilmedi, varsayılan kullanılıyor: {owner}/{repo} run {runIdRaw}");
    Console.WriteLine("!!! Bu GERÇEK bir çalıştırma: analiz sonucu o repoya yorum olarak yazılacak.");
    Console.WriteLine("!!! Sadece denemek istiyorsanız --dry-run ekleyin.");
    Console.WriteLine();
}

if (missingTargets.Count > 0)
{
    Console.Error.WriteLine(
        $"HATA: CI ortamında şu değerler zorunlu ama verilmedi: {string.Join(", ", missingTargets)}.");
    Console.Error.WriteLine("Workflow bunları workflow_run payload'ından env var olarak beslemeli.");
    return 1;
}

if (!long.TryParse(runIdRaw, out var runId))
{
    // CI'da sessizce eski bir run'ı analiz etmektense hemen patlamak daha doğru.
    Console.Error.WriteLine($"HATA: Geçersiz run ID: '{runIdRaw}'. Sayısal bir değer bekleniyor.");
    return 1;
}

// --- Bağımlılıkları kur ve çalıştır ---
var github = new GitHubService(githubToken!);
var llm = new LlmService(azureEndpoint!, azureKey!, azureDeployment!);
var report = new ReportService(github.Client);

var pipeline = new CiAnalysisPipeline(github, llm, report, ConsoleLogger.Create<CiAnalysisPipeline>());

await pipeline.RunAsync(owner, repo, runId, dryRun);

// Her PipelineStatus için exit 0: hiç başarısız job olmaması ya da analiz edilebilir
// hata bulunamaması agent'ın hatası değil, "yapacak iş yoktu" demek. Agent'ın kendi
// hatası zaten yukarıdaki yapılandırma kontrollerinde ya da exception olarak çıkıyor.
return 0;
