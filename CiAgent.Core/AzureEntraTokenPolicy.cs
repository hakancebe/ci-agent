using System.ClientModel.Primitives;
using Azure.Core;

namespace CiAgent.Core;

/// <summary>
/// Azure OpenAI isteklerine API anahtarı yerine Entra ID (AAD) token'ı takar.
///
/// Neden bir policy, ayrı bir SDK değil?
///   Azure.AI.OpenAI paketi bu işi kutudan yapıyor ama URL'i kendi kuruyor;
///   bizim endpoint'imiz ".../openai/v1/" biçiminde ve yolu kendi ekleyen bir
///   istemciye verilirse yol iki kez eklenir. Çalışan yapılandırmayı bozmamak
///   için düz OpenAI SDK'sı korunuyor, yalnızca kimlik doğrulama başlığı
///   değiştiriliyor.
///
/// İşin püf noktası: API anahtarı da AAD token'ı da AYNI başlıkla gidiyor
/// ("Authorization: Bearer ..."). Yani değişen tek şey o başlığın içeriği.
///
/// Kazanç: prod'da AZURE_OPENAI_KEY diye bir sır KALMIYOR. Bu, tam olarak bu
/// projede canlıda yaşanan hatayı imkânsız kılıyor — eski bir anahtarın sessizce
/// deploy edilip servisi bozması. Token her seferinde platformdan alınıyor,
/// saklanacak, kopyalanacak, eskiyecek bir değer yok.
/// </summary>
internal sealed class AzureEntraTokenPolicy : PipelinePolicy
{
    // Cognitive Services / AI Services kaynaklarının veri düzlemi kapsamı.
    private static readonly string[] Scopes = ["https://cognitiveservices.azure.com/.default"];

    // Token'ı son saniyesine kadar kullanmıyoruz: elimizdeki token geçerliyken
    // başlayan bir istek, sunucuya vardığında süresi dolmuş olabilir.
    private static readonly TimeSpan RefreshMargin = TimeSpan.FromMinutes(5);

    private readonly TokenCredential _credential;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private AccessToken _token;

    public AzureEntraTokenPolicy(TokenCredential credential) => _credential = credential;

    public override void Process(
        PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
    {
        // Senkron yol yalnızca SDK'nın senkron API'si çağrılırsa işler; bu kod
        // tabanı her yerde async kullanıyor, yine de sözleşme gereği doldurulmalı.
        ApplyTokenAsync(message, CancellationToken.None).AsTask().GetAwaiter().GetResult();
        ProcessNext(message, pipeline, currentIndex);
    }

    public override async ValueTask ProcessAsync(
        PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
    {
        await ApplyTokenAsync(message, message.CancellationToken);
        await ProcessNextAsync(message, pipeline, currentIndex);
    }

    private async ValueTask ApplyTokenAsync(PipelineMessage message, CancellationToken ct)
    {
        var token = await GetTokenAsync(ct);

        // Set (Add değil): OpenAI SDK'sı ApiKeyCredential'dan gelen başlığı zaten
        // koymuş oluyor, onun ÜZERİNE yazmamız gerekiyor.
        message.Request?.Headers.Set("Authorization", $"Bearer {token}");
    }

    private async ValueTask<string> GetTokenAsync(CancellationToken ct)
    {
        if (_token.ExpiresOn - DateTimeOffset.UtcNow > RefreshMargin)
            return _token.Token;

        await _lock.WaitAsync(ct);
        try
        {
            // Kilidi bekleyen ikinci çağrı için tekrar kontrol: ilk çağrı token'ı
            // çoktan tazelemiş olabilir, ikinci kez almak boşuna istek olurdu.
            if (_token.ExpiresOn - DateTimeOffset.UtcNow > RefreshMargin)
                return _token.Token;

            _token = await _credential.GetTokenAsync(new TokenRequestContext(Scopes), ct);
            return _token.Token;
        }
        finally
        {
            _lock.Release();
        }
    }
}
