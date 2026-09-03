namespace CiAgent.Service;

/// <summary>
/// Servisin çalışması için gereken her şey. Eksik yapılandırmayla AYAĞA KALKMAMAK
/// bilinçli: yanlış yapılandırılmış bir servis sessizce webhook yutup hiç iş
/// yapmaz, ve bu durum aylarca fark edilmeyebilir. Başlangıçta patlamak, ACA'da
/// hemen görünen bir crash-loop demek.
/// </summary>
internal sealed class ServiceOptions
{
    public required string AppId { get; init; }
    public required string PrivateKeyPem { get; init; }
    public required string WebhookSecret { get; init; }
    public required string AzureOpenAiEndpoint { get; init; }
    public required string AzureOpenAiDeployment { get; init; }

    /// <summary>
    /// Azure OpenAI API anahtarı. BOŞ OLABİLİR — o durumda managed identity
    /// kullanılır (prod yolu). Anahtar yalnızca lokal geliştirme ve Actions için.
    /// </summary>
    public string? AzureOpenAiKey { get; init; }

    /// <summary>Anahtar verilmediyse kimlik doğrulama managed identity ile yapılır.</summary>
    public bool UseManagedIdentityForOpenAi => string.IsNullOrWhiteSpace(AzureOpenAiKey);

    /// <summary>
    /// İzlenecek workflow adları (virgülle ayrılmış). Varsayılan "CI" — eski
    /// ci-agent.yml'deki `workflows: ["CI"]` filtresinin birebir karşılığı.
    /// Boş verilirse tüm workflow'lar izlenir.
    /// </summary>
    public required IReadOnlyCollection<string> WatchedWorkflows { get; init; }

    // --- /fix (Container Apps Job) ---------------------------------------
    // Bunların hepsi boşsa /fix devre dışı kalır ve issue_comment olayları
    // yok sayılır. Bu bilinçli: Faz 1 kurulumu (yalnızca analiz) bozulmadan
    // çalışmaya devam edebilmeli.

    public string? AzureSubscriptionId { get; init; }
    public string? AzureResourceGroup { get; init; }
    public string? FixJobName { get; init; }

    /// <summary>Job'ın çalıştıracağı image — web servisinkiyle aynı olmalı.</summary>
    public string? FixJobImage { get; init; }

    /// <summary>User-assigned managed identity'nin client id'si (ARM token'ı için).</summary>
    public string? AzureClientId { get; init; }

    public TimeSpan FixJobTimeout { get; init; } = TimeSpan.FromMinutes(20);

    // Job tanımı her çalıştırmadan önce PATCH'lendiği ve PATCH containers dizisini
    // komple değiştirdiği için, kaynak ayarları her seferinde tekrar yazılmalı;
    // yazılmazsa varsayılana düşüp build'i yavaşlatır.
    public double FixJobCpu { get; init; } = 2.0;
    public string FixJobMemory { get; init; } = "4Gi";

    /// <summary>
    /// Application Insights bağlantı dizesi. BOŞ OLABİLİR — o durumda izleme
    /// kapalı kalır ve servis normal çalışır. Opsiyonel olması bilinçli: izleme
    /// bir kolaylık, servisin ayağa kalkmasının ön koşulu değil.
    /// </summary>
    public string? AppInsightsConnectionString { get; init; }

    // --- Installation başına hız sınırı ---------------------------------
    // Agent'ın maliyeti başkalarının davranışına bağlı: her CI hatası bir LLM
    // çağrısı. Tek bir repo çıldırırsa fatura sınırsız büyür.
    public int MaxJobsPerInstallation { get; init; } = 20;
    public TimeSpan RateLimitWindow { get; init; } = TimeSpan.FromHours(1);

    /// <summary>/fix çalıştırmak için gereken her şey yapılandırılmış mı?</summary>
    public bool FixEnabled =>
        !string.IsNullOrWhiteSpace(AzureSubscriptionId)
        && !string.IsNullOrWhiteSpace(AzureResourceGroup)
        && !string.IsNullOrWhiteSpace(FixJobName)
        && !string.IsNullOrWhiteSpace(FixJobImage);

