using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Mimir.Server.Configuration;

namespace Mimir.Server.Capture;

/// <summary>An Event payload ready to store: truncated JSON plus the size it used to be.</summary>
internal sealed record TruncatedPayload(string Json, int FullSizeBytes);

/// <summary>
/// Spec §4 fidelity: every string field over the cap keeps its head and tail around a
/// <c>…[truncated N bytes]…</c> marker; the top-level prompt is exempt because prompts are stored
/// in full, and the top-level content of a <c>Remember</c> Event likewise — a deliberate save is
/// never dropped (§7.1), and clipping its middle would drop exactly what the user asked to keep.
/// Assistant messages — the other §4 stored-in-full class — never arrive on the hook surface at
/// all, an accepted v1 loss (§4 declines to read the transcript, ADR-0003). Cuts land on
/// character boundaries — a truncated payload is still honest UTF-8.
/// </summary>
internal static class PayloadTruncator
{
    public static TruncatedPayload Truncate(JsonElement payload, CaptureOptions options)
    {
        // The raw UTF-8 view of the original element — no UTF-16 round-trip. The span lives
        // only for this line, well inside the backing document's lifetime.
        var fullSize = JsonMarshal.GetRawUtf8Value(payload).Length;

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            Write(writer, payload, options, topLevel: true);
        }

        return new TruncatedPayload(Encoding.UTF8.GetString(stream.ToArray()), fullSize);
    }

    private static void Write(Utf8JsonWriter writer, JsonElement element, CaptureOptions options, bool topLevel)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject())
                {
                    writer.WritePropertyName(property.Name);
                    if (topLevel && (property.NameEquals("prompt") || property.NameEquals("content")))
                    {
                        property.Value.WriteTo(writer);
                    }
                    else
                    {
                        Write(writer, property.Value, options, topLevel: false);
                    }
                }

                writer.WriteEndObject();
                break;

            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    Write(writer, item, options, topLevel: false);
                }

                writer.WriteEndArray();
                break;

            case JsonValueKind.String:
                writer.WriteStringValue(TruncateField(element.GetString()!, options));
                break;

            default:
                element.WriteTo(writer);
                break;
        }
    }

    private static string TruncateField(string value, CaptureOptions options)
    {
        if (Encoding.UTF8.GetByteCount(value) <= options.PayloadFieldCapBytes)
        {
            return value;
        }

        var utf8 = Encoding.UTF8.GetBytes(value);
        var headEnd = CharBoundaryAtOrBefore(utf8, options.PayloadHeadBytes);
        var tailStart = CharBoundaryAtOrAfter(utf8, utf8.Length - options.PayloadTailBytes);

        return string.Concat(
            Encoding.UTF8.GetString(utf8, 0, headEnd),
            $"…[truncated {tailStart - headEnd} bytes]…",
            Encoding.UTF8.GetString(utf8, tailStart, utf8.Length - tailStart));
    }

    /// <summary>UTF-8 continuation bytes are 10xxxxxx; a cut point must not land on one.</summary>
    private static bool IsContinuation(byte b) => (b & 0xC0) == 0x80;

    private static int CharBoundaryAtOrBefore(byte[] utf8, int index)
    {
        while (index > 0 && IsContinuation(utf8[index]))
        {
            index--;
        }

        return index;
    }

    private static int CharBoundaryAtOrAfter(byte[] utf8, int index)
    {
        while (index < utf8.Length && IsContinuation(utf8[index]))
        {
            index++;
        }

        return index;
    }
}
