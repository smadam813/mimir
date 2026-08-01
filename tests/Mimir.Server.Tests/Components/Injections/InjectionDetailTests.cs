using Bunit;
using Mimir.Server.Components.Injections;
using Mimir.Server.Configuration;
using Mimir.Server.Storage.Entities;
using Mimir.Server.Ui;

namespace Mimir.Server.Tests.Components.Injections;

/// <summary>
/// One §8.3 entry, read the way the session read it. <c>InjectionDisplay</c> decides every word
/// and figure and is pinned there; what is here is the *order* — payload, then the score that put
/// each line in it, then the §7 formula those scores came out of, and only then the §9 mark. An
/// entry judged from the formula downwards is judged from the theory rather than from what the
/// session actually got.
/// <para>
/// Disconnected tier: the entry arrives whole as a parameter.
/// </para>
/// </summary>
public class InjectionDetailTests : RenderTestBase
{
    private static readonly DateTimeOffset Now = new(2026, 7, 22, 12, 0, 0, TimeSpan.Zero);

    private static readonly Guid ProjectId = Guid.Parse("77777777-7777-7777-7777-777777777777");

    private static readonly Guid WisdomId = Guid.Parse("99999999-9999-9999-9999-999999999999");

    /// <summary>
    /// The second line an entry carries, Global-scoped — the browsed Project's ambient universe
    /// includes Global (ADR-0009), so an entry mixing the two is the ordinary case rather than a
    /// contrived one, and it is the only fixture in which a link written from the record's own
    /// scope can be told apart from one written from the Project being browsed.
    /// </summary>
    private static readonly Guid GlobalWisdomId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

    private static InjectionLogEntry Entry(InjectionVerdict? verdict = null)
        => new(
            Id: Guid.Parse("88888888-8888-8888-8888-888888888888"),
            SessionId: "sess-detail",
            At: Now,
            Lane: InjectionLane.Prompt,
            QueryContext: "how does capture seal an Episode?",
            Chars: 240,
            Verdict: verdict,
            VerdictAt: verdict is null ? null : Now,
            PromotedCaseId: null,
            Items:
            [
                new InjectedWisdom(
                    WisdomId,
                    Score: 0.87,
                    Salient: false,
                    Wisdom: new WisdomListEntry(
                        WisdomId,
                        WisdomKind.Fact,
                        ProjectId,
                        "mimir",
                        "Sealing enqueues distillation",
                        Reinforcement: 3,
                        LastConfirmedAt: Now,
                        ContestedAt: null,
                        RetiredAt: null,
                        SupersededBy: null,
                        OrphanedProvenance: false)),
                new InjectedWisdom(
                    GlobalWisdomId,
                    Score: 0.71,
                    Salient: false,
                    Wisdom: new WisdomListEntry(
                        GlobalWisdomId,
                        WisdomKind.Preference,
                        Project.GlobalId,
                        "Global",
                        "Prefer the smallest diff that holds",
                        Reinforcement: 5,
                        LastConfirmedAt: Now,
                        ContestedAt: null,
                        RetiredAt: null,
                        SupersededBy: null,
                        OrphanedProvenance: false)),
            ]);

    private IRenderedComponent<InjectionDetail> RenderAt(InjectionVerdict? verdict = null)
        => Render<InjectionDetail>(p => p
            .Add(c => c.Entry, Entry(verdict))
            .Add(c => c.ProjectId, ProjectId)
            .Add(c => c.Options, new RecallOptions())
            .Add(c => c.OnMark, _ => { })
            .Add(c => c.OnPromote, () => { }));

    /// <summary>
    /// The four sections in the session's own order. Read as a sequence rather than as four
    /// presence checks, because the order <em>is</em> the rule: any of them can be on screen and
    /// still be in the wrong place.
    /// </summary>
    [Fact]
    public void TheEntryReadsInTheSessionsOrder_PayloadThenScoresThenFormulaThenMark()
    {
        var detail = RenderAt();

        // Each landmark named by its selector rather than its words: what is under test is where
        // the sections sit, and the words are InjectionDisplay's and pinned there.
        var landmarks = detail.Find("article.entry-detail")
            .QuerySelectorAll("h6.detail-heading, code.detail-formula, span.detail-actions-label")
            .Select(e => e.ClassName == "detail-formula" ? "«the §7 formula»"
                : e.ClassName == "detail-actions-label" ? "«the §9 mark»"
                : e.TextContent.Trim())
            .ToArray();

        landmarks.ShouldBe(
        [
            "The query it answered",
            "What the session received",
            "Why each line was chosen",
            "«the §7 formula»",
            "«the §9 mark»",
        ]);
    }

    /// <summary>
    /// The mark belongs to the entry, not to any line in it — the payload is what a session
    /// received as a whole, and a per-line verdict would be a judgement about a ranking nobody
    /// made. The screen says so beside the buttons rather than leaving it to be inferred.
    /// </summary>
    [Fact]
    public void TheMark_IsTheEntrysAndTheScreenSaysSo()
    {
        var detail = RenderAt();

        detail.Find("span.detail-actions-note").TextContent
            .ShouldContain("One mark for the whole entry, not per line");
        detail.FindAll("div.score-row button").ShouldBeEmpty();
    }

    /// <summary>
    /// Every score row links into the universe being <em>browsed</em>, never into the linked
    /// Wisdom's own Scope — the third member of that family, after the Wisdom surface's row links
    /// and the drill-down's produced links. The Global row is the one that can tell them apart:
    /// written from <c>ScopeProjectId</c> its link would switch the curator to Global's universe
    /// mid-read, and the score table beside it would then be explaining a ranking from a screen
    /// that is no longer the one it ranked for.
    /// </summary>
    [Fact]
    public void EveryScoreRowLinks_IntoTheBrowsedProjectsUniverse_NotTheWisdomsOwnScope()
    {
        var detail = RenderAt();

        detail.FindAll("a.score-text").Select(a => a.GetAttribute("href")).ShouldBe(
        [
            $"projects/{ProjectId}/wisdom/{WisdomId}",
            $"projects/{ProjectId}/wisdom/{GlobalWisdomId}",
        ]);
        // The row still *says* Global, so what the link drops is the navigation, not the fact.
        detail.FindAll("span.score-facts")[1].TextContent.ShouldContain("Global");
    }

    /// <summary>
    /// A marked entry shows which verdict it carries, so re-marking is a visible switch rather
    /// than a guess.
    /// </summary>
    [Fact]
    public void AMarkedEntry_ShowsWhichVerdictItCarries()
    {
        var detail = RenderAt(InjectionVerdict.Noise);

        detail.FindAll("div.detail-actions button.btn-primary")
            .Select(b => b.TextContent.Trim()).ShouldBe(["Noise"]);
    }
}
