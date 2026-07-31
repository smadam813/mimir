using System.Text.RegularExpressions;

namespace Mimir.Server.Tests;

/// <summary>
/// #119: the three §8 surfaces draw one chassis — the detail frame, the aside, and the list head's
/// chips — and it lives once, in the Mimir token layer. Per-component <c>.razor.css</c> is scoped,
/// so a shared look written there means three copies hand-synced by whoever remembers, which is
/// exactly how the three asides had already drifted apart in three places. This pins the shape the
/// hoist put them in: every shared selector defined in <c>mimir.css</c> and in no scoped
/// stylesheet, bar the deliberate per-surface deltas listed below, and no <c>::deep</c> anywhere.
///
/// Pure text scan, no SQL and no DI, so it runs everywhere including with no Postgres reachable.
/// Substring-anchored rather than parsed: the commas inside <c>color-mix(…)</c> and <c>var(…)</c>
/// make a naive selector split produce garbage, so each selector is matched where a rule can
/// actually start it — at the head of a line, or just past a comma in a selector list.
/// </summary>
public class SurfaceChassisTests
{
    private static readonly string TokenLayerPath =
        Path.Combine(AppContext.BaseDirectory, "wwwroot", "nocturne", "mimir.css");

    private static readonly string ScopedRoot = Path.Combine(AppContext.BaseDirectory, "Components");

    /// <summary>
    /// The chassis, exactly as the hoisted rules spell it. Two tiers: what all three surfaces draw
    /// (the aside, the frame, the chips) and what only the two whose detail is a child component
    /// draw (the detail plumbing and the placeholder) — Wisdom writes its detail inline, so those
    /// simply do not match there.
    /// </summary>
    private static readonly string[] HoistedSelectors =
    [
        ".pane-detail-frame",
        ".pane-detail-frame .pane-detail",
        ".pane-detail-frame .pane-detail-body",
        ".pane-detail-frame .pane-detail-footer",
        ".pane-placeholder",
        ".pane-aside",
        ".pane-aside h6",
        ".aside-figure",
        ".aside-figure-value",
        ".aside-figure-unit",
        ".aside-rows",
        ".aside-figure + .aside-rows",
        ".aside-rows.is-prose",
        ".aside-rows dt",
        ".aside-rows dd",
        ".aside-rows dd.is-accent",
        ".aside-rows dd.is-strong",
        ".aside-note",
        ".aside-links",
        ".aside-link",
        "a.aside-link:hover",
        ".chips",
        ".chip",
        ".chip.is-active",
        ".chip-count",
    ];

    /// <summary>
    /// The detail plumbing spelled the way a scoped file would spell it coming back. The token
    /// layer writes these under the frame, because that is the pane they belong to; a copy
    /// returning to <c>InjectionDetail</c>'s or <c>EpisodeDrillDown</c>'s own stylesheet would
    /// write the bare class, since a component styling its own root needs no ancestor and no
    /// <c>::deep</c>. Checking the prefixed spelling alone would leave that shape — the likely one
    /// now that the hoist has removed the reason for the prefix — unguarded.
    ///
    /// Only forbidden in scoped stylesheets, never required in the token layer: what the token
    /// layer must carry is the prefixed form above. A rule that merely *descends* from one of
    /// these is untouched, so <c>InjectionDetail</c>'s <c>.pane-detail-footer p</c> — the footer's
    /// own paragraph type, genuinely that component's — still passes.
    /// </summary>
    private static readonly string[] BareDetailSpellings =
    [
        ".pane-detail",
        ".pane-detail-body",
        ".pane-detail-footer",
    ];

    /// <summary>
    /// The surfaces that re-open a hoisted selector in their own stylesheet — one to override a
    /// declaration, one to add one the token layer never sets — each because the difference is
    /// genuinely theirs.
    /// <list type="bullet">
    /// <item>The Injection log's <c>.aside-link</c> is a grid, because it places this surface's own
    /// <c>.aside-link-text</c> beside its <c>.aside-link-count</c>. This is the rule from #119's
    /// decision 6 exactly: a variant whose children are the surface's own stays beside them.</item>
    /// <item>The Episode list's <c>.chip</c> capitalizes, because
    /// <c>EpisodeDisplay.StateWord</c> answers a lowercase state name and no other surface's chip
    /// labels itself from one. Decision 6's rule does not reach it — <c>text-transform</c> lays
    /// nothing out, so there is no layout here to split from the children it lays out.</item>
    /// </list>
    /// Held as an exact pair, so an entry that stops being a delta fails here rather than quietly
    /// licensing a duplicate that came back.
    /// </summary>
    private static readonly (string File, string Selector)[] ScopedDeltas =
    [
        ("InjectionLogTab.razor.css", ".aside-link"),
        ("EpisodeList.razor.css", ".chip"),
    ];

