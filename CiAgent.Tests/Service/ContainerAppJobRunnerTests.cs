using System.Text.Json;
using CiAgent.Service;
using Microsoft.Extensions.Logging.Abstractions;

namespace CiAgent.Tests;

/// <summary>
/// ARM'a gönderilen gövdenin ŞEKLİ. Bu dosya üç canlı hatadan doğdu:
///
///   1) Gövde `template` içine sarılmıştı → ARM 400: "Unknown properties template
///      in StartJobExecutionTemplate are not supported".
///
///   2) Start override, job'ın env var'larına EKLENMİYOR; onların yerine geçiyor.
///      İlk sürüm yalnızca CI_AGENT_* gönderiyordu, yani container GITHUB_APP_ID
///      ve AZURE_OPENAI_* olmadan başlayıp hemen ölecekti.
///
///   3) Start override'ında `secretRef` KORUNMUYOR: ACA gönderilen secret adı ne
///      olursa olsun hepsini `cappjob-<job-adı>` yer tutucusuna çeviriyor, yani
///      sır gerektiren bütün env var'lar AYNI değeri alıyor. Canlıda bu,
///      AZURE_OPENAI_KEY'in App private key'ini almasına ve Azure OpenAI'ın 401
///      dönmesine yol açtı. (`az containerapp job start --env-vars
///      "X=secretref:..."` da birebir aynı sonucu veriyor — platform davranışı.)
///
/// Çözüm: start override HİÇ kullanılmıyor. Her çalıştırmadan önce job TANIMI
/// PATCH'leniyor (orada secretRef doğru çalışıyor), sonra override'sız
/// başlatılıyor. Bu testler o gövdeyi sabitliyor.
/// </summary>
public class ContainerAppJobRunnerTests
{
    private static ContainerAppJobRunner Build() => new(
        new ServiceOptions
        {
            AppId = "123456",
            PrivateKeyPem = "pem",
            WebhookSecret = "secret",
            AzureOpenAiEndpoint = "https://example.openai.azure.com",
            AzureOpenAiKey = "key",
            AzureClientId = "client-id",
            AzureOpenAiDeployment = "gpt-4o",
            WatchedWorkflows = ["CI"],
            AzureSubscriptionId = "sub",
            AzureResourceGroup = "rg",
            FixJobName = "ci-agent-fix",
            FixJobImage = "acr.azurecr.io/ci-agent:v1"
        },
        NullLogger<ContainerAppJobRunner>.Instance);

    private static readonly FixJob Job = new(
        "delivery-1", "hakancebe", "ci-agent-pilot", 10, 555, "/fix", "OWNER", 999);

    private static JsonElement Serialize(object payload)
        => JsonDocument.Parse(JsonSerializer.Serialize(payload)).RootElement.Clone();

    private static JsonElement PatchedContainer() =>
        Serialize(Build().BuildJobPatch(Job))
            .GetProperty("properties").GetProperty("template")
            .GetProperty("containers")[0];

    [Fact]
    public void BuildJobPatch_TargetsJobTemplate()
    {
        // Job TANIMI güncelleniyor (properties.template), start override DEĞİL.
        // Sebep: override'da secretRef korunmuyor ve job'ın kendi env'i siliniyor.
        var payload = Serialize(Build().BuildJobPatch(Job));

        Assert.True(payload.GetProperty("properties").TryGetProperty("template", out _));
    }

    [Fact]
    public void BuildJobPatch_KeepsNameImageAndResources()
    {
        // PATCH containers dizisini komple değiştiriyor: kaynak ayarları
        // yazılmazsa varsayılana düşer ve build yetersiz kaynakta koşar.
        var container = PatchedContainer();

        Assert.Equal("ci-agent-fix", container.GetProperty("name").GetString());
        Assert.Equal("acr.azurecr.io/ci-agent:v1", container.GetProperty("image").GetString());
        Assert.Equal(2.0, container.GetProperty("resources").GetProperty("cpu").GetDouble());
        Assert.Equal("4Gi", container.GetProperty("resources").GetProperty("memory").GetString());
    }

