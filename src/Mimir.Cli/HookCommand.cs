using System.Net.Http.Json;
using System.Text.Json;
using Mimir.Contracts.Hooks;

namespace Mimir.Cli;

internal sealed class HookCommand(HttpClient http, TextReader input, TextWriter output, TimeSpan? cap = null)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<int> RunAsync(string hookEvent)
    {
        try
        {
            using var timeout = new CancellationTokenSource(cap ?? HookLimits.RoundTripCap);
            await RelayAsync(hookEvent, timeout.Token);
        }
        catch (Exception)
        {
            // Swallowed deliberately: every failure here is the same silent non-answer.
        }

        return 0;
    }

    private async Task RelayAsync(string hookEvent, CancellationToken cancellationToken)
    {
        var stdin = await Task.Run(input.ReadToEnd).WaitAsync(cancellationToken);
        using var document = JsonDocument.Parse(stdin);

        var sessionId = document.RootElement.GetProperty("session_id").GetString()
            ?? throw new JsonException("session_id is null");

        var cwd = document.RootElement.TryGetProperty("cwd", out var cwdProperty)
            && cwdProperty.GetString() is { Length: > 0 } reported
            ? reported
            : Environment.CurrentDirectory;

        var location = await ProjectLocator.LocateAsync(cwd, cancellationToken);
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