    [Fact]
    public void EveryHoistedSelector_IsDefinedInTheTokenLayer()
    {
        var tokenLayer = CssText.StripComments(File.ReadAllText(TokenLayerPath));

        foreach (var selector in HoistedSelectors)
        {
            Defines(tokenLayer, selector).ShouldBeTrue(
                $"the token layer no longer defines {selector}, so the surfaces that dropped their "
                + "own copy of it are drawing nothing");
        }
    }

    [Fact]
    public void NoScopedStylesheet_RestatesAHoistedSelector()
    {
        foreach (var (path, code) in ScopedStylesheets())
        {
            var name = Path.GetFileName(path);

            foreach (var selector in HoistedSelectors.Concat(BareDetailSpellings))
            {
                if (ScopedDeltas.Contains((name, selector)))
                {
                    continue;
                }

                Defines(code, selector).ShouldBeFalse(
                    $"{name} defines {selector}, which the token layer already carries — a second "
                    + "copy of a shared rule is what #119 hoisted the chassis out of");
            }
        }
    }

    [Fact]
    public void EveryListedDelta_IsStillADelta()
    {
        var stylesheets = ScopedStylesheets();

        foreach (var (name, selector) in ScopedDeltas)
        {
            // By name rather than by dictionary: two components in different folders may one day
            // share a file name, and a test that threw on that would fail for a reason that has
            // nothing to do with what it pins.
            var matches = stylesheets.Where(pair => Path.GetFileName(pair.Path) == name).ToList();

            matches.Count.ShouldBe(1, $"{name} should be exactly one scoped stylesheet");
            Defines(matches[0].Code, selector).ShouldBeTrue(
                $"{name} no longer re-opens {selector}, so the allowance for it is stale and would "
                + "let a real duplicate back in");
        }
    }

    /// <summary>
    /// <c>::deep</c> is what a scoped stylesheet needs to reach a child component's markup, and the
    /// chassis was the only thing in this repo reaching for it. Hoisted, those rules are plain
    /// descendant selectors — so a new one appearing means a shared rule has been written back into
    /// a scoped file, the shape the hoist exists to prevent.
    /// </summary>
    [Fact]
    public void NoScopedStylesheet_ReachesIntoAChildComponent()
    {
        foreach (var (path, code) in ScopedStylesheets())
        {
            code.ShouldNotContain(
                "::deep",
                customMessage: $"{Path.GetFileName(path)} styles a child component's markup — a rule "
                    + "two components share belongs in the token layer (#119)");
        }
    }

    private static List<(string Path, string Code)> ScopedStylesheets()
    {
        var files = Directory.GetFiles(ScopedRoot, "*.razor.css", SearchOption.AllDirectories);

        files.ShouldNotBeEmpty();

        return [.. files.Select(file => (file, CssText.StripComments(File.ReadAllText(file))))];
    }

    /// <summary>
    /// Whether <paramref name="css"/> opens a rule on exactly <paramref name="selector"/>. Anchored
    /// at a line start or a comma so a compound never matches inside a longer one — <c>.chip</c>
    /// must not answer for <c>.chip-count</c> or for <c>.chip.is-active .chip-count</c>, and
    /// <c>.aside-figure</c> must not answer for <c>.aside-figure-value</c> or for the
    /// <c>.aside-figure + .aside-rows</c> adjacency, each of which is its own entry.
    /// </summary>
    private static bool Defines(string css, string selector)
    {
        var compounds = selector.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(Regex.Escape);

        return Regex.IsMatch(
            css,
            $@"(?:^|,)\s*{string.Join(@"\s+", compounds)}\s*[{{,]",
            RegexOptions.Multiline);
    }
}
