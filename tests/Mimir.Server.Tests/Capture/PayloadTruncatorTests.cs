using System.Text;
using System.Text.Json;
using Mimir.Server.Capture;
using Mimir.Server.Configuration;

namespace Mimir.Server.Tests.Capture;

/// <summary>
/// Spec §4 fidelity: tool payloads keep 3 KB head + 1 KB tail of any oversized field with a
/// visible marker, prompts stay whole, and the original size is always recorded.
/// </summary>
public class PayloadTruncatorTests
{
    private static readonly CaptureOptions Options = new();

    [Fact]
    public void ASmallPayloadPassesThroughUntouched()
    {
        var json = """{"tool_name":"Bash","tool_input":{"command":"ls"},"count":3}""";

        var truncated = Truncate(json);

        truncated.Json.ShouldBe(json);
        truncated.FullSizeBytes.ShouldBe(Encoding.UTF8.GetByteCount(json));
    }

    [Fact]
    public void AnOversizedField_Keeps3KHeadPlus1KTailWithTheMarker()
    {
        // 5,000 bytes of ASCII: head 3,072 + tail 1,024 leaves exactly 904 dropped.
        var json = JsonSerializer.Serialize(new { tool_response = new string('a', 5000) });

        var value = FieldOf(Truncate(json), "tool_response");

        value.ShouldBe(new string('a', 3072) + "…[truncated 904 bytes]…" + new string('a', 1024));
    }

    [Fact]
    public void TheOriginalSizeIsRecordedEvenWhenTruncating()
    {
        var json = JsonSerializer.Serialize(new { tool_response = new string('a', 5000) });

        Truncate(json).FullSizeBytes.ShouldBe(Encoding.UTF8.GetByteCount(json));
    }

    [Fact]
    public void NestedFieldsAreTruncatedFieldByField()
    {
        var json = JsonSerializer.Serialize(new
        {
            tool_input = new { command = new string('x', 6000), description = "short" },
        });

        var root = JsonDocument.Parse(Truncate(json).Json).RootElement.GetProperty("tool_input");

        root.GetProperty("command").GetString()!.ShouldContain("…[truncated");
        root.GetProperty("description").GetString().ShouldBe("short");
    }

    [Fact]
    public void ThePromptIsStoredInFull_HoweverLong()
    {
        var prompt = new string('p', 10_000);
        var json = JsonSerializer.Serialize(new { prompt, tool_response = new string('a', 5000) });

        var truncated = Truncate(json);

        FieldOf(truncated, "prompt").ShouldBe(prompt, "spec §4: prompts are stored in full");
        FieldOf(truncated, "tool_response").ShouldContain("…[truncated");
    }

    [Fact]
    public void MultiByteTextIsCutAtCharacterBoundaries_NeverCorrupted()
    {
        // '€' is 3 UTF-8 bytes; 1,707 of them (5,121 bytes) forces cuts near both limits.
        var json = JsonSerializer.Serialize(new { tool_response = new string('€', 1707) });

        var value = FieldOf(Truncate(json), "tool_response");

        value.ShouldNotContain("�");
        value.ShouldStartWith("€");
        value.ShouldEndWith("€");
        value.ShouldContain("…[truncated");
    }

    [Fact]
    public void NonStringValuesPassThrough()
    {
        var json = """{"big":123456789,"flag":true,"none":null,"list":[1,2,3]}""";

        Truncate(json).Json.ShouldBe(json);
    }

    [Fact]
    public void StringsInsideArraysAreTruncatedToo()
    {
        var json = JsonSerializer.Serialize(new { chunks = new[] { new string('y', 9000), "tiny" } });

        var chunks = JsonDocument.Parse(Truncate(json).Json).RootElement.GetProperty("chunks");

        chunks[0].GetString()!.ShouldContain("…[truncated");
        chunks[1].GetString().ShouldBe("tiny");
    }

    private static TruncatedPayload Truncate(string json)
    {
        using var document = JsonDocument.Parse(json);
        return PayloadTruncator.Truncate(document.RootElement, Options);
    }

    private static string FieldOf(TruncatedPayload truncated, string property)
        => JsonDocument.Parse(truncated.Json).RootElement.GetProperty(property).GetString()!;
}
