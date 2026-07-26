using Mimir.Server.Ui;

namespace Mimir.Server.Tests.Ui;

/// <summary>
/// The chassis's one search box (§8, #94): who owns it, what the claimant hears, and what happens
/// when a surface hands it on. No database — the whole class is one object's state machine.
/// </summary>
public sealed class SurfaceSearchTests
{
    private readonly SurfaceSearch _search = new();

    private readonly List<string> _heard = [];

    private int _claimChanges;

    public SurfaceSearchTests() => _search.ClaimChanged += () => _claimChanges++;

    [Fact]
    public void Unclaimed_ItHasNoPlaceholderAndNoTerm()
    {
        _search.Placeholder.ShouldBeNull();
        _search.Term.ShouldBeEmpty();
    }

    [Fact]
    public void ASurfaceClaimingIt_NamesWhatToType_AndTheHeaderIsTold()
    {
        _search.ClaimBy("Search these Episodes", Narrow);

        _search.Placeholder.ShouldBe("Search these Episodes");
        _claimChanges.ShouldBe(1);
    }

    [Fact]
    public async Task WhatTheCuratorTypes_ReachesTheClaimant()
    {
        _search.ClaimBy("Search these Episodes", Narrow);

        await _search.EnterAsync("interceptor");

        _heard.ShouldBe(["interceptor"]);
        _search.Term.ShouldBe("interceptor");
    }

    [Fact]
    public async Task TypingWithNothingClaimed_IsDropped()
    {
        await _search.EnterAsync("interceptor");

        _search.Term.ShouldBeEmpty();
        _heard.ShouldBeEmpty();
    }

    [Fact]
    public async Task ReleasingIt_DisablesTheBox_AndForgetsTheTerm()
    {
        var claim = _search.ClaimBy("Search these Episodes", Narrow);
        await _search.EnterAsync("interceptor");

        claim.Dispose();

        _search.Placeholder.ShouldBeNull();
        _search.Term.ShouldBeEmpty();
        _claimChanges.ShouldBe(2);
    }

    [Fact]
    public async Task ANewClaim_StartsFromAnEmptyTerm_SoNoSurfaceInheritsAnothersSearch()
    {
        _search.ClaimBy("Search these Episodes", Narrow);
        await _search.EnterAsync("interceptor");

        _search.ClaimBy("Search this Wisdom", Narrow);

        _search.Term.ShouldBeEmpty();
        _search.Placeholder.ShouldBe("Search this Wisdom");
    }

    [Fact]
    public async Task AReleaseFromTheSurfaceJustLeft_LeavesTheNewOnesClaimStanding()
    {
        // Blazor may initialize the incoming surface before disposing the outgoing one, so the
        // stale Dispose lands after the new claim. It must not disable a box someone is serving.
        var outgoing = _search.ClaimBy("Search these Episodes", Narrow);
        _search.ClaimBy("Search this Wisdom", Narrow);

        outgoing.Dispose();

        _search.Placeholder.ShouldBe("Search this Wisdom");
        await _search.EnterAsync("dotnet");
        _heard.ShouldBe(["dotnet"]);
    }

    /// <summary>What a claimant does: re-read the term off the service, its one channel.</summary>
    private Task Narrow()
    {
        _heard.Add(_search.Term);
        return Task.CompletedTask;
    }
}
