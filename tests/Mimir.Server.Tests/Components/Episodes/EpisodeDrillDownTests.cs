using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Mimir.Server.Components.Episodes;
using Mimir.Server.Storage.Entities;
using Mimir.Server.Ui;

namespace Mimir.Server.Tests.Components.Episodes;

/// <summary>
/// The session record itself. Everything it says is <c>EpisodeDisplay</c>'s and pinned there, so
/// this is about the stream's bound and the expansion key — the two things that decide whether a
/// curator is looking at the whole session or the first slice of it, and the one place a wrong
/// answer is silent rather than visible.
/// <para>
/// Disconnected tier: the record arrives whole as a parameter. Only <c>NavigationManager</c> is
/// resolved, and bUnit supplies it.
/// </para>
/// </summary>
public class EpisodeDrillDownTests : RenderTestBase
{
    private static readonly DateTimeOffset Now = new(2026, 7, 22, 12, 0, 0, TimeSpan.Zero);

    private static readonly Guid ProjectId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private static readonly Episode Session = new()
    {
        Id = Guid.Parse("44444444-4444-4444-4444-444444444444"),
        SessionId = "sess-abcdef",
        ProjectId = ProjectId,
        StartedAt = Now.AddHours(-1),
        SealedAt = Now,
        SealReason = "clear",
        Cwd = @"C:\git\mimir",
        Distillation = DistillationState.Done,
    };

    private static Event At(int seq, string? payload = null) => new()
    {
        Id = Guid.Parse($"55555555-5555-5555-5555-{seq:D12}"),
        EpisodeId = Session.Id,
        Seq = seq,
        Type = EventType.UserPromptSubmit,
        At = Now,
        Payload = payload ?? $$"""{"prompt":"event {{seq}}"}""",
        PayloadFullSize = 32,
    };

    private static Mimir.Server.Ui.EpisodeDetail Detail(
        int events, IReadOnlyList<EpisodeWisdom>? produced = null)
        => new(Session, [.. Enumerable.Range(1, events).Select(seq => At(seq))], produced ?? []);

    private IRenderedComponent<EpisodeDrillDown> RenderAt(
        Mimir.Server.Ui.EpisodeDetail detail, string? fragment = null)
    {
        if (fragment is not null)
        {
            Services.GetRequiredService<NavigationManager>().NavigateTo(fragment);
        }

        return Render<EpisodeDrillDown>(p => p
            .Add(c => c.Detail, detail)
            .Add(c => c.ProjectId, ProjectId)
            .Add(c => c.OnDeleteEvent, _ => { })
            .Add(c => c.OnDeleteEpisode, _ => { }));
    }

    /// <summary>
    /// A stream inside the bound is whole and unannounced — no toggle, nothing withheld, and
    /// nothing claiming there is more.
    /// </summary>
    [Fact]
    public void AShortStream_RendersWholeAndSaysNothingAboutABound()
    {
        var drill = RenderAt(Detail(events: 3));

        drill.FindAll("li.event-item").Count.ShouldBe(3);
        drill.FindAll("div.stream-bound").ShouldBeEmpty();
    }

    /// <summary>
    /// Past the bound the stream is cut and <em>says so</em>. Silence here is the failure that
    /// matters: a curator who takes the first slice for the whole session concludes something
    /// about a record they have not read.
    /// </summary>
    [Fact]
    public void ALongStream_IsBoundedOnArrivalAndSaysSo()
    {
        var drill = RenderAt(Detail(events: EpisodeDisplay.StreamBound + 5));

        drill.FindAll("li.event-item").Count.ShouldBe(EpisodeDisplay.StreamBound);
        drill.Find("div.stream-bound").TextContent
            .ShouldContain((EpisodeDisplay.StreamBound + 5).ToString("N0"));
    }

    [Fact]
    public void AskingForTheRest_ShowsTheWholeStream()
    {
        var total = EpisodeDisplay.StreamBound + 5;
        var drill = RenderAt(Detail(events: total));

        drill.Find("div.stream-bound button").Click();

        drill.FindAll("li.event-item").Count.ShouldBe(total);
    }

