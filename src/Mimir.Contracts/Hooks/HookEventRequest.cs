using System.Text.Json;

namespace Mimir.Contracts.Hooks;

public sealed record HookEventRequest
{
    public required string SessionId { get; init; }

    public required string Cwd { get; init; }

    public required string ProjectIdentity { get; init; }

    public required string ProjectRoot { get; init; }

    public required string HookEvent { get; init; }

    public JsonElement Payload { get; init; }
}
