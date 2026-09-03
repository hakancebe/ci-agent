using System.Text.Json;
using Azure.Monitor.OpenTelemetry.AspNetCore;
using CiAgent.Core;
using CiAgent.Service;

// CiAgent webhook servisi. Repoya workflow dosyası koymadan çalışan tetikleyici:
// GitHub App'in webhook'ları buraya düşüyor, iş kuyruğa alınıyor, arka planda
// CiAnalysisPipeline (CLI'ın çalıştırdığı sınıfın AYNISI) koşuyor.

var builder = WebApplication.CreateBuilder(args);

// ACA logları stdout'tan topluyor; JSON yerine düz konsol formatı, Log Analytics'te
// insan tarafından okunabilir kalması için.
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(o =>
{
    o.SingleLine = true;
    o.TimestampFormat = "HH:mm:ss ";
});

var options = ServiceOptions.FromConfiguration(builder.Configuration);

// İzleme yalnızca yapılandırıldıysa açılıyor. Bağlantı dizesi yoksa servis
// normal çalışıyor — izleme bir kolaylık, ayağa kalkmanın ön koşulu değil.
if (!string.IsNullOrWhiteSpace(options.AppInsightsConnectionString))
{
    builder.Services.AddOpenTelemetry().UseAzureMonitor(o =>
        o.ConnectionString = options.AppInsightsConnectionString);
}

builder.Services.AddSingleton(options);
builder.Services.AddSingleton(new WorkQueue());
builder.Services.AddSingleton(new InstallationLimiter(
    options.MaxJobsPerInstallation, options.RateLimitWindow));
builder.Services.AddSingleton(new GitHubAppAuth(options.AppId, options.PrivateKeyPem));
builder.Services.AddSingleton(LlmServiceFactory.Create(
    options.AzureOpenAiEndpoint, options.AzureOpenAiKey,
    options.AzureOpenAiDeployment, options.AzureClientId));

// /fix yalnızca tam yapılandırıldığında etkin. Eksikse servis yine ayağa kalkıyor
// ve analiz çalışmaya devam ediyor — Faz 1 kurulumu bu sürüme geçince bozulmasın.
if (options.FixEnabled)
{
    builder.Services.AddSingleton(sp => new ContainerAppJobRunner(
        options, sp.GetRequiredService<ILogger<ContainerAppJobRunner>>()));
}

// Worker açıkça kuruluyor, AddHostedService<AnalysisWorker>() ile değil.
// Sebep: /fix kapalıyken ContainerAppJobRunner hiç KAYITLI DEĞİL ve worker onu
// opsiyonel alıyor. GetService (GetRequiredService değil) kayıtlı olmayan servis
// için null döner — "yoksa null" niyetini doğrudan ifade eden yol bu.
//
// Önceki hali `AddSingleton<ContainerAppJobRunner?>(_ => null)` idi: çalışıyordu
// ama AddSingleton'ın `where TService : class` kısıtını nullable bir tiple
// deliyordu (CS8634). Null'ı kaydetmek yerine hiç kaydetmemek hem tip güvenli
// hem de daha dürüst — "bu servis yok" demenin doğru yolu onu yaratmamak.
builder.Services.AddHostedService(sp => new AnalysisWorker(
    sp.GetRequiredService<WorkQueue>(),
    sp.GetRequiredService<GitHubAppAuth>(),
    sp.GetRequiredService<LlmService>(),
    sp.GetRequiredService<ILogger<AnalysisWorker>>(),
    sp.GetRequiredService<ILoggerFactory>(),
    sp.GetService<ContainerAppJobRunner>()));

var app = builder.Build();

// Hangi kimlik yolunun seçildiği başlangıçta AÇIKÇA loglanıyor: yanlış tarafa
// düşmek sessiz bir hata (prod'da eski anahtara bağlı kalmak gibi), bu satır onu
// ilk saniyede görünür kılıyor.
var startupLog = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Baslangic");

startupLog.LogInformation(
    "Azure OpenAI kimlik doğrulama: {Mode}",
    options.UseManagedIdentityForOpenAi ? "managed identity (anahtar yok)" : "API anahtarı");

startupLog.LogInformation(
    "İzleme: {Mode} — hız sınırı: {Limit} iş / {Window:0} dk",
    string.IsNullOrWhiteSpace(options.AppInsightsConnectionString)
        ? "kapalı (APPLICATIONINSIGHTS_CONNECTION_STRING yok)"
        : "Application Insights açık",
    options.MaxJobsPerInstallation, options.RateLimitWindow.TotalMinutes);