    /// <summary>
    /// A §8.1 Provenance link lands on an Event the bound would otherwise withhold, so the stream
    /// opens whole rather than dropping the curator on a page where the anchor resolves to nothing.
    /// </summary>
    [Fact]
    public void AnAnchorPastTheBound_OpensTheStreamOnArrival()
    {
        var total = EpisodeDisplay.StreamBound + 5;
        var deep = At(total).Id;

        var drill = RenderAt(Detail(events: total), EpisodeDisplay.EventAnchorHref(deep));

        drill.FindAll("li.event-item").Count.ShouldBe(total);
        drill.FindAll($"li#{EpisodeDisplay.EventAnchorId(deep)}").ShouldHaveSingleItem();
    }

    /// <summary>
    /// The expansion belongs to the (record, anchor) pair, not to the render. The feed hands this
    /// component a freshly-read detail on every captured Event, and collapsing the stream there
    /// would fold it under a curator reading a live session.
    /// </summary>
    [Fact]
    public void AFeedRefreshOfTheSameSession_LeavesTheStreamOpen()
    {
        var total = EpisodeDisplay.StreamBound + 5;
        var drill = RenderAt(Detail(events: total));
        drill.Find("div.stream-bound button").Click();

        drill.Render(p => p
            .Add(c => c.Detail, Detail(events: total + 1))
            .Add(c => c.ProjectId, ProjectId)
            .Add(c => c.OnDeleteEvent, _ => { })
            .Add(c => c.OnDeleteEpisode, _ => { }));

        drill.FindAll("li.event-item").Count.ShouldBe(total + 1);
    }

    /// <summary>
    /// A produced Wisdom links into the surface of the Project being browsed, never into the
    /// Wisdom's own Scope — the same family the Wisdom surface's own row links belong to.
    /// </summary>
    [Fact]
    public void AProducedWisdomLink_TargetsTheBrowsedProject()
    {
        var wisdomId = Guid.Parse("66666666-6666-6666-6666-666666666666");
        var drill = RenderAt(Detail(
            events: 1,
            produced: [new EpisodeWisdom(wisdomId, WisdomKind.Fact, "a global fact", true, 2)]));

        drill.Find("a.produced-row").GetAttribute("href")
            .ShouldBe($"projects/{ProjectId}/wisdom/{wisdomId}");
    }

    /// <summary>
    /// Each Event carries the anchor id the §8.1 link writes, taken from one spelling in
    /// <c>EpisodeDisplay</c>. Spelled a second time here and the link stops landing, silently.
    /// </summary>
    [Fact]
    public void EveryEvent_CarriesTheAnchorTheProvenanceLinkWrites()
    {
        var drill = RenderAt(Detail(events: 2));

        drill.FindAll("li.event-item").Select(e => e.Id)
            .ShouldBe([EpisodeDisplay.EventAnchorId(At(1).Id), EpisodeDisplay.EventAnchorId(At(2).Id)]);
    }

    /// <summary>
    /// <see cref="EventPayload"/> serializes with <c>UnsafeRelaxedJsonEscaping</c> so the §4
    /// truncation marker survives as written, which is only safe because the text lands in Blazor
    /// render output and Blazor encodes for HTML itself. Nothing in the pure class can show that,
    /// so the guarantee is pinned where it actually holds: a payload carrying markup renders as
    /// text, not as an element. (#134)
    /// </summary>
    [Fact]
    public void APayloadCarryingMarkup_RendersAsText_BecauseBlazorEncodesTheOutput()
    {
        const string markup = """{"prompt":"<script>alert('x')</script>"}""";

        var drill = RenderAt(new Mimir.Server.Ui.EpisodeDetail(Session, [At(1, markup)], []));

        var payload = drill.Find("pre.event-payload");
        payload.QuerySelector("script").ShouldBeNull();
        payload.TextContent.ShouldContain("<script>alert('x')</script>");
        drill.Markup.ShouldContain("&lt;script&gt;");
    }

    /// <summary>
    /// A failed delete is said under the button that failed. The surface owns the words because it
    /// owns the call; this component owns only where they sit.
    /// </summary>
    [Fact]
    public void AFailedDelete_IsSaidInTheDangerZone()
    {
        var drill = Render<EpisodeDrillDown>(p => p
            .Add(c => c.Detail, Detail(events: 1))
            .Add(c => c.ProjectId, ProjectId)
            .Add(c => c.OnDeleteEvent, _ => { })
            .Add(c => c.OnDeleteEpisode, _ => { })
            .Add(c => c.Error, "The Episode was not deleted"));

        drill.Find("div.pane-danger p.pane-error").TextContent
            .ShouldBe("The Episode was not deleted");
        drill.Find("p.pane-error").GetAttribute("role").ShouldBe("alert");
    }
}
