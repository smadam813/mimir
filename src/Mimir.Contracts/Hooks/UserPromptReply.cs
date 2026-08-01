namespace Mimir.Contracts.Hooks;

public sealed record UserPromptReply
{
    public string Injection { get; init; } = "";
}
