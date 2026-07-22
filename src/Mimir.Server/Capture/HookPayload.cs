using System.Text.Json;

namespace Mimir.Server.Capture;

/// <summary>
/// Field access over the §4 payload — the hook's untouched stdin JSON. Capture is dumb, so
/// readers pull single fields defensively: absent, null, non-object, or non-string all read as
/// null rather than throwing on a malformed hook.
/// </summary>
internal static class HookPayload
{
    public static string? StringProperty(this JsonElement payload, string name)
        => payload.ValueKind == JsonValueKind.Object
            && payload.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
