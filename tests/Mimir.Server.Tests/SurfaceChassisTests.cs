using System.Text.RegularExpressions;

namespace Mimir.Server.Tests;

/// <summary>
/// #119: the three §8 surfaces draw one chassis — the detail frame, the aside, and the list head's
/// chips — and it lives once, in the Mimir token layer rather than as three hand-synced copies in
/// their scoped stylesheets. CLAUDE.md's stylesheet section states the rule and why a rule two
/// components share can only live there; this pins the shape it put them in.
///
/// Four properties: every shared selector is defined in <c>mimir.css</c>; no scoped stylesheet
/// styles one of the chassis's classes, bar the licensed per-surface deltas below and each held to
/// exactly the declarations it is licensed for; no <c>::deep</c> anywhere; and the chassis's names
/// collide with nothing in the vendored system.
///
/// Pure text scan, no SQL and no DI, so it runs everywhere including with no Postgres reachable.
/// </summary>
public class SurfaceChassisTests
{
    private static readonly Regex ClassToken = new(@"\.([A-Za-z_][\w-]*)");
    private static readonly Regex Combinator = new(@"[\s>+~]+");
    private static readonly Regex Whitespace = new(@"\s+");

    private static readonly string TokenLayerPath =
        Path.Combine(AppContext.BaseDirectory, "wwwroot", "nocturne", "mimir.css");

    private static readonly string VendoredPath =
        Path.Combine(AppContext.BaseDirectory, "wwwroot", "nocturne", "styles.css");

    private static readonly string ScopedRoot = Path.Combine(AppContext.BaseDirectory, "Components");

