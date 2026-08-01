using Mimir.Server.Components.FirstRun;

namespace Mimir.Server.Tests.Components.FirstRun;

public class FirstRunCommandsTests
{
    [Theory]
    [InlineData("SessionStart")]
    [InlineData("UserPromptSubmit")]
    [InlineData("PostToolUse")]
    [InlineData("Stop")]
    [InlineData("SessionEnd")]
    public void TheHookBlock_RegistersEverySpec4Hook(string hook)
        => FirstRunCommands.Hooks.ShouldContain($"\"command\": \"mimir hook {hook}\"");

    [Theory]
    [InlineData("SessionStart")]
    [InlineData("UserPromptSubmit")]
    public void TheSynchronousHooks_CarryNoAsyncFlagAtAll(string hook)
        => FirstRunCommands.Hooks.ShouldContain(
            $$"""{ "hooks": [{ "type": "command", "command": "mimir hook {{hook}}" }] }""");

    [Fact]
    public void OnlyTheThreeFireAndForgetHooks_AreAsync()
    {
        var asyncFlags = FirstRunCommands.Hooks.Split("\"async\": true").Length - 1;

        asyncFlags.ShouldBe(3);
    }

    [Fact]
    public void TheMcpRegistration_IsUserScoped()
        => FirstRunCommands.Mcp.ShouldBe("claude mcp add --scope user mimir -- mimir mcp");

    [Fact]
    public void TheRegistrations_AreTheOnesTheReadmeStates()
    {
        var readme = File.ReadAllText("README.md");

        readme.ShouldContain(FirstRunCommands.Hooks);
        readme.ShouldContain(FirstRunCommands.Mcp);
    }

    [Fact]
    public void OneCopy_CarriesBothRegistrations_AndSaysWhichIsWhich()
    {
        var copied = FirstRunCommands.Both;

        copied.ShouldContain(FirstRunCommands.Hooks);
        copied.ShouldContain(FirstRunCommands.Mcp);
        copied.ShouldContain("~/.claude/settings.json");
    }
}
