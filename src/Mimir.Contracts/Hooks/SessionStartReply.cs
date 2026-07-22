namespace Mimir.Contracts.Hooks;

/// <summary>
/// The answer to the synchronous SessionStart hook: the Episode is created or resumed and the
/// Brief (spec §7) comes back for the CLI to print.
/// </summary>
public sealed record SessionStartReply
{
    /// <summary>The Brief to print at session start; empty means print nothing.</summary>
    public string Brief { get; init; } = "";
}
