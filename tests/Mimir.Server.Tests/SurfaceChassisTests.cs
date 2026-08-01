using System.Text.RegularExpressions;

namespace Mimir.Server.Tests;

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

    private static readonly HashSet<string> HoistedClasses =
    [
        .. HoistedSelectors
            .SelectMany(selector => ClassToken.Matches(selector).Select(match => match.Groups[1].Value))
            // A modifier is always written onto one of these, so the base class names the owned thing.
            .Where(name => !name.StartsWith("is-", StringComparison.Ordinal)),
    ];

    private static readonly (string File, string Selector, string[] Declarations)[] ScopedDeltas =
    [
        // Places this surface's own .aside-link-text beside its .aside-link-count.
        ("Injections/InjectionLogTab.razor.css", ".aside-link",
            [
                "display: grid",
                "grid-template-columns: 1fr auto",
                "align-items: center",
                "gap: var(--space-3)",
            ]),
        // EpisodeDisplay.StateWord answers a lowercase state name; no other surface's chip labels
        // itself from one.
        ("Episodes/EpisodeList.razor.css", ".chip",
            ["text-transform: capitalize"]),
        // A state's population is the thing that filter is being chosen on.
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

    [Fact]
    public void TheScan_FlattensAtRules_SoAOneLineMediaNestedRuleIsStillARule()
        => Rules("@media (min-width: 40rem) { .chip { color: red; } }")
            .ShouldHaveSingleItem()
            .Selector.ShouldBe(".chip");

    [Theory]
    [InlineData(".chip:hover")]
    [InlineData("button.chip")]
    [InlineData(":is(.chip)")]
    [InlineData(".pane-list-head .chip")]
    [InlineData("p, .chip")]
    public void ARespeltHoistedSubject_IsStillTheHoistedClass(string selector)
        => SubjectClasses(selector).ShouldContain("chip");

    [Theory]
    [InlineData(".pane-detail-footer p")]
    [InlineData(".aside-link.is-gone .aside-link-text")]
    public void OwnMarkupUnderneathAHoistedClass_IsNotStylingIt(string selector)
        => SubjectClasses(selector).Overlaps(HoistedClasses).ShouldBeFalse();

    [Fact]
    public void Declarations_AreSplitAndWhitespaceNormalised_SoALicenceCanBeWrittenOnOneLine()
        => Declarations("""
                display:   grid;
                gap: var(--space-3);
            """)
            .ShouldBe(["display: grid", "gap: var(--space-3)"]);

    [Theory]
    [InlineData(".chip-count { color: red; }")]
    [InlineData(".chip.is-active .chip-count { color: red; }")]
    [InlineData(".pane-list-head .chip { color: red; }")]
    public void DefiningALongerCompound_IsNotDefiningTheChassisSelector(string css)
        => Defines(css, ".chip").ShouldBeFalse();

    [Theory]
    [InlineData(".chip { color: red; }")]
    [InlineData("p,\n.chip { color: red; }")]
    [InlineData(".chip,\n.pane-aside { color: red; }")]
    public void DefinesReadsAWholeCompound_WhereverInTheSelectorListItSits(string css)
        => Defines(css, ".chip").ShouldBeTrue();

    [Fact]
    public void NamesIsBlunterThanDefines_BecauseACollisionIsACollisionHoweverItIsSpelt()
    {
        Names(".chip:hover { color: red; }", "chip").ShouldBeTrue();
        Names("@media print { button.chip { color: red; } }", "chip").ShouldBeTrue();
        Names(".chip-count { color: red; }", "chip").ShouldBeFalse();
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

    private static string[] Declarations(string body) =>
    [
        .. body
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(Normalise),
    ];

    private static string Normalise(string text) => Whitespace.Replace(text.Trim(), " ");

    // Substring anchoring rather than a selector split: the commas inside color-mix(…) make a naive
    // one produce garbage.
    private static bool Defines(string css, string selector)
    {
        var compounds = selector.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(Regex.Escape);

        return Regex.IsMatch(
            css,
            $@"(?:^|,)\s*{string.Join(@"\s+", compounds)}\s*[{{,]",
            RegexOptions.Multiline);
    }

    private static bool Names(string css, string name) =>
        Regex.IsMatch(css, $@"\.{Regex.Escape(name)}(?![\w-])");
}
