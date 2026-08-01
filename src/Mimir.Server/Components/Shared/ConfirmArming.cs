namespace Mimir.Server.Components.Shared;

internal sealed class ConfirmArming
{
    // Non-nullable rather than "never bound yet": nothing can arm before the first Bind, so the
    // two cases are one disarmed state and a nullable would distinguish them where nothing looks.
    private Guid _subject;

    internal bool Armed { get; private set; }

    internal void Bind(Guid subject)
    {
        if (_subject == subject)
        {
            return;
        }

        _subject = subject;
        Armed = false;
    }

    internal void Arm() => Armed = true;

    internal void Disarm() => Armed = false;

    // Asks rather than checks-then-acts because a disarmed confirmation stays reachable: Blazor
    // Server keeps a disposed handler's binding alive until the client acknowledges the render
    // batch that dropped it, since the browser can still dispatch against the DOM it has.
    internal bool TryConfirm()
    {
        if (!Armed)
        {
            return false;
        }

        Armed = false;
        return true;
    }
}
