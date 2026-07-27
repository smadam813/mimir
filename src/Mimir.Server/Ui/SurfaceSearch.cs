namespace Mimir.Server.Ui;

/// <summary>
/// The header's one search box and whichever surface has claimed it (spec §8): one control serves
/// all three surfaces, narrowing the list the curator is looking at rather than opening a
/// cross-surface results screen. A surface claims the box when it mounts and releases on dispose,
/// so an unclaimed box can say so instead of pretending to search.
/// <para>
/// Registered per circuit, not per install — the term is one curator's typing, and two browser
/// tabs are two curators as far as this is concerned. Pure of the database, so its rules are
/// pinned by tests that run on a machine with no Postgres.
/// </para>
/// </summary>
public sealed class SurfaceSearch
{
    private object? _holder;

    /// <summary>What the claiming surface is filtering against; empty when nothing is typed.</summary>
    public string Term { get; private set; } = "";

    /// <summary>The claiming surface's prompt, or null while no surface holds the box.</summary>
    public string? Placeholder { get; private set; }

    public bool IsClaimed => _holder is not null;

    /// <summary>Raised on every claim, release and keystroke; both sides re-render off it.</summary>
    public event Action? Changed;

    /// <summary>
    /// Claims the box for <paramref name="holder"/> — the surface component — until the returned
    /// token is disposed. The term resets on both edges: a claim starts empty, and releasing clears
    /// whatever was typed, so navigating to another surface never leaves it silently filtered by a
    /// term its own box is no longer showing. A second claim wins outright rather than throwing:
    /// Blazor mounts the incoming surface before disposing the outgoing one, so an overlap is the
    /// ordinary case, and the release of a claim another holder has superseded does nothing.
    /// A holder superseding <em>itself</em> is the exception, because the box is held by holder
    /// identity rather than by token: its earlier token stays live, so a surface re-claiming for
    /// itself releases first and leaves nothing behind to fire (#108).
    /// </summary>
    public IDisposable Claim(object holder, string placeholder)
    {
        ArgumentNullException.ThrowIfNull(holder);

        _holder = holder;
        Placeholder = placeholder;
        Term = "";
        Changed?.Invoke();
        return new Claimed(this, holder);
    }

    /// <summary>The header typing. Ignored while no surface holds the box.</summary>
    public void Set(string? term)
    {
        if (!IsClaimed)
        {
            return;
        }

        Term = term ?? "";
        Changed?.Invoke();
    }

    private void Release(object holder)
    {
        if (!ReferenceEquals(_holder, holder))
        {
            return;
        }

        _holder = null;
        Placeholder = null;
        Term = "";
        Changed?.Invoke();
    }

    private sealed class Claimed(SurfaceSearch search, object holder) : IDisposable
    {
        public void Dispose() => search.Release(holder);
    }
}
