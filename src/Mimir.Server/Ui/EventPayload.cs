using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Mimir.Server.Ui;

/// <summary>
/// Presentation of a stored Event payload (spec §8.2): indented for reading, with the §4
/// truncation marker kept visible — the marker is the honesty of the record.
/// </summary>
public static partial class EventPayload
{
    // Relaxed escaping keeps "…[truncated N bytes]…" as written instead of … escapes. The
    // output lands in Blazor render output, which encodes for HTML itself, so this is safe here.
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
            // Stored payloads are always jsonb, but a renderer must never be the thing that dies.
            return payloadJson;
        }
    }
}
