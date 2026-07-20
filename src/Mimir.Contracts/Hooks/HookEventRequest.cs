using System.Text.Json;

namespace Mimir.Contracts.Hooks;

/// <summary>
/// One relayed hook occurrence, POSTed by <c>mimir hook</c>. Identity and root travel with every
/// request because resolution is host-side (spec §3.1) — the server never runs git.
/// </summary>
public sealed record HookEventRequest
{
    /// <summary>Claude Code's session id; the Episode key (one Episode per session, ADR-0003).</summary>
    public required string SessionId { get; init; }

    /// <summary>The session's working directory as reported by the hook.</summary>
    public required string Cwd { get; init; }

    /// <summary>Normalized <c>host/owner/repo</c>, or an absolute path for non-repos (spec §3.1).</summary>
    public required string ProjectIdentity { get; init; }

    /// <summary>The repo root when in a repository, else the cwd. Always absolute.</summary>
    public required string ProjectRoot { get; init; }

    /// <summary>Which hook fired — one of <see cref="HookEvents"/>.</summary>
    public required string HookEvent { get; init; }

    /// <summary>The hook's full stdin JSON, untouched. Capture is dumb; the server truncates.</summary>
    public JsonElement Payload { get; init; }
}
