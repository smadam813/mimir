namespace Mimir.Server.Ui;

/// <summary>
/// The chassis's one search box (spec §8): a surface claims it while it is mounted and the header
/// hands over whatever the curator types, so one control serves all three surfaces without anyone
/// learning three. Unclaimed it renders disabled — the state every unported surface leaves it in.
///
/// Scoped, so the claim and the term live and die with the Blazor circuit: two browser tabs on two
/// Projects each narrow their own list. Pure C# with no database behind it, which is why its rules
/// are pinned without Postgres.
/// </summary>
public sealed class SurfaceSearch
{
    private Claim? _claim;

    /// <summary>What the box invites the curator to type; null while nothing claims it.</summary>
    public string? Placeholder => _claim?.Placeholder;

    /// <summary>The term the claimant is narrowing by. Empty whenever nothing is claimed.</summary>
    public string Term { get; private set; } = string.Empty;

    /// <summary>
    /// Raised when a surface claims or releases the box, so the header re-renders enabled or
    /// disabled — and drops whatever the last surface's term left in the input.
    /// </summary>
    public event Action? ClaimChanged;

    /// <summary>
    /// Claims the box for one surface. Dispose the result to release it — on the claimant's own
    /// <c>Dispose</c>, so navigating to an unported surface disables the box again.
    /// </summary>
    public IDisposable ClaimBy(string placeholder, Func<string, Task> narrow)
    {
        var claim = new Claim(this, placeholder, narrow);
        _claim = claim;
        Term = string.Empty;
        ClaimChanged?.Invoke();
        return claim;
    }

    /// <summary>
    /// The curator typed. Dropped when nothing is claimed — the box is disabled then, but a
    /// half-torn-down circuit must not leave a term behind for the next surface to inherit.
    /// </summary>
    public Task EnterAsync(string term)
    {
        if (_claim is not { } claim)
        {
            return Task.CompletedTask;
        }

        Term = term;
        return claim.Narrow(term);
    }

    /// <summary>
    /// Releases <paramref name="claim"/>, unless a newer surface has already claimed the box:
    /// Blazor may initialize the incoming surface before disposing the outgoing one, and a stale
    /// release would then disable a box the new surface is serving.
    /// </summary>
    private void Release(Claim claim)
    {
        if (!ReferenceEquals(_claim, claim))
        {
            return;
        }

        _claim = null;
        Term = string.Empty;
        ClaimChanged?.Invoke();
    }

    private sealed class Claim(SurfaceSearch search, string placeholder, Func<string, Task> narrow)
        : IDisposable
    {
        public string Placeholder { get; } = placeholder;

        public Func<string, Task> Narrow { get; } = narrow;

        public void Dispose() => search.Release(this);
    }
}
