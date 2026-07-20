using System.Net.Http.Json;
using System.Text.Json;
using Mimir.Contracts.Hooks;

namespace Mimir.Cli;

/// <summary>
/// <c>mimir hook &lt;event&gt;</c> (spec §4): relay the hook's stdin JSON to Mimir with host-resolved
/// Project identity. Synchronous hooks print what the server returns; capture hooks print nothing.
/// Everything fails open — 3 s cap, exit 0 on every path, because a dead Mimir must never break or
/// slow the session that invoked it.
/// </summary>
internal sealed class HookCommand(HttpClient http, TextReader input, TextWriter output)
{
    /// <summary>Spec §11: the hard cap on any hook's round-trip.</summary>
    public static readonly TimeSpan Cap = TimeSpan.FromSeconds(3);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<int> RunAsync(string hookEvent)
    {
        try
        {
            using var cap = new CancellationTokenSource(Cap);
            await RelayAsync(hookEvent, cap.Token);
        }
        catch (Exception)
        {
            // Fail open (spec §4): no server, slow server, bad stdin — all the same non-answer.
        }

        return 0;
    }

    private async Task RelayAsync(string hookEvent, CancellationToken cancellationToken)
    {
        var stdin = await input.ReadToEndAsync(cancellationToken);
        using var document = JsonDocument.Parse(stdin);

        // No session id means nothing to attach an Episode to; stay silent rather than guess.
        var sessionId = document.RootElement.GetProperty("session_id").GetString()
            ?? throw new JsonException("session_id is null");

        var cwd = document.RootElement.TryGetProperty("cwd", out var cwdProperty)
            && cwdProperty.GetString() is { Length: > 0 } reported
            ? reported
            : Environment.CurrentDirectory;

        var location = ProjectLocator.Locate(cwd);
        var request = new HookEventRequest
        {
            SessionId = sessionId,
            Cwd = cwd,
            ProjectIdentity = location.Identity,
            ProjectRoot = location.Root,
            HookEvent = hookEvent,
            Payload = document.RootElement.Clone(),
        };

        switch (hookEvent)
        {
            case HookEvents.SessionStart:
                var started = await PostAsync<SessionStartReply>("api/hooks/session-start", request, cancellationToken);
                Print(started?.Brief);
                break;

            case HookEvents.UserPromptSubmit:
                var replied = await PostAsync<UserPromptReply>("api/hooks/user-prompt", request, cancellationToken);
                Print(replied?.Injection);
                break;

            case HookEvents.PostToolUse or HookEvents.Stop or HookEvents.SessionEnd:
                (await http.PostAsJsonAsync("api/capture/events", request, Json, cancellationToken)).Dispose();
                break;

            default:
                // An event this build does not know. Relaying it would make the server guess;
                // dropping it keeps capture honest and the session unbothered.
                break;
        }
    }

    private async Task<TReply?> PostAsync<TReply>(
        string route,
        HookEventRequest request,
        CancellationToken cancellationToken)
        where TReply : class
    {
        using var response = await http.PostAsJsonAsync(route, request, Json, cancellationToken);
        return response.IsSuccessStatusCode
            ? await response.Content.ReadFromJsonAsync<TReply>(Json, cancellationToken)
            : null;
    }

    private void Print(string? text)
    {
        if (!string.IsNullOrEmpty(text))
        {
            output.WriteLine(text);
        }
    }
}
