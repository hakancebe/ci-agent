using CiAgent.Core;

namespace CiAgent.Tests;

/// <summary>
/// "Anahtarla mı, managed identity ile mi?" kararı.
///
/// Bu kararın yanlış tarafa düşmesi SESSİZ bir hata: anahtar varsayılıp managed
/// identity'ye geçilmezse prod eski bir anahtara bağlı kalır — ki bu projede tam
/// olarak o yaşandı, eski bir anahtar deploy edilip hem web servisini hem /fix
/// job'ını bozdu ve hata saatler sonra ortaya çıktı.
/// </summary>
public class LlmServiceFactoryTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void UsesManagedIdentity_WhenKeyMissing(string? apiKey)
    {
        Assert.True(LlmServiceFactory.UsesManagedIdentity(apiKey));
    }

    [Fact]
    public void UsesApiKey_WhenKeyProvided()
    {
        Assert.False(LlmServiceFactory.UsesManagedIdentity("sk-gercek-bir-anahtar"));
    }

    [Fact]
    public void Create_WithKey_BuildsService()
    {
        // Ağa çıkmıyor: yalnızca istemcinin kurulabildiğini doğruluyor.
        var llm = LlmServiceFactory.Create(
            "https://example.openai.azure.com/openai/v1/", "bir-anahtar", "gpt-4o");

        Assert.NotNull(llm);
    }

    [Fact]
    public void Create_WithoutKey_BuildsManagedIdentityService()
    {
        // DefaultAzureCredential kurulurken token İSTEMİYOR (ilk istekte istiyor),
        // bu yüzden bu test Azure'a hiç dokunmadan kurulum yolunu doğrulayabiliyor.
        var llm = LlmServiceFactory.Create(
            "https://example.openai.azure.com/openai/v1/", null, "gpt-4o", "client-id");

        Assert.NotNull(llm);
    }

    [Fact]
    public void Create_TreatsWhitespaceKeyAsMissing()
    {
        // Boş string'i "anahtar var" saymak, ACA'da tanımlı ama boş bırakılmış bir
        // env var'ın managed identity'yi sessizce devre dışı bırakması demekti.
        var llm = LlmServiceFactory.Create(
            "https://example.openai.azure.com/openai/v1/", "   ", "gpt-4o", "client-id");

        Assert.NotNull(llm);
    }
}