    [Fact]
    public void BuildEnvironment_ResuppliesJobsOwnVariables()
    {
        // Override env'i EZDİĞİ için job tanımındaki değişkenler burada tekrar
        // verilmeli; verilmezse container "env var eksik" deyip hemen ölür.
        var names = Build().BuildEnvironment(Job)
            .Select(e => Serialize(e).GetProperty("name").GetString())
            .ToList();

        Assert.Contains("GITHUB_APP_ID", names);
        Assert.Contains("GITHUB_APP_PRIVATE_KEY", names);
        Assert.Contains("AZURE_OPENAI_ENDPOINT", names);
        Assert.Contains("AZURE_OPENAI_KEY", names);
        Assert.Contains("AZURE_OPENAI_DEPLOYMENT", names);
    }

    [Fact]
    public void BuildEnvironment_CarriesTargetForThisRun()
    {
        var env = Build().BuildEnvironment(Job)
            .Select(Serialize)
            .ToDictionary(
                e => e.GetProperty("name").GetString()!,
                e => e.TryGetProperty("value", out var v) ? v.GetString() : null);

        Assert.Equal("fix", env["CI_AGENT_MODE"]);
        Assert.Equal("true", env["CI_AGENT_CLONE"]);
        Assert.Equal("hakancebe", env["CI_AGENT_OWNER"]);
        Assert.Equal("ci-agent-pilot", env["CI_AGENT_REPO"]);
        Assert.Equal("10", env["CI_AGENT_PR_NUMBER"]);
        Assert.Equal("555", env["CI_AGENT_COMMENT_ID"]);
        Assert.Equal("/fix", env["CI_AGENT_COMMENT_BODY"]);
        Assert.Equal("OWNER", env["CI_AGENT_COMMENT_AUTHOR_ASSOCIATION"]);
        Assert.Equal("999", env["CI_AGENT_INSTALLATION_ID"]);
    }

    [Fact]
    public void BuildEnvironment_SecretsUseSecretRefNotPlainValue()
    {
        // Sırlar bu isteğin gövdesinde DÜZ METİN taşınmamalı: ARM çağrısı Azure
        // Activity Log'a kaydediliyor. secretRef ile yalnızca secret'ın ADI gidiyor.
        var env = Build().BuildEnvironment(Job).Select(Serialize).ToList();

        foreach (var name in new[] { "GITHUB_APP_PRIVATE_KEY", "AZURE_OPENAI_KEY" })
        {
            var entry = env.Single(e => e.GetProperty("name").GetString() == name);

            Assert.True(entry.TryGetProperty("secretRef", out var secretRef),
                $"{name} secretRef ile geçmeli");
            Assert.False(entry.TryGetProperty("value", out _),
                $"{name} düz değer olarak GÖNDERİLMEMELİ");
            Assert.False(string.IsNullOrWhiteSpace(secretRef.GetString()));
        }
    }

    [Fact]
    public void BuildEnvironment_OmitsOpenAiKeyUnderManagedIdentity()
    {
        // Kritik regresyon koruması: anahtarı koşulsuz eklemek, deploy onu
        // kaldırsa bile bir sonraki /fix'te geri koyar ve managed identity'ye
        // geçiş SESSİZCE geri alınırdı.
        var runner = new ContainerAppJobRunner(
            new ServiceOptions
            {
                AppId = "123456", PrivateKeyPem = "pem", WebhookSecret = "secret",
                AzureOpenAiEndpoint = "https://example.openai.azure.com",
                AzureOpenAiKey = null,               // → managed identity
                AzureOpenAiDeployment = "gpt-4o",
                WatchedWorkflows = ["CI"],
                AzureSubscriptionId = "sub", AzureResourceGroup = "rg",
                FixJobName = "ci-agent-fix", FixJobImage = "acr.azurecr.io/ci-agent:v1",
                AzureClientId = "client-id"
            },
            NullLogger<ContainerAppJobRunner>.Instance);

        var env = runner.BuildEnvironment(Job).Select(Serialize).ToList();
        var names = env.Select(e => e.GetProperty("name").GetString()).ToList();

        Assert.DoesNotContain("AZURE_OPENAI_KEY", names);
        // Kimlik client id'si şart: ACA'da birden fazla kimlik atanabiliyor.
        Assert.Contains("AZURE_CLIENT_ID", names);
    }

