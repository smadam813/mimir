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
}
