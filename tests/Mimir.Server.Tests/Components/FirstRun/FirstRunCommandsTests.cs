using Mimir.Server.Components.FirstRun;

namespace Mimir.Server.Tests.Components.FirstRun;

/// <summary>
/// The two registrations the first-run body hands out (#90), pinned with no database and no
/// rendered markup: the panel renders these same constants, so what the clipboard carries is what
/// is on the screen. Assertions are per line, never whole-string equality — a raw string literal
/// carries the file's line endings, which are CRLF on a Windows checkout and LF on CI's.
/// </summary>
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
        // The whole registration object, not just the absence of one spelling of `"async": true`:
        // a version of that assertion that named the key and the command in one order stayed green
        // when they were reordered, which would have silenced the Brief and the Prompt-lane
        // injection — both are printed into the session and so must be synchronous (spec §4).
        => FirstRunCommands.Hooks.ShouldContain(
            $$"""{ "hooks": [{ "type": "command", "command": "mimir hook {{hook}}" }] }""");

    [Fact]
    public void OnlyTheThreeFireAndForgetHooks_AreAsync()
    {
        var asyncFlags = FirstRunCommands.Hooks.Split("\"async\": true").Length - 1;

        // PostToolUse, Stop and SessionEnd — a fourth anywhere in the block fails this, whatever
        // order its keys are written in.
        asyncFlags.ShouldBe(3);
    }

    [Fact]
    public void TheMcpRegistration_IsUserScoped()
        => FirstRunCommands.Mcp.ShouldBe("claude mcp add --scope user mimir -- mimir mcp");

    [Fact]
    public void TheRegistrations_AreTheOnesTheReadmeStates()
    {
        // README.md restates both for a reader who never opens the app; FirstRunCommands' own
        // <remarks> says to change them together. This is what makes that more than a promise —
        // the same shape AppSettingsTests uses for appsettings.json against the §11 defaults.
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
