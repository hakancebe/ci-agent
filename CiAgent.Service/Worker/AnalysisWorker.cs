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

    /// <summary>
    /// Kapanış sırası: önce yeni iş kabulünü kes, sonra kuyruktakini bitir.
    ///
    /// Bu olmadan bekleyen işler SESSİZCE kayboluyordu — GitHub 202 ("aldım")
    /// cevabını aldığı için olayı tekrar göndermiyor, iş de hiç yapılmıyordu.
    /// Kullanıcı tarafından görünüşü "ajan bu PR'a hiç cevap vermedi" oluyordu:
    /// ne hata, ne uyarı, sadece sessizlik. Her deploy bu pencereyi açıyordu.
    ///
    /// CompleteWriter() burada, base.StopAsync'ten ÖNCE çağrılıyor: base zaten
    /// ExecuteAsync'in bitmesini bekliyor, kuyruk kapatılmazsa okuma döngüsü hiç
    /// sona ermez ve kapanış host'un ShutdownTimeout'una kadar boşuna asılırdı.
    /// </summary>
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        var pending = _queue.PendingCount;
        _log.LogInformation(
            "Kapanış istendi. Yeni iş alınmayacak; kuyrukta bekleyen {Pending} iş bitirilecek.",
            pending);

        _queue.CompleteWriter();

        await base.StopAsync(cancellationToken);

        _log.LogInformation("Worker durdu.");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _log.LogInformation("Analiz worker'ı başladı, kuyruk dinleniyor.");

        // Tek okuyucu, sıralı işleme. Paralellik BİLEREK yok: eşzamanlı iki analiz
        // aynı Azure OpenAI kotasını yer ve rate limit'e takılır. Faz 3'te
        // installation başına eşzamanlılık tavanı gelecek.
        //
        // stoppingToken BİLEREK verilmiyor: verilseydi kapanışta döngü anında
        // kesilir ve tamponda bekleyen işler terk edilirdi — düzeltmeye
        // çalıştığımız kusur tam olarak buydu. Döngü artık kuyruk KAPATILDIĞINDA
        // (CompleteWriter) sona eriyor, yani bekleyenler işlendikten sonra.
        // Sonsuza kadar asılma riski yok: host'un ShutdownTimeout'u üst sınır.
        await foreach (var work in _queue.ReadAllAsync(CancellationToken.None))
        {
            try
            {
                // İşin kendisine de stoppingToken verilmiyor: yarıda kesilen bir
                // analiz, hiç yapılmamış analizden farksız — üstelik LLM çağrısı
                // için para ödenmiş oluyor.
                if (work.Analysis is not null)
                    await ProcessAsync(work.Analysis, CancellationToken.None);
                else if (work.Fix is not null)
                    await ProcessFixAsync(work.Fix, CancellationToken.None);
            }
            catch (Exception ex)
            {
                // Tek bir işin patlaması worker'ı ÖLDÜRMEMELİ: ölürse kuyruktaki
                // diğer işler de hiç işlenmez ve servis sessizce sağır kalır
                // (health check yeşil, ama kimse iş yapmıyor).
                _log.LogError(ex, "İş işlenirken beklenmeyen hata: {Job}", work);
            }
        }

        _log.LogInformation("Kuyruk boşaldı, okuma döngüsü sona erdi.");
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