// ACA'nın canlılık kontrolü için. Kuyruk/worker durumunu YANSITMIYOR - yalnızca
// "süreç ayakta mı" sorusuna cevap veriyor.
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.MapPost("/webhooks/github", async (HttpRequest request, WorkQueue queue,
    InstallationLimiter limiter, ServiceOptions opts, ILoggerFactory loggerFactory) =>
{
    var log = loggerFactory.CreateLogger("Webhook");

    // İmza HAM byte'lar üzerinden doğrulanıyor: önce gövdeyi olduğu gibi okuyoruz,
    // JSON'a çevirmek İMZA DOĞRULANDIKTAN SONRA. Ters sırada, doğrulanmamış veriyi
    // ayrıştırmış oluruz.
    using var buffer = new MemoryStream();
    await request.Body.CopyToAsync(buffer);
    var body = buffer.ToArray();

    var signature = request.Headers["X-Hub-Signature-256"].ToString();

    if (!WebhookSignature.IsValid(body, signature, opts.WebhookSecret))
    {
        // Detay VERMİYORUZ: "imza yanlış" ile "secret tanımsız" arasındaki farkı
        // dışarıya söylemek saldırgana bilgi verir.
        log.LogWarning("İmza doğrulanamadı, istek reddedildi.");
        return Results.Unauthorized();
    }

    var eventName = request.Headers["X-GitHub-Event"].ToString();
    var deliveryId = request.Headers["X-GitHub-Delivery"].ToString();

    if (string.IsNullOrWhiteSpace(deliveryId))
    {
        log.LogWarning("X-GitHub-Delivery başlığı yok, istek reddedildi.");
        return Results.BadRequest(new { error = "X-GitHub-Delivery gerekli" });
    }

    JsonDocument payload;
    try
    {
        payload = JsonDocument.Parse(body);
    }
    catch (JsonException ex)
    {
        log.LogWarning(ex, "Payload JSON olarak ayrıştırılamadı (delivery {Delivery}).", deliveryId);
        return Results.BadRequest(new { error = "geçersiz JSON" });
    }

    using (payload)
    {
        // /fix yolu: issue_comment olayları buradan geçiyor. Ayrıştırıcı ucuz
        // elemeleri (PR mı, /fix komutu mu, yazan yetkili mi) burada yapıyor —
        // hepsi CiAgent.Core'daki aynı kurallara giderek. Böylece yetkisiz bir
        // yorum için container BAŞLATILMIYOR.
        if (string.Equals(eventName, "issue_comment", StringComparison.OrdinalIgnoreCase))
        {
            if (!opts.FixEnabled)
            {
                log.LogInformation("/fix yapılandırılmamış, issue_comment yok sayıldı.");
                return Results.Accepted(value: new { status = "ignored", reason = "/fix kapalı" });
            }

            var (fixJob, fixReason) = FixEventParser.Parse(eventName, deliveryId, payload);

            if (fixJob is null)
            {
                log.LogInformation("/fix olayı yok sayıldı (delivery {Delivery}): {Reason}",
                    deliveryId, fixReason);
                return Results.Accepted(value: new { status = "ignored", reason = fixReason });
            }

            var fixLimit = limiter.TryAcquire(fixJob.InstallationId);
            if (!fixLimit.Allowed)
            {
                // 202: 503 dönmek GitHub'ı tekrar denemeye iter, yani sınırı aşan
                // installation'ı daha da hızlandırırdı — istediğimizin tam tersi.
                log.LogWarning("/fix sınıra takıldı: {Reason}", fixLimit.Reason);
                return Results.Accepted(value: new { status = "rate_limited", reason = fixLimit.Reason });
            }

            var fixEnqueued = queue.TryEnqueue(fixJob);
            log.LogInformation("/fix {Result}: {Job} (delivery {Delivery})",
                fixEnqueued, fixJob, deliveryId);

            return fixEnqueued switch
            {
                EnqueueResult.Queued => Results.Accepted(value: new { status = "queued" }),
                EnqueueResult.Duplicate => Results.Accepted(value: new { status = "duplicate" }),
                _ => Results.StatusCode(503)
            };
        }

        var outcome = WebhookParser.Parse(eventName, deliveryId, payload, opts.WatchedWorkflows);

        if (outcome.Job is null)
        {
            // Yok sayılan olay da 2xx almalı: 4xx dönersek GitHub bunu başarısız
            // teslimat sayıp tekrar tekrar gönderir ve App'in teslimat geçmişi
            // kırmızıya boyanır. "Aldım, ilgilenmiyorum" doğru cevap.
            log.LogInformation("Olay yok sayıldı ({Event}, delivery {Delivery}): {Reason}",
                eventName, deliveryId, outcome.Reason);
            return Results.Accepted(value: new { status = "ignored", reason = outcome.Reason });
        }

        var limit = limiter.TryAcquire(outcome.Job.InstallationId);
        if (!limit.Allowed)
        {
            log.LogWarning("Analiz sınıra takıldı: {Reason}", limit.Reason);
            return Results.Accepted(value: new { status = "rate_limited", reason = limit.Reason });
        }

        var enqueued = queue.TryEnqueue(outcome.Job);

        switch (enqueued)
        {
            case EnqueueResult.Queued:
                log.LogInformation("Kuyruğa alındı: {Job} (delivery {Delivery})",
                    outcome.Job, deliveryId);
                return Results.Accepted(value: new { status = "queued" });

            case EnqueueResult.Duplicate:
                log.LogInformation("Tekrar teslimat yok sayıldı: {Job} (delivery {Delivery})",
                    outcome.Job, deliveryId);
                return Results.Accepted(value: new { status = "duplicate" });

            default:
                // 503 bilinçli: GitHub bunu başarısız sayıp olayı TEKRAR gönderir,
                // yani kuyruk boşaldığında iş kaybolmadan geri gelir.
                log.LogWarning("Kuyruk dolu, iş reddedildi: {Job}", outcome.Job);
                return Results.StatusCode(503);
        }
    }
});

app.Run();

// Minimal API'nin top-level statement'ları bir Program sınıfı üretiyor ama internal
// oluyor; test projesinden erişilebilmesi için açıkça public yapılıyor.
public partial class Program { }
