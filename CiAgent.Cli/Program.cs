using CiAgent.Core;

// --- Secret'lar: GitHub ve Azure OpenAI birbirinden bağımsız env var'lar ---
// GITHUB_TOKEN yalnızca Octokit/GitHub API çağrıları için kullanılır.
// AZURE_OPENAI_* ise LLM analizi için ayrı bir secret setidir, birbirine karıştırılmaz.
var githubToken = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
var azureEndpoint = Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT");
var azureKey = Environment.GetEnvironmentVariable("AZURE_OPENAI_KEY");
var azureDeployment = Environment.GetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT");

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
var runIdRaw = Resolve(2, "CI_AGENT_RUN_ID", "32651264809");

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
Console.WriteLine($"Annotation sayısı: {errorContext.FilteredAnnotations.Count}");
Console.WriteLine();

// --- Adım 3: LLM analizi ---
Console.WriteLine("Azure OpenAI'a istek atılıyor...");
var llm = new LlmService(azureEndpoint!, azureKey!, azureDeployment!);
var result = await llm.AnalyzeAsync(errorContext);

if (result is null)
{
    Console.WriteLine("HATA: LLM'den null döndü (deserialize başarısız olmuş olabilir).");
    return;
}

Console.WriteLine("--- AnalysisResult ---");
Console.WriteLine($"Summary:      {result.Summary}");
Console.WriteLine($"RootCause:    {result.RootCause}");
Console.WriteLine($"SuggestedFix: {result.SuggestedFix}");
Console.WriteLine($"Confidence:   {result.Confidence}");
Console.WriteLine($"AffectedFile: {result.AffectedFile}");
Console.WriteLine($"AffectedLine: {result.AffectedLine}");

// --- Adım 4: Raporlama (PR yorumu -> commit yorumu -> Job Summary) ---
Console.WriteLine();
Console.WriteLine("GitHub'a raporlanıyor...");
var reportService = new ReportService(github.Client);
await reportService.ReportAsync(result, errorContext, owner, repo, failedJob.HeadSha, runId);
Console.WriteLine("Raporlama tamamlandı.");