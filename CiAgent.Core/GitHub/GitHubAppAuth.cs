using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CiAgent.Core;

/// <summary>Bir installation için üretilmiş, süreli erişim token'ı.</summary>
public sealed record InstallationToken(string Token, DateTimeOffset ExpiresAt);

/// <summary>
/// GitHub App kimlik doğrulaması: private key ile App JWT üretir, onu installation
/// token'a çevirir.
///
/// Neden iki aşama?
///   App JWT (RS256, max 10 dk) App'in KENDİSİNİ temsil eder ve tek işe yarar:
///   "şu installation için bana token ver" demek. Repo'ya dokunan bütün çağrılar
///   installation token'ı ile yapılır — 1 saat ömürlü ve App'in yalnızca o
///   installation'daki izinleriyle sınırlı. PAT'in aksine sızdığında hasar
///   penceresi bir saatle kapalı.
///
/// Token'lar installation başına cache'leniyor: her webhook olayında yeniden token
/// basmak hem gereksiz bir round-trip hem de GitHub'ın rate limit'ini boşa harcamak
/// olurdu.
/// </summary>
public sealed class GitHubAppAuth
{
    // Token'ı son saniyesine kadar kullanmıyoruz: elimizdeki token geçerliyken
    // başlayan bir işin ORTASINDA süresi dolabilir. Bu pay, uzun süren bir /fix
    // çalışmasının yarıda 401 almasını engelliyor.
    private static readonly TimeSpan RefreshMargin = TimeSpan.FromMinutes(5);

    // GitHub, iat'ı gelecekte olan JWT'yi reddediyor. Sunucu saatimiz GitHub'ınkinden
    // birkaç saniye ileriyse token üretimi sebepsiz patlardı; iat'ı 60 sn geri alarak
    // saat kaymasına tolerans bırakıyoruz (GitHub'ın kendi dokümanının önerisi).
    private static readonly TimeSpan ClockSkew = TimeSpan.FromSeconds(60);

    // JWT ömrü. GitHub üst sınırı 10 dk (600 sn) ve pencere iat→exp olarak ölçülüyor.
    // iat zaten 60 sn geriye alındığı için 9 dk seçmek pencereyi TAM 600 sn yapardı,
    // yani sınırın bir saniye altında değil, tam üstünde. 8 dk ile pencere 540 sn:
    // sınırın rahatça altında, ve aynı JWT hâlâ arka arkaya birkaç token değişiminde
    // kullanılabiliyor.
    private static readonly TimeSpan JwtLifetime = TimeSpan.FromMinutes(8);

    private readonly string _appId;
    private readonly string _privateKeyPem;
    private readonly HttpClient _http;
    private readonly ConcurrentDictionary<long, InstallationToken> _cache = new();

    /// <param name="privateKeyPem">
    /// App sayfasından inen .pem dosyasının İÇERİĞİ (dosya yolu değil). GitHub
    /// PKCS#1 formatında veriyor ("BEGIN RSA PRIVATE KEY"); ImportFromPem hem onu
    /// hem PKCS#8'i ("BEGIN PRIVATE KEY") tanıdığı için ikisi de kabul edilir.
    /// </param>
    /// <param name="httpClient">
    /// Testlerde sahte bir handler enjekte edilebilsin diye dışarıdan alınıyor.
    /// Verilmezse servis ömrü boyunca yaşayan tek bir HttpClient kurulur.
    /// </param>
    public GitHubAppAuth(string appId, string privateKeyPem, HttpClient? httpClient = null)
    {
        if (string.IsNullOrWhiteSpace(appId))
            throw new ArgumentException("App ID boş olamaz.", nameof(appId));
        if (string.IsNullOrWhiteSpace(privateKeyPem))
            throw new ArgumentException("Private key boş olamaz.", nameof(privateKeyPem));

        _appId = appId;
        _privateKeyPem = privateKeyPem;
        _http = httpClient ?? new HttpClient { BaseAddress = new Uri("https://api.github.com/") };

        if (_http.BaseAddress is null)
            _http.BaseAddress = new Uri("https://api.github.com/");

        if (_http.DefaultRequestHeaders.UserAgent.Count == 0)
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("ci-agent");
    }

