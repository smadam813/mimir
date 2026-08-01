using System.Text.Json;

namespace Mimir.Server.Capture;

internal static class HookPayload
{
    public static string? StringProperty(this JsonElement payload, string name)
        => payload.ValueKind == JsonValueKind.Object
            && payload.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
