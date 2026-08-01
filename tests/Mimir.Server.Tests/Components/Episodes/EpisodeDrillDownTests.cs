using Bunit;
using Mimir.Server.Components.Episodes;
using Mimir.Server.Storage.Entities;
using Mimir.Server.Ui;

namespace Mimir.Server.Tests.Components.Episodes;

/// <summary>
/// The disconnected tier over the §8.2 drill-down: everything it draws arrives through
/// <c>Detail</c>, so no database is in the picture.
/// </summary>
public class EpisodeDrillDownTests : RenderTestBase
{
    private static readonly DateTimeOffset Started = new(2026, 7, 26, 13, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// <see cref="EventPayload"/> serializes with <c>UnsafeRelaxedJsonEscaping</c> so the §4
    /// truncation marker survives as written, which is only safe because the text lands in Blazor
    /// render output and Blazor encodes for HTML itself. Nothing in the pure class can show that,
    /// so the guarantee is pinned where it actually holds: a payload carrying markup renders as
    /// text, not as an element.
    /// </summary>
    [Fact]
    public void APayloadCarryingMarkup_RendersAsText_BecauseBlazorEncodesTheOutput()
    {
        const string markup = """{"prompt":"<script>alert('x')</script>"}""";

        var drillDown = Render(markup);

        var payload = drillDown.Find("pre.event-payload");
        payload.QuerySelector("script").ShouldBeNull();
        payload.TextContent.ShouldContain("<script>alert('x')</script>");
        drillDown.Markup.ShouldContain("&lt;script&gt;");
    }

    /// <summary>
    /// The other half of the same serializer choice: relaxed escaping is what keeps the marker
    /// legible rather than re-encoded into <c>\u2026</c> escapes on the way to the screen.
    /// </summary>
    [Fact]
    public void ATruncatedPayload_KeepsItsMarkerLegibleOnTheScreen()
    {
        const string truncated = """{"prompt":"the first half…[truncated 4096 bytes]…"}""";

        var drillDown = Render(truncated);

        drillDown.Find("pre.event-payload").TextContent
            .ShouldContain("…[truncated 4096 bytes]…");
    }

    private IRenderedComponent<EpisodeDrillDown> Render(string payload)
    {
        var episodeId = Guid.CreateVersion7();
        var episode = new Episode
        {
            Id = episodeId,
            SessionId = "sess-render",
            ProjectId = Guid.CreateVersion7(),
            StartedAt = Started,
            SealedAt = Started.AddHours(1),
            SealReason = "clear",
            Cwd = @"C:\git\mimir",
            Distillation = DistillationState.Done,
        };
        var evt = new Event
        {
            Id = Guid.CreateVersion7(),
            EpisodeId = episodeId,
            Seq = 1,
            Type = EventType.UserPromptSubmit,
            At = Started,
            Payload = payload,
            PayloadFullSize = payload.Length,
        };

        return Render<EpisodeDrillDown>(p => p
            .Add(c => c.Detail, new EpisodeDetail(episode, [evt], []))
            .Add(c => c.ProjectId, episode.ProjectId)
            .Add(c => c.OnDeleteEvent, (Guid _) => { })
            .Add(c => c.OnDeleteEpisode, (Guid _) => { }));
    }
}
