namespace Mimir.Server.Ui;

public sealed class SurfaceSearch
{
    private object? _holder;

    public string Term { get; private set; } = "";

    public string? Placeholder { get; private set; }

    public bool IsClaimed => _holder is not null;

    public event Action? Changed;

    public IDisposable Claim(object holder, string placeholder)
    {
        ArgumentNullException.ThrowIfNull(holder);

        _holder = holder;
        Placeholder = placeholder;
        Term = "";
        Changed?.Invoke();
        return new Claimed(this, holder);
    }

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
