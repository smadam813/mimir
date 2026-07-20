using Mimir.Server.Ui;

namespace Mimir.Server.Tests.Ui;

/// <summary>
/// Spec §8.2: the drill-down renders payloads with their truncation markers. The helper only
/// reshapes what capture stored — pretty JSON, marker kept visible, never a crash on odd input.
/// </summary>
public sealed class EventPayloadTests
{
    [Fact]
    public void ATruncatedPayload_IsRecognizedByItsMarker()
    {
        EventPayload.IsTruncated("""{"tool_response":"head…[truncated 904 bytes]…tail"}""").ShouldBeTrue();
    }

    [Fact]
    public void AnIntactPayload_IsNot()
    {
        EventPayload.IsTruncated("""{"prompt":"how do I deploy?"}""").ShouldBeFalse();
    }

    [Fact]
    public void Pretty_IndentsAndKeepsTheMarkerReadable()
    {
        var pretty = EventPayload.Pretty("""{"tool_response":"a…[truncated 12 bytes]…z"}""");

        pretty.ShouldContain("\n");
        pretty.ShouldContain("…[truncated 12 bytes]…", customMessage: "the marker must survive JSON re-encoding");
    }

    [Fact]
    public void Pretty_AnswersMalformedPayloadAsIs()
    {
        EventPayload.Pretty("not json at all").ShouldBe("not json at all");
    }
}
