namespace Mimir.Contracts.Hooks;

public static class HookEvents
{
    public const string SessionStart = "SessionStart";
    public const string UserPromptSubmit = "UserPromptSubmit";
    public const string PostToolUse = "PostToolUse";
    public const string Stop = "Stop";
    public const string SessionEnd = "SessionEnd";
}
