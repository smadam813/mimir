using Mimir.Server.Ui;

namespace Mimir.Server.Tests.Ui;

/// <summary>
/// The header's one search box and the surface that claims it (§8). Nothing here touches Postgres,
/// deliberately: the claim rules are pure, so they must run — and be able to fail — on a machine
/// with no Docker.
/// </summary>
public sealed class SurfaceSearchTests
{
    private readonly SurfaceSearch _search = new();

    [Fact]
    public void Unclaimed_TheBoxSaysSo_AndSwallowsTyping()
    {
        _search.IsClaimed.ShouldBeFalse();
        _search.Placeholder.ShouldBeNull();

        _search.Set("into the void");

        _search.Term.ShouldBe("", "an unclaimed box has no surface to narrow");
    }

    [Fact]
    public void AClaim_NamesThePrompt_AndTakesTheTyping()
    {
        using var claim = _search.Claim(this, "Search this Project's Wisdom…");

        _search.IsClaimed.ShouldBeTrue();
        _search.Placeholder.ShouldBe("Search this Project's Wisdom…");

        _search.Set("zebra");

        _search.Term.ShouldBe("zebra");
    }

    [Fact]
    public void ReleasingAClaim_ClearsTheTerm_SoTheNextSurfaceOpensUnfiltered()
    {
        var claim = _search.Claim(this, "Search…");
        _search.Set("zebra");

        claim.Dispose();

        _search.IsClaimed.ShouldBeFalse();
        _search.Term.ShouldBe("");
        _search.Placeholder.ShouldBeNull();
    }

    /// <summary>
    /// Blazor mounts the incoming surface before disposing the outgoing one, so the overlap is the
    /// ordinary case rather than an error — and the late release must not pull the box out from
    /// under the surface that now holds it.
    /// </summary>
    [Fact]
    public void AnOverlappingClaim_Wins_AndTheOutgoingReleaseIsANoOp()
    {
        var outgoing = _search.Claim(new object(), "Episodes…");
        var incoming = _search.Claim(new object(), "Wisdom…");
        _search.Set("zebra");

        outgoing.Dispose();

        _search.IsClaimed.ShouldBeTrue();
        _search.Placeholder.ShouldBe("Wisdom…");
        _search.Term.ShouldBe("zebra");

        incoming.Dispose();
        _search.IsClaimed.ShouldBeFalse();
    }

    [Fact]
    public void EveryEdge_RaisesChanged_SoBothSidesRedraw()
    {
        var raised = 0;
        _search.Changed += () => raised++;

        var claim = _search.Claim(this, "Search…");
        _search.Set("zeb");
        claim.Dispose();

        raised.ShouldBe(3);
    }

    [Fact]
    public void AClaimWithoutAHolder_IsRejected()
    {
        Should.Throw<ArgumentNullException>(() => _search.Claim(null!, "Search…"));
    }
}
