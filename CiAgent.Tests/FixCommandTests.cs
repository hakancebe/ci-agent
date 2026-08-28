using CiAgent.Core;

namespace CiAgent.Tests;

public class FixCommandTests
{
    [Theory]
    [InlineData("/fix")]
    [InlineData("/fix ")]
    [InlineData("  /fix  ")]
    [InlineData("/FIX")]
    [InlineData("/fix\nikinci satır açıklama")]
    public void TryParse_RecognisesCommand(string body)
    {
        var cmd = FixCommand.TryParse(body);

        Assert.NotNull(cmd);
        Assert.False(cmd!.DryRun);
    }

    [Fact]
    public void TryParse_ReadsDryRunFlag()
    {
        Assert.True(FixCommand.TryParse("/fix --dry-run")!.DryRun);
    }

    [Theory]
    [InlineData("bence burada /fix çalıştırmalıyız")]
    [InlineData("```\n/fix\n```")]
    [InlineData("> /fix")]
    [InlineData("/fixture bir şey")]
    [InlineData("normal bir yorum")]
    [InlineData("")]
    [InlineData(null)]
    public void TryParse_IgnoresMentionsThatAreNotCommands(string? body)
    {
        // Kod bloğunda ya da cümle içinde geçen /fix agent'ı tetiklememeli.
        Assert.Null(FixCommand.TryParse(body));
    }

    [Theory]
    [InlineData("OWNER")]
    [InlineData("MEMBER")]
    [InlineData("COLLABORATOR")]
    [InlineData("collaborator")]
    public void CanRunFix_AllowsUsersWithWriteAccess(string association)
    {
        Assert.True(FixAuthorization.CanRunFix(association));
    }

    [Theory]
    [InlineData("CONTRIBUTOR")]
    [InlineData("FIRST_TIME_CONTRIBUTOR")]
    [InlineData("NONE")]
    [InlineData("")]
    [InlineData(null)]
    public void CanRunFix_BlocksEveryoneElse(string? association)
    {
        // Yabancı biri yorum yazarak agent'a kod değiştirtemesin.
        Assert.False(FixAuthorization.CanRunFix(association));
    }
}
