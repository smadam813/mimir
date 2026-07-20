namespace Mimir.Contracts.Hooks;

/// <summary>
/// The answer to the synchronous SessionStart hook: the Episode is created or resumed and the
/// Brief comes back for the CLI to print. Empty until the Brief ticket fills it in (spec §7).
/// </summary>
public sealed record SessionStartReply
{
    /// <summary>The Brief to print at session start; empty means print nothing.</summary>
    public string Brief { get; init; } = "";
}
