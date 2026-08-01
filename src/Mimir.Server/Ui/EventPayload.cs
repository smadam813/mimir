using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Mimir.Server.Ui;

public static partial class EventPayload
{
    private static readonly JsonSerializerOptions Indented = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Detects by marker, not by size: a payload carrying the pattern is wrongly badged, accepted.</summary>
    [GeneratedRegex(@"…\[truncated \d+ bytes\]…")]
    private static partial Regex TruncationMarker();

    public static bool IsTruncated(string payloadJson) => TruncationMarker().IsMatch(payloadJson);

    public static string Pretty(string payloadJson)
    {
        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            return JsonSerializer.Serialize(document, Indented);
        }
        catch (JsonException)
        {
            return payloadJson;
        }
    }
}
