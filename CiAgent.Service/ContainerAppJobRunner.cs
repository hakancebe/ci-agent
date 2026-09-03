using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Azure.Core;
using Azure.Identity;
using CiAgent.Core;

namespace CiAgent.Service;

/// <summary>Bir job çalıştırmasının sonucu.</summary>
internal sealed record JobRunResult(bool Started, string? ExecutionName, string? Error);

/// <summary>
/// /fix'i ayrı bir Container Apps Job olarak çalıştırır.
///
/// Neden web servisinin içinde değil de ayrı bir job?
///   /fix, üçüncü tarafın PR'ındaki kodu klonlayıp `dotnet build` + `dotnet test`
///   çalıştırıyor — yani yabancı MSBuild target'ları ve testleri koşuyor. Bunu
///   webhook'lara cevap veren sürecin içinde yapmak iki riski birleştirirdi:
///   düşmanca bir PR servisi etkileyebilir, ve dakikalarca süren bir build
///   webhook işleyicisini aç bırakabilirdi. Ayrı job = her çalıştırma için taze,
///   kendi kaynak sınırı olan, işi bitince ölen bir container.
///
/// Aynı image kullanılıyor; ayrımı yalnızca CI_AGENT_MODE=fix env var'ı yapıyor.
/// </summary>
internal sealed class ContainerAppJobRunner
{
    private const string ArmScope = "https://management.azure.com/.default";
    private const string ApiVersion = "2024-03-01";

    // Job'ın bitmesini beklerken ne sıklıkla soralım. Çok sık sormak ARM'ın rate
    // limitine takılır, çok seyrek sormak /fix'in bittiğini geç fark etmek demek.
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(10);

    private readonly ServiceOptions _options;
    private readonly HttpClient _http;
    private readonly TokenCredential _credential;
    private readonly ILogger<ContainerAppJobRunner> _log;

    public ContainerAppJobRunner(
        ServiceOptions options,
        ILogger<ContainerAppJobRunner> log,
        HttpClient? http = null,
        TokenCredential? credential = null)
    {
        _options = options;
        _log = log;
        _http = http ?? new HttpClient();

        // Managed identity: ACA'da çalışırken kimlik container'a platform
        // tarafından veriliyor, ortamda hiçbir secret tutulmuyor.
        _credential = credential ?? new DefaultAzureCredential(
            new DefaultAzureCredentialOptions
            {
                ManagedIdentityClientId = _options.AzureClientId
            });
    }

    /// <summary>
    /// Job'ı başlatır ve BİTENE KADAR bekler.
    ///
    /// Beklemek bilinçli: çağıran worker tek iş parçacıklı olduğu için, burada
    /// beklemek "aynı anda yalnızca bir /fix" garantisini bedavaya veriyor. Aynı
    /// PR'da iki /fix'in aynı dala push edip çakışması bu sayede imkânsız.
    /// (Bu, gerekenden daha katı bir garanti — PR başına serileştirme yeterdi;
    /// Faz 3'te installation başına eşzamanlılık tavanıyla gevşetilebilir.)
    /// </summary>
    public async Task<JobRunResult> RunToCompletionAsync(FixJob job, CancellationToken ct)
    {
        var started = await StartAsync(job, ct);
        if (!started.Started)
            return started;

        await WaitForCompletionAsync(started.ExecutionName!, ct);
        return started;
    }

    /// <summary>
    /// İki adım: önce job tanımını bu çalıştırmanın hedefiyle güncelle, sonra
    /// override'SIZ başlat.
    ///
    /// Neden start override kullanılmıyor? Denendi ve platform seviyesinde
    /// çalışmıyor:
    ///
    ///   • Override, container spec'in TAMAMININ yerine geçiyor (image vermek
    ///     zorunlu olması bunun kanıtı) — yani job'ın kendi env var'ları siliniyor.
    ///   • Override içinde `secretRef` KORUNMUYOR: gönderilen secret adı ne olursa
    ///     olsun ACA hepsini `cappjob-&lt;job-adı&gt;` diye tek bir yer tutucuya
    ///     çeviriyor. Sonuç: sır gerektiren bütün env var'lar AYNI değeri alıyor.
    ///     (`az containerapp job start --env-vars "X=secretref:..."` da birebir
    ///     aynı sonucu veriyor, yani bu bizim serileştirmemizin hatası değil.)
    ///
    /// Geriye iki seçenek kalıyordu: sırları ARM gövdesine DÜZ METİN koymak (ki o
    /// gövde Azure Activity Log'a düşer), ya da job tanımını güncelleyip
    /// override'sız başlatmak. İkincisi seçildi — hiçbir sır hiçbir istek
    /// gövdesinde taşınmıyor, yalnızca secret ADLARI geçiyor.
    ///
    /// Bunun ön koşulu: aynı anda tek bir /fix çalışmalı, yoksa iki çalıştırma
    /// birbirinin job tanımını ezer. Bu garanti zaten var — worker tek iş
    /// parçacıklı ve RunToCompletionAsync bitene kadar bekliyor.
    /// </summary>
    private async Task<JobRunResult> StartAsync(FixJob job, CancellationToken ct)
    {
        var configured = await UpdateJobDefinitionAsync(job, ct);
        if (configured is not null)
            return new JobRunResult(false, null, configured);

        var token = await _credential.GetTokenAsync(new TokenRequestContext([ArmScope]), ct);

        var url = JobUrl() + $"/start?api-version={ApiVersion}";

        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            // Gövde bilerek BOŞ: hedef zaten job tanımına yazıldı.
            Content = JsonContent.Create(new { })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);

