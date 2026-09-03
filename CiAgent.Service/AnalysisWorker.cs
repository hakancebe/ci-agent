using CiAgent.Core;

namespace CiAgent.Service;

/// <summary>
/// Kuyruktaki işleri sırayla işleyen arka plan servisi.
///
/// Buradaki asıl mesele şu: analiz mantığının TEK satırı burada değil. İş
/// CiAnalysisPipeline'a devrediliyor — CLI'ın (ve Actions'ın) çalıştırdığı sınıfın
/// aynısı. Bu katman yalnızca "kuyruktan al, token üret, pipeline'ı kur, çalıştır,
/// hatayı yut" yapıyor. Göçün özü de bu: tetikleyici değişti, iş mantığı değişmedi.
/// </summary>
internal sealed class AnalysisWorker : BackgroundService
{
    private readonly WorkQueue _queue;
    private readonly GitHubAppAuth _auth;
    private readonly LlmService _llm;
    private readonly ContainerAppJobRunner? _fixRunner;
    private readonly ILogger<AnalysisWorker> _log;
    private readonly ILoggerFactory _loggerFactory;

    public AnalysisWorker(
        WorkQueue queue,
        GitHubAppAuth auth,
        LlmService llm,
        ILogger<AnalysisWorker> log,
        ILoggerFactory loggerFactory,
        ContainerAppJobRunner? fixRunner = null)
    {
        _queue = queue;
        _auth = auth;
        _llm = llm;
        _log = log;
        _loggerFactory = loggerFactory;
        _fixRunner = fixRunner;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _log.LogInformation("Analiz worker'ı başladı, kuyruk dinleniyor.");

        // Tek okuyucu, sıralı işleme. Paralellik BİLEREK yok: eşzamanlı iki analiz
        // aynı Azure OpenAI kotasını yer ve rate limit'e takılır. Faz 3'te
        // installation başına eşzamanlılık tavanı gelecek.
        await foreach (var work in _queue.ReadAllAsync(stoppingToken))
        {
            try
            {
                if (work.Analysis is not null)
                    await ProcessAsync(work.Analysis, stoppingToken);
                else if (work.Fix is not null)
                    await ProcessFixAsync(work.Fix, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                _log.LogInformation("Kapanış istendi, worker duruyor.");
                break;
            }
            catch (Exception ex)
            {
                // Tek bir işin patlaması worker'ı ÖLDÜRMEMELİ: ölürse kuyruktaki
                // diğer işler de hiç işlenmez ve servis sessizce sağır kalır
                // (health check yeşil, ama kimse iş yapmıyor).
                _log.LogError(ex, "İş işlenirken beklenmeyen hata: {Job}", work);
            }
        }
    }

    private async Task ProcessAsync(AnalysisJob job, CancellationToken ct)
    {
        _log.LogInformation("İş alındı: {Job} (delivery {Delivery})", job, job.DeliveryId);

        var token = await _auth.GetInstallationTokenAsync(job.InstallationId, ct);

        // GitHubService installation token'la kuruluyor — PAT ile kurulduğundaki
        // davranışın aynısı. GitHubService(string token) imzası zaten böyle
        // çalıştığı için Core'da değişiklik gerekmedi; yalnızca token'ın KAYNAĞI
        // değişti (secret yerine App'in ürettiği kısa ömürlü token).
        var github = new GitHubService(token);
        var report = new ReportService(github.Client);

        var pipeline = new CiAnalysisPipeline(
            github, _llm, report, _loggerFactory.CreateLogger<CiAnalysisPipeline>());

        var outcome = await pipeline.RunAsync(job.Owner, job.Repo, job.RunId);

        _log.LogInformation("İş tamamlandı: {Job} → {Status}", job, outcome.Status);
    }

    /// <summary>
    /// /fix'i ayrı bir Container Apps Job'da çalıştırır ve BİTENE KADAR bekler.
    ///
    /// Beklemek burada bir özellik: worker tek iş parçacıklı olduğu için bu,
    /// "aynı anda yalnızca bir /fix" garantisini veriyor. Aynı PR'da iki /fix'in
    /// aynı dala push edip birbirini ezmesi bu sayede imkânsız.
    /// </summary>
    private async Task ProcessFixAsync(FixJob job, CancellationToken ct)
    {
        if (_fixRunner is null)
        {
            _log.LogWarning(
                "/fix isteği geldi ({Job}) ama fix job'ı yapılandırılmamış, atlanıyor.", job);
            return;
        }

        _log.LogInformation("/fix işi alındı: {Job} (delivery {Delivery})", job, job.DeliveryId);

        var result = await _fixRunner.RunToCompletionAsync(job, ct);

        if (result.Started)
            _log.LogInformation("/fix işi tamamlandı: {Job}", job);
        else
            _log.LogError("/fix işi başlatılamadı: {Job} — {Error}", job, result.Error);
    }
}
