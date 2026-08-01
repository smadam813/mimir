using Bunit;
using Mimir.Server.Components.Wisdom;
using Mimir.Server.Storage.Entities;

namespace Mimir.Server.Tests.Components.Wisdom;

public class KindGlyphTests : RenderTestBase
{
    [Fact]
    public void TheGlyph_CarriesTheShapeItsKindAsksFor()
    {
        var glyph = Render<KindGlyph>(p => p.Add(g => g.Kind, WisdomKind.Preference));

        var span = glyph.Find("span");
        span.ClassList.ShouldBe(["kind-glyph", "glyph-diamond"], ignoreOrder: true);
        span.GetAttribute("aria-hidden").ShouldBe("true");
    }

    /// <summary>
    /// Kind is told apart by shape, never by hue (#86) — so the four have to be four, and the
    /// enum rather than a hand-list is what says how many: a §3 Kind added tomorrow either draws
    /// its own outline or lands here rather than sharing Fact's circle in silence. A set-level
    /// property of the rendered markup, which is why it is not the single-case pin above.
    /// </summary>
    [Fact]
    public void EveryKindTheDomainHas_DrawsItsOwnShape()
    {
        var shapes = Enum.GetValues<WisdomKind>()
            .Select(kind => Render<KindGlyph>(p => p.Add(g => g.Kind, kind))
                .Find("span.kind-glyph").ClassList
                .Single(c => c.StartsWith("glyph-", StringComparison.Ordinal)));

        shapes.ShouldBeUnique();
    }
}
