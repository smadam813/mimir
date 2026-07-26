using Mimir.Server.Ui;

namespace Mimir.Server.Tests.Ui;

/// <summary>
/// The header's one search box and the surface that owns it (§8): claimed on mount, released on
/// unmount, and disabled in between. Postgres-free — it is the wire between two components and
/// touches no storage at all.
/// </summary>
public class SurfaceSearchTests
{
    [Fact]
    public void ABoxNoSurfaceHasClaimed_IsUnclaimedAndUnnamed()
    {
        var search = new SurfaceSearch();

        search.IsClaimed.ShouldBeFalse();
        search.Placeholder.ShouldBeNull();
        search.Term.ShouldBe("");
    }

    [Fact]
    public async Task AClaimedBox_CarriesTheSurfacesWording_AndForwardsWhatIsTyped()
    {
        var search = new SurfaceSearch();
        var received = new List<string>();

        using var claim = search.Claim("Search this log…", term =>
        {
            received.Add(term);
            return Task.CompletedTask;
        });
        await search.SetTermAsync("migrations");

        search.IsClaimed.ShouldBeTrue();
        search.Placeholder.ShouldBe("Search this log…");
        search.Term.ShouldBe("migrations");
        received.ShouldBe(["migrations"]);
    }

    [Fact]
    public async Task ReleasingTheClaim_HandsTheBoxBackDisabled_AndStopsForwarding()
    {
        var search = new SurfaceSearch();
        var received = new List<string>();
        var claim = search.Claim("Search this log…", term =>
        {
            received.Add(term);
            return Task.CompletedTask;
        });

        claim.Dispose();
        await search.SetTermAsync("migrations");

        search.IsClaimed.ShouldBeFalse();
        search.Placeholder.ShouldBeNull();
        search.Term.ShouldBe("");
        received.ShouldBeEmpty();
    }

    [Fact]
    public async Task AStaleRelease_DoesNotDisarmTheSurfaceThatClaimedAfterIt()
    {
        // Blazor constructs the incoming surface before disposing the outgoing one, so the release
        // that arrives second belongs to the surface that left first.
        var search = new SurfaceSearch();
        var outgoing = search.Claim("Search Wisdom…", _ => Task.CompletedTask);
        var received = new List<string>();
        using var incoming = search.Claim("Search this log…", term =>
        {
            received.Add(term);
            return Task.CompletedTask;
        });

        outgoing.Dispose();
        await search.SetTermAsync("migrations");

        search.IsClaimed.ShouldBeTrue();
        search.Placeholder.ShouldBe("Search this log…");
        received.ShouldBe(["migrations"]);
    }

    [Fact]
    public async Task ANewClaim_EmptiesTheTerm_WordsTypedAtOneSurfaceMeanNothingAtTheNext()
    {
        var search = new SurfaceSearch();
        var first = search.Claim("Search Wisdom…", _ => Task.CompletedTask);
        await search.SetTermAsync("migrations");
        first.Dispose();

        using var next = search.Claim("Search this log…", _ => Task.CompletedTask);

        search.Term.ShouldBe("");
    }

    [Fact]
    public async Task TheGeneration_MovesOnEveryClaimAndRelease_AndNeverOnATerm()
    {
        // It is the header input's @key, and the input carries no bound value — so this moving is
        // the only thing that can empty the box, and its moving while a curator types would empty
        // it mid-word. Two surfaces wording their box identically (the same tab on a second
        // Project) are told apart by this and nothing else.
        var search = new SurfaceSearch();
        var atRest = search.Generation;

        var first = search.Claim("Search this Project's injections…", _ => Task.CompletedTask);
        var claimed = search.Generation;
        await search.SetTermAsync("migrations");
        var typed = search.Generation;
        first.Dispose();
        using var second = search.Claim("Search this Project's injections…", _ => Task.CompletedTask);

        claimed.ShouldNotBe(atRest);
        typed.ShouldBe(claimed);
        search.Generation.ShouldNotBe(claimed);
    }

    [Fact]
    public async Task EveryClaimReleaseAndTerm_RaisesChangedForTheHeaderToRenderOn()
    {
        var search = new SurfaceSearch();
        var changes = 0;
        search.Changed += () => changes++;

        var claim = search.Claim("Search this log…", _ => Task.CompletedTask);
        await search.SetTermAsync("migrations");
        claim.Dispose();

        changes.ShouldBe(3);
    }

    [Fact]
    public async Task TypingIntoAnUnclaimedBox_IsIgnoredRatherThanRemembered()
    {
        var search = new SurfaceSearch();
        var changes = 0;
        search.Changed += () => changes++;

        await search.SetTermAsync("migrations");

        search.Term.ShouldBe("");
        changes.ShouldBe(0);
    }
}