    /// <summary>
    /// Installation token'ı döndürür. Cache'te geçerli (ve yakında dolmayacak) bir
    /// token varsa onu, yoksa yenisini üretir.
    /// </summary>
    public async Task<string> GetInstallationTokenAsync(
        long installationId, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;

        if (_cache.TryGetValue(installationId, out var cached)
            && cached.ExpiresAt - now > RefreshMargin)
        {
            return cached.Token;
        }

        var fresh = await FetchInstallationTokenAsync(installationId, now, ct);

        // Yarış durumunda (aynı installation için iki olay aynı anda) ikisi de token
        // üretebilir; ikisi de geçerli olduğu için son yazan kazanır, sorun olmaz.
        _cache[installationId] = fresh;
        return fresh.Token;
    }

    private async Task<InstallationToken> FetchInstallationTokenAsync(
        long installationId, DateTimeOffset now, CancellationToken ct)
    {
        var jwt = CreateAppJwt(_appId, _privateKeyPem, now);

        using var request = new HttpRequestMessage(
            HttpMethod.Post, $"app/installations/{installationId}/access_tokens");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", jwt);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");

        using var response = await _http.SendAsync(request, ct);

        if (!response.IsSuccessStatusCode)
        {
            // Gövde teşhis için şart: 401 "private key yanlış", 404 "installation ID
            // yanlış ya da App kaldırılmış" demek ve ikisi ayırt edilemezse bu hatayı
            // ayıklamak saatler alır.
            var body = await response.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException(
                $"Installation token alınamadı (installation {installationId}): "
                + $"{(int)response.StatusCode} {response.ReasonPhrase} — {Masker.Mask(body)}");
        }

        var payload = await response.Content.ReadFromJsonAsync<AccessTokenResponse>(ct);

        if (payload is null || string.IsNullOrWhiteSpace(payload.Token))
            throw new InvalidOperationException(
                $"Installation token yanıtı beklenen şekilde değil (installation {installationId}).");

        return new InstallationToken(payload.Token, payload.ExpiresAt);
    }

    /// <summary>
    /// App'i temsil eden RS256 imzalı JWT üretir. Saf fonksiyon: ağa çıkmaz, saati
    /// dışarıdan alır — bu sayede testte sabit bir zamanla doğrulanabiliyor.
    /// </summary>
    internal static string CreateAppJwt(string appId, string privateKeyPem, DateTimeOffset now)
    {
        var issuedAt = now - ClockSkew;
        var expiresAt = now + JwtLifetime;

        var header = new Dictionary<string, object> { ["alg"] = "RS256", ["typ"] = "JWT" };
        var claims = new Dictionary<string, object>
        {
            ["iat"] = issuedAt.ToUnixTimeSeconds(),
            ["exp"] = expiresAt.ToUnixTimeSeconds(),

            // App ID sayısalsa SAYI olarak gönderiliyor: GitHub'ın dokümanındaki
            // örnek iss'i sayı olarak veriyor ve string kabul edilip edilmediği
            // sürüme göre değişebiliyor. Sayısal değilse (yeni Client ID formatı)
            // string olarak gidiyor.
            ["iss"] = long.TryParse(appId, out var numericAppId) ? numericAppId : appId
        };

        var signingInput =
            $"{Base64Url(JsonSerializer.SerializeToUtf8Bytes(header))}."
          + $"{Base64Url(JsonSerializer.SerializeToUtf8Bytes(claims))}";

        using var rsa = RSA.Create();
        rsa.ImportFromPem(privateKeyPem);

        var signature = rsa.SignData(
            Encoding.ASCII.GetBytes(signingInput),
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        return $"{signingInput}.{Base64Url(signature)}";
    }

    /// <summary>
    /// JWT'nin kullandığı base64url: standart base64'ten farkı '+' ve '/' yerine
    /// '-' ve '_', ve sondaki '=' dolgusunun atılması (URL'de anlam taşıdıkları için).
    /// </summary>
    private static string Base64Url(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private sealed class AccessTokenResponse
    {
        [JsonPropertyName("token")]
        public string Token { get; set; } = "";

        [JsonPropertyName("expires_at")]
        public DateTimeOffset ExpiresAt { get; set; }
    }
}