    public static ServiceOptions FromConfiguration(IConfiguration config)
    {
        var missing = new List<string>();

        string Required(string key)
        {
            var value = config[key];
            if (string.IsNullOrWhiteSpace(value)) missing.Add(key);
            return value ?? "";
        }

        var appId = Required("GITHUB_APP_ID");
        var privateKey = ReadPrivateKey(config, missing);
        var webhookSecret = Required("GITHUB_WEBHOOK_SECRET");
        var endpoint = Required("AZURE_OPENAI_ENDPOINT");
        var deployment = Required("AZURE_OPENAI_DEPLOYMENT");

        // Anahtar BİLEREK zorunlu değil: yoksa managed identity kullanılıyor.
        // Faz 3'ün amacı zaten bu değeri prod'dan tamamen kaldırmak.
        var key = config["AZURE_OPENAI_KEY"];

        if (missing.Count > 0)
        {
            throw new InvalidOperationException(
                $"Şu yapılandırma değerleri eksik: {string.Join(", ", missing)}. "
                + "GITHUB_APP_PRIVATE_KEY (PEM içeriği) yerine GITHUB_APP_PRIVATE_KEY_PATH "
                + "(dosya yolu) da verilebilir.");
        }

        var watched = (config["CI_AGENT_WATCHED_WORKFLOWS"] ?? "CI")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToArray();

        return new ServiceOptions
        {
            AppId = appId,
            PrivateKeyPem = privateKey,
            WebhookSecret = webhookSecret,
            AzureOpenAiEndpoint = endpoint,
            AzureOpenAiKey = key,
            AzureOpenAiDeployment = deployment,
            WatchedWorkflows = watched,

            // /fix ayarları "zorunlu" listesinde DEĞİL: eksiklerse servis yine
            // ayağa kalkar, sadece /fix kapalı olur. Faz 1'de kurulmuş bir
            // servisin bu sürüme geçtiğinde patlamaması için.
            AzureSubscriptionId = config["AZURE_SUBSCRIPTION_ID"],
            AzureResourceGroup = config["AZURE_RESOURCE_GROUP"],
            FixJobName = config["CI_AGENT_FIX_JOB_NAME"],
            FixJobImage = config["CI_AGENT_FIX_JOB_IMAGE"],
            AzureClientId = config["AZURE_CLIENT_ID"],
            FixJobTimeout = int.TryParse(config["CI_AGENT_FIX_TIMEOUT_MINUTES"], out var minutes)
                ? TimeSpan.FromMinutes(minutes)
                : TimeSpan.FromMinutes(20),
            MaxJobsPerInstallation = int.TryParse(config["CI_AGENT_MAX_JOBS_PER_HOUR"], out var maxJobs)
                ? maxJobs
                : 20,
            AppInsightsConnectionString = config["APPLICATIONINSIGHTS_CONNECTION_STRING"]
        };
    }

    /// <summary>
    /// Private key iki şekilde verilebiliyor: doğrudan PEM içeriği (ACA secret'ı
    /// olarak env var'a gömmek için) ya da dosya yolu (lokal geliştirmede .pem
    /// dosyasını env var'a çok satırlı olarak sıkıştırmamak için).
    /// </summary>
    private static string ReadPrivateKey(IConfiguration config, List<string> missing)
    {
        var inline = config["GITHUB_APP_PRIVATE_KEY"];
        if (!string.IsNullOrWhiteSpace(inline))
        {
            // ACA/Docker env var'larında çok satırlı değer taşımak zor olduğu için
            // satır sonlarının "\n" olarak kaçırılmış hali de kabul ediliyor.
            return inline.Contains("\\n") ? inline.Replace("\\n", "\n") : inline;
        }

        var path = config["GITHUB_APP_PRIVATE_KEY_PATH"];
        if (!string.IsNullOrWhiteSpace(path))
        {
            if (!File.Exists(path))
                throw new InvalidOperationException($"Private key dosyası bulunamadı: '{path}'");

            return File.ReadAllText(path);
        }

        missing.Add("GITHUB_APP_PRIVATE_KEY");
        return "";
    }
}
