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

    /// <summary>
    /// Matches the exact marker <see cref="Capture.PayloadTruncator"/> writes (§4). The marker is
    /// deliberately the detector: comparing <c>payload_full_size</c> against the stored bytes is
    /// less honest — re-encoding legally shifts escaping, and a small cut plus the marker can even
    /// grow the payload. A stored payload whose own text contains the marker pattern is wrongly
    /// badged; accepted.
    /// </summary>
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
