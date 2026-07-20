namespace Mimir.Contracts.Hooks;

/// <summary>
/// The Claude Code hook events the CLI relays (spec §4 table). SessionStart and SessionEnd are not
/// Event types — the §3 Event enum is closed — but they are hook events: SessionStart
/// creates/resumes the Episode, SessionEnd Seals it.
/// </summary>
public static class HookEvents
{
    public const string SessionStart = "SessionStart";
    public const string UserPromptSubmit = "UserPromptSubmit";
    public const string PostToolUse = "PostToolUse";
    public const string Stop = "Stop";
    public const string SessionEnd = "SessionEnd";
}
