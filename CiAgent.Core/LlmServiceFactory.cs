using Azure.Core;
using Azure.Identity;

namespace CiAgent.Core;

/// <summary>
/// Azure OpenAI'a hangi kimlikle bağlanılacağına karar verir.
///
/// Kural tek cümle: <b>anahtar verilmişse anahtar, verilmemişse managed identity.</b>
///
/// Bu, GitHub tarafındaki kararla aynı desen (bkz. <see cref="GitHubTokenSource"/>):
/// açık bir sır varsa o kullanılır, yoksa platformun kimliğine düşülür. Aynı deseni
/// iki yerde de kullanmak, "prod'da sır yok" ilkesini tek bir okunur kurala indiriyor.
///
/// Karar burada, ayrı ve test edilebilir bir yerde duruyor çünkü yanlış tarafa
/// düşmesi sessiz: anahtar sanılıp managed identity kullanılmazsa prod'da eski bir
/// anahtara bağlı kalınır, tersi olursa lokal geliştirme kimlik hatası verir.
/// </summary>
public static class LlmServiceFactory
{
    public static LlmService Create(
        string endpoint, string? apiKey, string deployment, string? managedIdentityClientId = null)
    {
        if (!string.IsNullOrWhiteSpace(apiKey))
            return new LlmService(endpoint, apiKey, deployment);

        return new LlmService(endpoint, CreateCredential(managedIdentityClientId), deployment);
    }

    /// <summary>Anahtar yoksa managed identity kullanılacak mı? (Loglamak için.)</summary>
    public static bool UsesManagedIdentity(string? apiKey) => string.IsNullOrWhiteSpace(apiKey);

    /// <summary>
    /// User-assigned managed identity'nin client id'si veriliyor: ACA'da birden
    /// fazla kimlik atanmış olabilir ve hangisinin kullanılacağı belirsiz kalırsa
    /// DefaultAzureCredential yanlış olanı seçip anlaşılmaz bir 403 üretir.
    /// </summary>
    private static TokenCredential CreateCredential(string? clientId) =>
        new DefaultAzureCredential(new DefaultAzureCredentialOptions
        {
            ManagedIdentityClientId = clientId
        });
}
