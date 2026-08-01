using Bunit;
using Mimir.Server.Components.Wisdom;
using Mimir.Server.Storage.Entities;

namespace Mimir.Server.Tests.Components.Wisdom;

/// <summary>
/// The render harness's own smoke pin (#130's compat spike): the smallest component in the app,
/// rendered on the disconnected tier. If bUnit ever stops agreeing with the repo's xunit.v3 or its
/// .NET version, this is the test that says so first, without a database or a fake in the way.
/// <para>
/// One case, not four. Which glyph belongs to which Kind is a rule about what is <em>computed</em>,
/// and the ladder keeps those off the renderer (<c>.claude/rules/blazor-ui.md</c>); what is pinned
/// here is that a parameter reaches the markup at all.
/// </para>
/// </summary>
public class KindGlyphTests : RenderTestBase
{
    [Fact]
    public void TheGlyph_CarriesTheShapeItsKindAsksFor()
    {
        var glyph = Render<KindGlyph>(p => p.Add(g => g.Kind, WisdomKind.Preference));

        var span = glyph.Find("span");
        span.ClassList.ShouldBe(["kind-glyph", "glyph-diamond"], ignoreOrder: true);
        // Decorative: every caller writes the Kind's word beside it, and that is what is read out.
        span.GetAttribute("aria-hidden").ShouldBe("true");
    }
}
