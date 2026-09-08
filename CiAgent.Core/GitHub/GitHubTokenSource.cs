namespace CiAgent.Core;

/// <summary>GitHub App kimliğiyle token üretmek için gereken üçlü.</summary>
public sealed record AppCredentials(string AppId, string PrivateKeyPem, long InstallationId);

/// <summary>
/// Agent GitHub'a hangi kimlikle bağlanacak?
///
/// İki kaynak var ve hangisinin kullanılacağı çalıştığı yere göre değişiyor:
///
///   GITHUB_TOKEN doğrudan verilmiş  → onu kullan
///     (Actions'ın job token'ı, lokal geliştirmedeki PAT)
///
///   verilmemiş ama App kimliği var  → installation token üret
///     (Container Apps Job: hazır token GEÇİRİLMİYOR, çünkü ARM API çağrısının
///      gövdesinde ve execution'ın env var listesinde görünür olurdu)
///
/// Bu karar ayrı ve saf bir yerde duruyor çünkü Program.cs'in içinde test
/// edilemiyordu: yapılandırma zinciri user secrets'ı da okuduğu için lokalde
/// "App yolu" hiç tetiklenmiyor, yani hata ancak container'da ortaya çıkardı.
/// </summary>
public static class GitHubTokenSource
{
    /// <summary>
    /// App kimlik bilgileri eksiksiz mi? Üçünden biri bile eksikse null döner —
    /// yarım yapılandırmayla token üretmeye çalışıp anlamsız bir 401 almaktansa,
    /// çağıran tarafın "GITHUB_TOKEN eksik" demesi daha anlaşılır.
    /// </summary>
    public static AppCredentials? TryReadAppCredentials(
        string? appId, string? privateKeyPem, string? installationIdRaw)
    {
        if (string.IsNullOrWhiteSpace(appId) || string.IsNullOrWhiteSpace(privateKeyPem))
            return null;

        if (!long.TryParse(installationIdRaw, out var installationId) || installationId <= 0)
            return null;

        return new AppCredentials(appId, NormalizePem(privateKeyPem), installationId);
    }

    /// <summary>
    /// PEM'i kullanılabilir hale getirir.
    ///
    /// ACA/Docker env var'larında çok satırlı değer taşımak zor olduğu için private
    /// key genelde satır sonları "\n" olarak KAÇIRILMIŞ şekilde geliyor. Bu haliyle
    /// RSA.ImportFromPem'e verilirse "geçersiz PEM" hatası alınır — üstelik hata
    /// mesajı sebebi söylemez.
    /// </summary>
    public static string NormalizePem(string pem)
        => pem.Contains("\\n") ? pem.Replace("\\n", "\n") : pem;
}
