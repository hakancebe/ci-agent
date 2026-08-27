using CiAgent.Core;
using Microsoft.Extensions.Configuration;

// Yapılandırma zincirini oluştur: 
// 1. Önce Environment Variable'ları bakar
// 2. Ardından User Secrets bakar (yerelde varsa üzerine yazar)
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
    Console.WriteLine($"HATA: Şu env var'lar eksik: {string.Join(", ", missing)}");
    Console.WriteLine("GITHUB_TOKEN GitHub API çağrıları içindir, AZURE_OPENAI_ENDPOINT / AZURE_OPENAI_KEY / AZURE_OPENAI_DEPLOYMENT ise Azure OpenAI için ayrı secret'lardır.");
    Environment.Exit(1);
    return;
}

// --- Hedef owner/repo/run ID ---
// Öncelik sırası: komut satırı argümanı > env var > varsayılan.
// Lokal test için `dotnet run -- owner repo runId` aynen çalışmaya devam eder;
// CI'da workflow bu değerleri workflow_run payload'ından env var olarak besler.
string Resolve(int index, string envName, string fallback)
{
    if (args.Length > index && !string.IsNullOrWhiteSpace(args[index]))
        return args[index];

    var fromEnv = Environment.GetEnvironmentVariable(envName);
    return string.IsNullOrWhiteSpace(fromEnv) ? fallback : fromEnv;
}

var owner = Resolve(0, "CI_AGENT_OWNER", "hakancebe");
var repo = Resolve(1, "CI_AGENT_REPO", "ci-agent-pilot");
var runIdRaw = Resolve(2, "CI_AGENT_RUN_ID", "32977225843");

if (!long.TryParse(runIdRaw, out var runId))
{
    // CI'da sessizce eski bir run'ı analiz etmektense hemen patlamak daha doğru.
    Console.WriteLine($"HATA: Geçersiz run ID: '{runIdRaw}'. Sayısal bir değer bekleniyor.");
    Environment.Exit(1);
    return;
}

Console.WriteLine($"Hedef: {owner}/{repo} run {runId}");

// --- Adım 1-2: Octokit ile job/annotation/log çekme, ErrorContext üretme ---
var github = new GitHubService(githubToken!);

var jobs = await github.GetJobsAsync(owner, repo, runId);

Octokit.WorkflowJob? failedJob = null;
foreach (var job in jobs)
{
    if (job.Conclusion?.StringValue == "failure")
    {
        failedJob = job;
        break;
    }
}

if (failedJob is null)
{
    Console.WriteLine($"HATA: {owner}/{repo} run {runId} için job bulunamadı.");
    return;
}

var annotations = await github.GetAnnotationsAsync(owner, repo, failedJob.Id);
var log = await github.DownloadJobLogAsync(owner, repo, failedJob.Id);

var errorContext = LogParser.BuildErrorContext(failedJob, annotations, log);

if (errorContext is null)
{
    Console.WriteLine($"HATA: '{failedJob.Name}' job'ında başarısız bir step bulunamadı, ErrorContext üretilemedi.");
    return;
}

Console.WriteLine("=== ErrorContext ===");
Console.WriteLine($"Job: {errorContext.JobName}");
Console.WriteLine($"Başarısız adım: {errorContext.FailedStepName}");
Console.WriteLine($"Dosya: {errorContext.FilePath}, Satır: {errorContext.LineNumber}");
Console.WriteLine($"AllFailuresLocated: {errorContext.AllFailuresLocated}");
Console.WriteLine($"ErrorMessage: {errorContext.ErrorMessage}");
Console.WriteLine($"CodeSnippet null mu: {errorContext.CodeSnippet is null}");
Console.WriteLine($"CodeSnippet uzunluk: {errorContext.CodeSnippet?.Length ?? 0}");
Console.WriteLine($"Annotation sayısı: {errorContext.FilteredAnnotations.Count}");