        using var response = await _http.SendAsync(request, ct);
        var payload = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            var error = $"{(int)response.StatusCode} {response.ReasonPhrase} — {Masker.Mask(payload)}";
            _log.LogError("Fix job başlatılamadı ({Job}): {Error}", job, error);
            return new JobRunResult(false, null, error);
        }

        var executionName = ReadExecutionName(payload);
        _log.LogInformation("Fix job başlatıldı: {Job} → execution {Execution}", job, executionName);

        return new JobRunResult(true, executionName, null);
    }

    /// <summary>
    /// Job tanımını bu çalıştırmanın hedefiyle günceller. Hata varsa mesajı döner.
    /// </summary>
    private async Task<string?> UpdateJobDefinitionAsync(FixJob job, CancellationToken ct)
    {
        var token = await _credential.GetTokenAsync(new TokenRequestContext([ArmScope]), ct);

        using var request = new HttpRequestMessage(
            HttpMethod.Patch, JobUrl() + $"?api-version={ApiVersion}")
        {
            Content = JsonContent.Create(BuildJobPatch(job))
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);

        using var response = await _http.SendAsync(request, ct);

        if (response.IsSuccessStatusCode)
            return null;

        var payload = await response.Content.ReadAsStringAsync(ct);
        var error = $"{(int)response.StatusCode} {response.ReasonPhrase} — {Masker.Mask(payload)}";
        _log.LogError("Fix job tanımı güncellenemedi ({Job}): {Error}", job, error);
        return error;
    }

    private string JobUrl() =>
        $"https://management.azure.com/subscriptions/{_options.AzureSubscriptionId}"
      + $"/resourceGroups/{_options.AzureResourceGroup}"
      + $"/providers/Microsoft.App/jobs/{_options.FixJobName}";

    /// <summary>
    /// Job tanımına gönderilecek PATCH gövdesi.
    ///
    /// `resources` açıkça veriliyor: PATCH containers dizisini komple değiştirdiği
    /// için, yazılmazsa CPU/bellek varsayılana düşer ve build yetersiz kaynakta
    /// koşar.
    /// </summary>
    internal object BuildJobPatch(FixJob job) => new
    {
        properties = new
        {
            template = new
            {
                containers = new[]
                {
                    new
                    {
                        name = _options.FixJobName,
                        image = _options.FixJobImage,
                        resources = new { cpu = _options.FixJobCpu, memory = _options.FixJobMemory },
                        env = BuildEnvironment(job)
                    }
                }
            }
        }
    };

    /// <summary>
    /// Çalıştırmanın env var listesi.
    ///
    /// DİKKAT: Bu liste job'ın kendi env var'larına EKLENMİYOR, onların YERİNE
    /// geçiyor — override, container spec'in tamamını değiştiriyor. Yani job
    /// tanımında duran GITHUB_APP_ID / AZURE_OPENAI_* burada tekrar verilmezse
    /// container onlarsız başlar ve "env var eksik" diyerek hemen ölür.
    ///
    /// Secret'lar `secretRef` ile geçiyor: değerleri job'ın secret deposunda
    /// duruyor, bu istekte düz metin olarak TAŞINMIYOR. Böylece ARM çağrısının
    /// gövdesi (ve Azure Activity Log'a düşen kaydı) hiçbir sır içermiyor.
    /// </summary>
    internal object[] BuildEnvironment(FixJob job)
    {
        var env = new List<object>
        {
            Value("CI_AGENT_MODE", "fix"),
            Value("CI_AGENT_CLONE", "true"),

            // Job'ın kendi tanımından gelen, PATCH'in sildiği değerler:
            Value("GITHUB_APP_ID", _options.AppId),
            Secret("GITHUB_APP_PRIVATE_KEY", "github-app-private-key"),
            Value("AZURE_OPENAI_ENDPOINT", _options.AzureOpenAiEndpoint),
            Value("AZURE_OPENAI_DEPLOYMENT", _options.AzureOpenAiDeployment),

            // Bu çalıştırmaya özgü hedef:
            Value("CI_AGENT_OWNER", job.Owner),
            Value("CI_AGENT_REPO", job.Repo),
            Value("CI_AGENT_PR_NUMBER", job.PullRequestNumber.ToString()),
            Value("CI_AGENT_COMMENT_ID", job.CommentId.ToString()),
            Value("CI_AGENT_COMMENT_BODY", job.CommentBody),
            Value("CI_AGENT_COMMENT_AUTHOR_ASSOCIATION", job.AuthorAssociation),
            Value("CI_AGENT_INSTALLATION_ID", job.InstallationId.ToString())
        };

        // Anahtar YALNIZCA servisin kendisi anahtar kullanıyorsa ekleniyor.
        // Koşulsuz eklemek göçü sessizce geri alırdı: deploy anahtarı kaldırsa
        // bile, bir sonraki /fix job'ı PATCH'lerken onu geri koyar ve container
        // managed identity'ye hiç geçmezdi.
        if (!_options.UseManagedIdentityForOpenAi)
            env.Insert(6, Secret("AZURE_OPENAI_KEY", "azure-openai-key"));

        // Managed identity modunda kimliğin client id'si şart: ACA'da birden fazla
        // kimlik atanabildiği için container hangisini kullanacağını bilmeli.
        else if (!string.IsNullOrWhiteSpace(_options.AzureClientId))
            env.Add(Value("AZURE_CLIENT_ID", _options.AzureClientId));

        // İzleme opsiyonel ve OpenAI kimlik doğrulama kararından BAĞIMSIZ: web
        // servisi Faz 3'te izleme aldı, job'a hiç eklenmemişti — /fix çalışmaları
        // (ARM sorguları, GitHub App token üretimi, LLM çağrıları) şu ana kadar
        // görünmezdi. Burada da PATCH'in silmemesi için her çalıştırmada
        // yeniden veriliyor, tıpkı yukarıdaki diğer "job'ın kendi tanımından
        // gelen" değerler gibi.
        if (!string.IsNullOrWhiteSpace(_options.AppInsightsConnectionString))
            env.Add(Value("APPLICATIONINSIGHTS_CONNECTION_STRING", _options.AppInsightsConnectionString));

        return env.ToArray();
    }

    private static object Value(string name, string value) => new { name, value };

    // ARM'ın JSON şemasında alan adı `secretRef` (az CLI'daki "secretref:" öneki
    // değil) — yanlış yazımda env var sessizce BOŞ gelir, hata vermez.
    private static object Secret(string name, string secretRef) => new { name, secretRef };

    /// <summary>
    /// Yanıttan execution adını çıkarır. Bulunamazsa null döner — job başlamıştır
    /// ama durumunu izleyemeyiz, bu yüzden çağıran taraf beklemeden devam eder.
    /// </summary>
    internal static string? ReadExecutionName(string responseBody)
    {
        try
        {
            using var doc = JsonDocument.Parse(responseBody);
            return doc.RootElement.TryGetProperty("name", out var name) ? name.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task WaitForCompletionAsync(string executionName, CancellationToken ct)
    {
        var url =
            $"https://management.azure.com/subscriptions/{_options.AzureSubscriptionId}"
          + $"/resourceGroups/{_options.AzureResourceGroup}"
          + $"/providers/Microsoft.App/jobs/{_options.FixJobName}/executions/{executionName}"
          + $"?api-version={ApiVersion}";

        var deadline = DateTimeOffset.UtcNow + _options.FixJobTimeout;

        while (DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(PollInterval, ct);

            string status;
            try
            {
                status = await ReadStatusAsync(url, ct);
            }
            catch (Exception ex)
            {
                // Durum sorgusu geçici olarak patlayabilir (ARM throttling, ağ).
                // Bu, job'ın başarısız olduğu anlamına gelmez — beklemeye devam.
                _log.LogWarning(ex, "Job durumu okunamadı, tekrar denenecek.");
                continue;
            }

            // Terminal durumlar: bunlardan sonra bir daha değişmez.
            if (status is "Succeeded" or "Failed" or "Stopped")
            {
                _log.LogInformation("Fix job bitti: {Execution} → {Status}", executionName, status);
                return;
            }
        }

        // Zaman aşımı job'ı DURDURMUYOR: sadece beklemeyi bırakıyoruz. Job kendi
        // başına devam edip sonucu PR'a yazabilir; burada takılı kalmak ise
        // kuyruktaki diğer işleri süresiz bloke ederdi.
        _log.LogWarning(
            "Fix job {Execution} {Timeout} içinde bitmedi, beklemeyi bırakıyorum "
            + "(job arka planda devam ediyor olabilir).",
            executionName, _options.FixJobTimeout);
    }

    private async Task<string> ReadStatusAsync(string url, CancellationToken ct)
    {
        var token = await _credential.GetTokenAsync(new TokenRequestContext([ArmScope]), ct);

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);

        using var response = await _http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadAsStringAsync(ct);
        return ReadStatus(payload);
    }

    /// <summary>Execution yanıtından çalışma durumunu çıkarır.</summary>
    internal static string ReadStatus(string responseBody)
    {
        try
        {
            using var doc = JsonDocument.Parse(responseBody);
            return doc.RootElement.TryGetProperty("properties", out var props)
                && props.TryGetProperty("status", out var status)
                    ? status.GetString() ?? "Unknown"
                    : "Unknown";
        }
        catch (JsonException)
        {
            return "Unknown";
        }
    }
}
