using CiAgent.Core;

namespace CiAgent.Tests;

public class MaskerTests
{
    [Fact]
    public void Mask_MasksGitHubToken()
    {
        var input = "token=ghp_ABCDEFGHIJKLMNOPQRSTUVWX1234";
        var result = Masker.Mask(input);

        Assert.Contains("***GITHUB_TOKEN***", result);
        Assert.DoesNotContain("ghp_ABCDEFGHIJKLMNOPQRSTUVWX1234", result);
    }

    [Fact]
    public void Mask_MasksAwsKey()
    {
        var input = "aws_access_key_id = AKIAIOSFODNN7EXAMPLE";
        var result = Masker.Mask(input);

        Assert.Contains("***AWS_KEY***", result);
        Assert.DoesNotContain("AKIAIOSFODNN7EXAMPLE", result);
    }

    [Fact]
    public void Mask_MasksBearerHeader()
    {
        var input = "Authorization: Bearer eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.payload";
        var result = Masker.Mask(input);

        Assert.Contains("Bearer ***", result);
        Assert.DoesNotContain("eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9", result);
    }

    [Fact]
    public void Mask_MasksEmail()
    {
        var input = "İletişim: user@example.com adresinden ulaşabilirsiniz.";
        var result = Masker.Mask(input);

        Assert.Contains("***EMAIL***", result);
        Assert.DoesNotContain("user@example.com", result);
    }

    [Fact]
    public void Mask_DoesNotReMaskAlreadyMaskedValue()
    {
        // Zaten maskelenmiş bir alan (örn. önceki bir kural tarafından ***GITHUB_TOKEN*** yapılmış)
        // generic "token=" kuralı tarafından tekrar maskelenip ***'a indirgenmemeli.
        var input = "token=***GITHUB_TOKEN***";
        var result = Masker.Mask(input);

        Assert.Equal("token=***GITHUB_TOKEN***", result);
    }

    [Fact]
    public void Mask_MasksGenericSecretKeyValue()
    {
        var input = "password: SuperSecret123!";
        var result = Masker.Mask(input);

        Assert.Contains("password=***", result);
        Assert.DoesNotContain("SuperSecret123!", result);
    }

    [Fact]
    public void Mask_ReturnsEmptyStringForNullOrEmptyInput()
    {
        Assert.Equal(string.Empty, Masker.Mask(null));
        Assert.Equal(string.Empty, Masker.Mask(string.Empty));
    }
}