    /// <summary>
    /// The chassis, exactly as the hoisted rules spell it. Two tiers: what all three surfaces draw
    /// (the aside, the frame, the chips) and what only the two whose detail is a child component
    /// draw (the detail plumbing and the placeholder) — Wisdom writes its detail inline, so those
    /// simply do not match there. <c>.pane-danger</c> and <c>.pane-error</c> are a third case: the
    /// §8.2 danger zone and the inline failure above it, drawn by the two surfaces that offer a
    /// hard delete and by no others (#106).
    /// </summary>
    private static readonly string[] HoistedSelectors =
    [
        ".pane-detail-frame",
        ".pane-detail-frame .pane-detail",
        ".pane-detail-frame .pane-detail-body",
        ".pane-detail-frame .pane-detail-footer",
        ".pane-danger",
        ".pane-error",
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
    /// The class names the token layer owns, read off <see cref="HoistedSelectors"/> so the two
    /// cannot drift apart. The <c>is-</c> modifiers are deliberately not among them: a modifier
    /// means nothing on its own — it is always written onto one of these — so it is the base class
    /// that names the thing the chassis owns.
    /// </summary>
    private static readonly HashSet<string> HoistedClasses =
    [
        .. HoistedSelectors
            .SelectMany(selector => ClassToken.Matches(selector).Select(match => match.Groups[1].Value))
            .Where(name => !name.StartsWith("is-", StringComparison.Ordinal)),
    ];

    /// <summary>
    /// Every scoped rule that styles one of those classes — the complete inventory, not a set of
    /// exceptions to a narrower check, since anything absent here fails. Each is licensed for the
    /// declarations listed and no others, so the pre-#119 block cannot be pasted back under cover
    /// of a licensed selector.
    /// <list type="bullet">
    /// <item>The Injection log's <c>.aside-link</c> is a grid, because it places this surface's own
    /// <c>.aside-link-text</c> beside its <c>.aside-link-count</c>.</item>
    /// <item>The Episode list's <c>.chip</c> capitalizes, because <c>EpisodeDisplay.StateWord</c>
    /// answers a lowercase state name and no other surface's chip labels itself from one.</item>
    /// <item>The Episode list's <c>.chip-count</c> lights with its chip, because a state's
    /// population is the thing that filter is being chosen on. It reaches a hoisted class through a
    /// descendant selector, which is exactly the shape a spelling-by-spelling scan cannot see, and
    /// it is listed here rather than left to that blind spot.</item>
    /// </list>
    /// Keyed by path under <c>Components/</c> rather than by file name, so a licence names one
    /// stylesheet and a second component that one day shares a file name is neither licensed by it
    /// nor failed by it.
    /// </summary>
    private static readonly (string File, string Selector, string[] Declarations)[] ScopedDeltas =
    [
        ("Injections/InjectionLogTab.razor.css", ".aside-link",
            [
                "display: grid",
                "grid-template-columns: 1fr auto",
                "align-items: center",
                "gap: var(--space-3)",
            ]),
        ("Episodes/EpisodeList.razor.css", ".chip",
            ["text-transform: capitalize"]),
        ("Episodes/EpisodeList.razor.css", ".chip.is-active .chip-count",
            ["color: var(--color-accent-400)"]),
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
    public void NoScopedStylesheet_StylesAHoistedClass()
    {
        foreach (var (path, code) in ScopedStylesheets())
        {
            foreach (var (selector, body) in Rules(code))
            {
                if (!SubjectClasses(selector).Overlaps(HoistedClasses))
                {
                    continue;
                }

                var licensed = ScopedDeltas
                    .Where(delta => delta.File == path && delta.Selector == Normalise(selector))
                    .ToList();

                licensed.Count.ShouldBe(
                    1,
                    $"{path} styles {Normalise(selector)}, whose subject is a class the token layer "
                    + "owns — a second copy of a shared rule is what #119 hoisted the chassis out "
                    + "of. If the difference is genuinely this surface's, license it in "
                    + "ScopedDeltas with the declarations it is for");

                Declarations(body).ShouldBe(
                    licensed[0].Declarations,
                    $"{path}'s {Normalise(selector)} is licensed as a delta, and a delta is the one "
                    + "or two declarations that differ — restating the rest of the hoisted rule "
                    + "under it is the hand-synced copy back again");
            }
        }
    }

    /// <summary>
    /// The other direction: a licence whose rule has gone is a standing permission to write a real
    /// duplicate back, and nothing else would catch it.
    /// </summary>
    [Fact]
    public void EveryLicensedDelta_IsStillADelta()
    {
        var stylesheets = ScopedStylesheets();

        foreach (var (file, selector, _) in ScopedDeltas)
        {
            var matches = stylesheets.Where(pair => pair.Path == file).ToList();

            matches.Count.ShouldBe(1, $"{file} is no longer one scoped stylesheet");

            Rules(matches[0].Code).Any(rule => Normalise(rule.Selector) == selector).ShouldBeTrue(
                $"{file} no longer styles {selector}, so the licence for it is stale and would let "
                + "a real duplicate back in");
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
                customMessage: $"{path} styles a child component's markup — a rule two components "
                    + "share belongs in the token layer (#119)");
        }
    }

    /// <summary>
    /// The chassis's names are generic — <c>.chip</c>, <c>.chips</c>, <c>.aside-note</c> — and they
    /// sit in the same global namespace as <c>styles.css</c>, which is vendored verbatim and
    /// replaced wholesale on a Nocturne sync (ADR-0001's one-file swap). Nocturne ships no
    /// <c>.chip</c> today. If a sync ever did, <c>mimir.css</c> would still win where both set the
    /// same property, since <c>App.razor</c> links it second — but it would lose every compound the
    /// vendored file spelled, because <c>.chip:hover</c> outranks a bare <c>.chip</c>, and the swap
    /// would land green with every filter chip's cascade quietly changed. This is what makes that
    /// sync go red instead.
    /// </summary>
    [Fact]
    public void NoHoistedClass_IsNamedByTheVendoredSystem()
    {
        var vendored = CssText.StripComments(File.ReadAllText(VendoredPath));

        foreach (var name in HoistedClasses)
        {
            Names(vendored, name).ShouldBeFalse(
                $"the vendored Nocturne stylesheet now names .{name}, which the Mimir layer also "
                + "writes — one of the two has to be renamed, or the chips and asides draw a "
                + "cascade nobody chose");
        }
    }

    private static List<(string Path, string Code)> ScopedStylesheets()
    {
        var files = Directory.GetFiles(ScopedRoot, "*.razor.css", SearchOption.AllDirectories);

        files.ShouldNotBeEmpty();

        return
        [
            .. files.Select(file =>
                (Path.GetRelativePath(ScopedRoot, file).Replace('\\', '/'),
                 CssText.StripComments(File.ReadAllText(file)))),
        ];
    }

    /// <summary>
    /// Every rule in <paramref name="css"/> as its selector and its body, flattened through
    /// at-rules so a rule nested in an <c>@media</c> reads the same as one written beside it —
    /// a one-line <c>@media (…) { .chip { … } }</c> is the shape that hides from a line-anchored
    /// scan. Comments are already stripped by the caller.
    /// </summary>
    private static IEnumerable<(string Selector, string Body)> Rules(string css)
    {
        var start = 0;

        for (var i = 0; i < css.Length; i++)
        {
            if (css[i] != '{')
            {
                continue;
            }

            var prelude = css[start..i].Trim();

            var depth = 1;
            var end = i + 1;
            while (end < css.Length && depth > 0)
            {
                if (css[end] == '{')
                {
                    depth++;
                }
                else if (css[end] == '}')
                {
                    depth--;
                }

                end++;
            }

            var body = css[(i + 1)..(end - 1)];

            if (prelude.StartsWith('@'))
            {
                foreach (var nested in Rules(body))
                {
                    yield return nested;
                }
            }
            else
            {
                yield return (prelude, body);
            }

            i = end - 1;
            start = end;
        }
    }

    /// <summary>
    /// The classes carried by <paramref name="selector"/>'s <em>subject</em> — the rightmost
    /// compound of each comma-branch, which is the element the rule actually styles.
    ///
    /// Reading the subject rather than the whole selector is what separates a second copy of a
    /// shared rule from a surface styling its own markup underneath one. The Injection detail's
    /// <c>.pane-detail-footer p</c> and the log's <c>.aside-link.is-gone .aside-link-text</c> both
    /// name a hoisted class, but neither styles it: their subjects are that component's own
    /// <c>p</c> and <c>.aside-link-text</c>. It is also what catches the respellings an
    /// enumerated-spellings scan cannot see — <c>.chip:hover</c>, <c>button.chip</c>,
    /// <c>:is(.chip)</c> and <c>.pane-list-head .chips</c> all land on a hoisted subject.
    /// </summary>
    private static HashSet<string> SubjectClasses(string selector)
    {
        HashSet<string> subjects = [];

        foreach (var branch in selector.Split(','))
        {
            var subject = Combinator.Split(branch.Trim()).LastOrDefault(part => part.Length > 0);

            if (subject is null)
            {
                continue;
            }

            foreach (Match token in ClassToken.Matches(subject))
            {
                subjects.Add(token.Groups[1].Value);
            }
        }

        return subjects;
    }

    /// <summary>
    /// A rule body's declarations, whitespace-normalised so a licence can be written on one line
    /// whatever the stylesheet wraps. Splitting on <c>;</c> is enough for the rules this reaches:
    /// they are the chassis's own, and none of them carries a semicolon inside a value.
    /// </summary>
    private static string[] Declarations(string body) =>
    [
        .. body
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(Normalise),
    ];

    private static string Normalise(string text) => Whitespace.Replace(text.Trim(), " ");

    /// <summary>
    /// Whether <paramref name="css"/> opens a rule on exactly <paramref name="selector"/>. Anchored
    /// at a line start or a comma so a compound never matches inside a longer one — <c>.chip</c>
    /// must not answer for <c>.chip-count</c> or for <c>.chip.is-active .chip-count</c>, and
    /// <c>.aside-figure</c> must not answer for <c>.aside-figure-value</c> or for the
    /// <c>.aside-figure + .aside-rows</c> adjacency, each of which is its own entry. Substring
    /// anchoring rather than a parse because the commas inside <c>color-mix(…)</c> make a naive
    /// selector split produce garbage.
    /// </summary>
    private static bool Defines(string css, string selector)
    {
        var compounds = selector.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(Regex.Escape);

        return Regex.IsMatch(
            css,
            $@"(?:^|,)\s*{string.Join(@"\s+", compounds)}\s*[{{,]",
            RegexOptions.Multiline);
    }

    /// <summary>
    /// Whether <paramref name="css"/> names the class <paramref name="name"/> anywhere at all —
    /// deliberately blunter than <see cref="Defines"/>, because a collision is a collision whatever
    /// compound or at-rule the other file happened to spell it in. The lookahead is what stops
    /// <c>.chip</c> answering for <c>.chip-count</c>.
    /// </summary>
    private static bool Names(string css, string name) =>
        Regex.IsMatch(css, $@"\.{Regex.Escape(name)}(?![\w-])");
}
