namespace Mimir.Server.Ui;

/// <summary>
/// The header's one search box, and whichever surface currently owns it (spec §8, #89: "the search
/// box renders disabled until a surface claims it"). One control serves all three surfaces because
/// each one claims it on mount and releases it on dispose — the header never learns what any of
/// them search, only what to call it and where to send the term.
///
/// Scoped, so the claim is per circuit: two browser tabs on two surfaces each get their own, and a
/// Singleton would have one narrow the other's list. Nothing here touches the database; it is the
/// wire between two components that cannot see each other, which is why it lives beside the
/// browsers rather than inside either component.
/// </summary>
public sealed class SurfaceSearch
{
    private Func<string, Task>? _handler;

    /// <summary>What the header calls the box — null while no surface has claimed it.</summary>
    public string? Placeholder { get; private set; }

    /// <summary>The term the claiming surface last received; "" whenever nothing is claimed.</summary>
    public string Term { get; private set; } = "";

    public bool IsClaimed => _handler is not null;

    /// <summary>
    /// Counts claims and releases, and nothing else — never a term. It is what the header keys its
    /// input on: the box carries no bound value (a debounced round trip would otherwise land
    /// mid-word and move the caret), so a new claim can only empty it by making it a new element.
    /// Two surfaces wording their box identically — the same tab on a second Project — would not
    /// be told apart by the placeholder alone.
    /// </summary>
    public int Generation { get; private set; }

    /// <summary>Raised whenever the claim or the term changes, for the header to re-render on.</summary>
    public event Action? Changed;

    /// <summary>
    /// Claims the box for one surface. The term resets, because a term typed against the Wisdom
    /// list means nothing to the injection log — carrying it across would leave the new surface
    /// silently narrowed by words the curator typed at a different one.
    /// </summary>
    /// <returns>The release; disposing it hands the box back, disabled.</returns>
    public IDisposable Claim(string placeholder, Func<string, Task> onTerm)
    {
        _handler = onTerm;
        Placeholder = placeholder;
        Term = "";
        Generation++;
        Changed?.Invoke();
        return new Release(this, onTerm);
    }

    /// <summary>
    /// The header's own edit. Ignored while nothing is claimed — the box renders disabled then, so
    /// the only way here is a race with a surface being torn down.
    /// </summary>
    public async Task SetTermAsync(string term)
    {
        if (_handler is not { } handler)
        {
            return;
        }

        Term = term;
        Changed?.Invoke();
        await handler(term);
    }

    private sealed class Release(SurfaceSearch search, Func<string, Task> handler) : IDisposable
    {
        public void Dispose()
        {
            // Only if this claim is still the live one: Blazor disposes the outgoing surface after
            // constructing the incoming one, so a release that did not check would hand the box
            // back disabled the moment the next surface had already claimed it.
            if (!ReferenceEquals(search._handler, handler))
            {
                return;
            }

            search._handler = null;
            search.Placeholder = null;
            search.Term = "";
            search.Generation++;
            search.Changed?.Invoke();
        }
    }
}
