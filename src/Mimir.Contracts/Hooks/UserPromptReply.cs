namespace Mimir.Contracts.Hooks;

/// <summary>
/// The answer to the single UserPromptSubmit round-trip (spec §4): the prompt Event is recorded and
/// any Prompt-lane injection comes back for the CLI to print. Most prompts inject nothing (§7).
/// </summary>
public sealed record UserPromptReply
{
    /// <summary>Text to add to the prompt's context; empty means inject nothing.</summary>
    public string Injection { get; init; } = "";
}
