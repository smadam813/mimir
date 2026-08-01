using Mimir.Server.Components.Layout;

namespace Mimir.Server.Tests.Components.Layout;

public class ProjectRouteTests
{
    [Fact]
    public void ARootPath_MatchesNothing()
        => ProjectRoute.Parse("").ShouldBeNull();

    [Fact]
    public void AProjectWithNoTab_DefaultsToEpisodes()
    {
        var id = Guid.NewGuid();

        var route = ProjectRoute.Parse($"projects/{id}");

        route.ShouldNotBeNull();
        route.Value.ProjectId.ShouldBe(id);
        route.Value.Tab.ShouldBe("episodes");
    }

    [Theory]
    [InlineData("wisdom")]
    [InlineData("episodes")]
    [InlineData("injections")]
    public void ARecognisedTab_IsReadBack(string tab)
    {
        var id = Guid.NewGuid();

        var route = ProjectRoute.Parse($"projects/{id}/{tab}");

        route.ShouldNotBeNull();
        route.Value.Tab.ShouldBe(tab);
    }

    [Fact]
    public void ATabIsCaseInsensitive()
    {
        var route = ProjectRoute.Parse($"projects/{Guid.NewGuid()}/WISDOM");

        route.ShouldNotBeNull();
        route.Value.Tab.ShouldBe("wisdom");
    }

    [Fact]
    public void ADrillDownSegment_StillReadsItsOwnTab()
    {
        var id = Guid.NewGuid();

        var route = ProjectRoute.Parse($"projects/{id}/episodes/{Guid.NewGuid()}");

        route.ShouldNotBeNull();
        route.Value.ProjectId.ShouldBe(id);
        route.Value.Tab.ShouldBe("episodes");
    }

    [Fact]
    public void AnUnrecognisedTab_FallsBackToEpisodes()
    {
        var route = ProjectRoute.Parse($"projects/{Guid.NewGuid()}/not-a-surface");

        route.ShouldNotBeNull();
        route.Value.Tab.ShouldBe("episodes");
    }

    [Fact]
    public void AMalformedProjectId_MatchesNothing()
        => ProjectRoute.Parse("projects/not-a-guid/wisdom").ShouldBeNull();

    [Fact]
    public void AQueryString_DoesNotCorruptTheTabSegment()
    {
        var id = Guid.NewGuid();

        var route = ProjectRoute.Parse($"projects/{id}/wisdom?highlight=abc");

        route.ShouldNotBeNull();
        route.Value.ProjectId.ShouldBe(id);
        route.Value.Tab.ShouldBe("wisdom");
    }

    [Fact]
    public void AQueryStringOnTheBareProjectPath_StillMatches()
    {
        var id = Guid.NewGuid();

        var route = ProjectRoute.Parse($"projects/{id}?x=1");

        route.ShouldNotBeNull();
        route.Value.ProjectId.ShouldBe(id);
        route.Value.Tab.ShouldBe("episodes");
    }
}