// --- "Koda bakma": FilePath+LineNumber ikisi de doluysa (compile/test hataları)
// ilgili dosyanın ±30 satırlık kesitini çekip prompt'a ekliyoruz. Path+line yoksa
// (restore/deploy hataları) bu adım tamamen atlanıyor.
if (errorContext.FilePath is not null && errorContext.LineNumber is int line)
{
    Console.WriteLine("İlgili kod dosyası çekiliyor...");
    try
    {
        var fileContent = await github.GetFileContentAsync(
            owner, repo, errorContext.FilePath, failedJob.HeadSha);

        if (fileContent is not null)
        {
            errorContext.CodeSnippet = CodeSnippetExtractor.ExtractSnippet(fileContent, line);
        }
        else
        {
            Console.WriteLine($"Uyarı: '{errorContext.FilePath}' dosyası bulunamadı, kod kesiti olmadan devam ediliyor.");
        }
    }
    catch (Exception ex)
    {
        // Kod çekme başarısız olsa bile agent LLM analizine kod olmadan devam etmeli
        Console.Error.WriteLine($"Kod çekilirken hata: {ex.Message}, kod kesiti olmadan devam ediliyor.");
    }

    if (errorContext.CodeSnippet is null)
    {
        Console.WriteLine("CodeSnippet: boş kaldı.");
    }
    else
    {
        Console.WriteLine($"CodeSnippet: dolduruldu ({errorContext.CodeSnippet.Split('\n').Length} satır):");
        Console.WriteLine("--- CodeSnippet başlangıcı ---");
        Console.WriteLine(errorContext.CodeSnippet);
        Console.WriteLine("--- CodeSnippet sonu ---");
    }
    Console.WriteLine();
}

// --- Adım 3: LLM analizi ---
Console.WriteLine("Azure OpenAI'a istek atılıyor...");
var llm = new LlmService(azureEndpoint!, azureKey!, azureDeployment!);

AnalysisResult? result;
try
{
    result = await llm.AnalyzeAsync(errorContext);
}
catch (Exception ex)
{
    // LLM katmanındaki HERHANGİ bir hata (ağ, deployment adı yanlış, rate limit,
    // JSON schema uyuşmazlığı vb.) süreci burada durdurmamalı. ForSkipped burada
    // uygun değil (o sadece token limiti aşımı için, int parametre alıyor) - bu
    // yüzden AnalysisResult'ı elle, "low" confidence ile oluşturuyoruz ki
    // ConfidenceBadge eşlemesi bozulmasın ve rapor akışı normal işlesin.
    Console.Error.WriteLine($"HATA: LLM analizi başarısız oldu. Hata: {ex.Message}");
    result = new AnalysisResult
    {
        Summary = "LLM analizi sırasında bir hata oluştu, otomatik analiz yapılamadı.",
        RootCause = $"{ex.GetType().Name}: {ex.Message}",
        SuggestedFix = "Lütfen logu manuel inceleyin. Sorun devam ederse Azure OpenAI bağlantısı/secret'ları kontrol edin.",
        Confidence = "low"
    };
}

if (result is null)
{
    Console.WriteLine("HATA: LLM'den null döndü (deserialize başarısız olmuş olabilir).");
    result = new AnalysisResult
    {
        Summary = "LLM'den boş/geçersiz yanıt döndü, analiz atlandı.",
        RootCause = "Deserialize işlemi başarısız oldu ya da LLM boş içerik döndürdü.",
        SuggestedFix = "Logu manuel inceleyin.",
        Confidence = "low"
    };
}

Console.WriteLine("--- AnalysisResult ---");
if (result.Skipped)
    Console.WriteLine($"ATLANDI: {result.SkipReason}");
else
{
    Console.WriteLine($"Summary:      {result.Summary}");
    Console.WriteLine($"RootCause:    {result.RootCause}");
    Console.WriteLine($"SuggestedFix: {result.SuggestedFix}");
    Console.WriteLine($"Confidence:   {result.Confidence}");
    Console.WriteLine($"AffectedFile: {result.AffectedFile}");
    Console.WriteLine($"AffectedLine: {result.AffectedLine}");
}

// --- Adım 4: Raporlama (PR yorumu -> commit yorumu -> Job Summary) ---
Console.WriteLine();
Console.WriteLine("GitHub'a raporlanıyor...");
var reportService = new ReportService(github.Client);
await reportService.ReportAsync(result, errorContext, owner, repo, failedJob.HeadSha, runId);
Console.WriteLine("Raporlama tamamlandı.");