    [Fact]
    public void BuildEnvironment_ResuppliesAppInsightsConnectionStringWhenConfigured()
    {
        // Faz 3'te web servisine izleme eklenmişti, job'a HİÇ eklenmemişti —
        // /fix çalışmaları (ARM sorguları, GitHub App token üretimi, LLM
        // çağrıları) görünmüyordu. Burada da PATCH'in silmemesi için her
        // çalıştırmada yeniden verilmeli, tıpkı GITHUB_APP_ID gibi.
        var runner = new ContainerAppJobRunner(
            new ServiceOptions
            {
                AppId = "123456", PrivateKeyPem = "pem", WebhookSecret = "secret",
                AzureOpenAiEndpoint = "https://example.openai.azure.com",
                AzureOpenAiKey = "key", AzureOpenAiDeployment = "gpt-4o",
                WatchedWorkflows = ["CI"],
                AzureSubscriptionId = "sub", AzureResourceGroup = "rg",
                FixJobName = "ci-agent-fix", FixJobImage = "acr.azurecr.io/ci-agent:v1",
                AppInsightsConnectionString = "InstrumentationKey=abc123"
            },
            NullLogger<ContainerAppJobRunner>.Instance);

        var env = runner.BuildEnvironment(Job).Select(Serialize).ToList();
        var entry = env.SingleOrDefault(
            e => e.GetProperty("name").GetString() == "APPLICATIONINSIGHTS_CONNECTION_STRING");

        Assert.NotEqual(default, entry);
        Assert.Equal("InstrumentationKey=abc123", entry.GetProperty("value").GetString());
    }

    [Fact]
    public void BuildEnvironment_OmitsAppInsightsWhenNotConfigured()
    {
        // İzleme opsiyonel: yapılandırılmamışsa env'e boş/null bir değer
        // eklenmemeli, alan hiç görünmemeli.
        var names = Build().BuildEnvironment(Job)   // Build() AppInsightsConnectionString vermiyor
            .Select(e => Serialize(e).GetProperty("name").GetString())
            .ToList();

        Assert.DoesNotContain("APPLICATIONINSIGHTS_CONNECTION_STRING", names);
    }

    [Fact]
    public void BuildEnvironment_NoGitHubTokenIsPassed()
    {
        // Hazır bir installation token GEÇİRİLMİYOR; job kendi token'ını App
        // private key'iyle container içinde üretiyor.
        var names = Build().BuildEnvironment(Job)
            .Select(e => Serialize(e).GetProperty("name").GetString())
            .ToList();

        Assert.DoesNotContain("GITHUB_TOKEN", names);
    }

    [Theory]
    [InlineData("""{"name":"ci-agent-fix-abc123"}""", "ci-agent-fix-abc123")]
    [InlineData("""{"other":"x"}""", null)]
    [InlineData("gecersiz json", null)]
    public void ReadExecutionName_ParsesOrReturnsNull(string body, string? expected)
    {
        Assert.Equal(expected, ContainerAppJobRunner.ReadExecutionName(body));
    }

    [Theory]
    [InlineData("""{"properties":{"status":"Succeeded"}}""", "Succeeded")]
    [InlineData("""{"properties":{"status":"Running"}}""", "Running")]
    [InlineData("""{"properties":{}}""", "Unknown")]
    [InlineData("gecersiz json", "Unknown")]
    public void ReadStatus_ParsesOrFallsBackToUnknown(string body, string expected)
    {
        // "Unknown" bilinçli: durum okunamadığında job'ı bitmiş SAYMAK, yarıda
        // olan bir işi tamamlanmış göstermek olurdu.
        Assert.Equal(expected, ContainerAppJobRunner.ReadStatus(body));
    }
}
