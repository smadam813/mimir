using Mimir.Server.Components.Wisdom;
using Mimir.Server.Ui;

namespace Mimir.Server.Tests.Components.Wisdom;

public sealed class WisdomRouteTests
{
    private static readonly Guid ProjectId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid WisdomId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    [Fact]
    public void TheDefaultLens_NeedsNoQueryAtAll()
    {
        WisdomRoute.Listing(ProjectId, WisdomLens.Active)
            .ShouldBe($"projects/{ProjectId}/wisdom");
        WisdomRoute.Detail(ProjectId, WisdomId, WisdomLens.Active)
            .ShouldBe($"projects/{ProjectId}/wisdom/{WisdomId}");
    }

    [Theory]
    [InlineData(WisdomLens.Contested, "contested")]
    [InlineData(WisdomLens.Orphaned, "orphaned")]
    [InlineData(WisdomLens.Retired, "retired")]
    public void EveryOtherLens_RidesTheQuery_AndReadsBackAsItself(WisdomLens lens, string spelled)
    {
        var listing = WisdomRoute.Listing(ProjectId, lens);
        var detail = WisdomRoute.Detail(ProjectId, WisdomId, lens);

        listing.ShouldBe($"projects/{ProjectId}/wisdom?show={spelled}");
        detail.ShouldBe($"projects/{ProjectId}/wisdom/{WisdomId}?show={spelled}");
        WisdomRoute.LensOf(new Uri($"http://localhost/{listing}")).ShouldBe(lens);
        WisdomRoute.LensOf(new Uri($"http://localhost/{detail}")).ShouldBe(lens);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("nonsense")]
    public void AnUnknownLens_LandsOnTheDefaultListing(string? value)
    {
        WisdomRoute.ParseLens(value).ShouldBe(WisdomLens.Active);
    }

    [Fact]
    public void AMissingQuery_LandsOnTheDefaultListing()
    {
        WisdomRoute.LensOf(new Uri($"http://localhost/projects/{ProjectId}/wisdom"))
            .ShouldBe(WisdomLens.Active);
    }

    [Fact]
    public void TheLensName_IsCaseInsensitive_SoAPastedUrlStillResolves()
    {
        WisdomRoute.LensOf(new Uri($"http://localhost/projects/{ProjectId}/wisdom?show=Retired"))
            .ShouldBe(WisdomLens.Retired);
    }
}
