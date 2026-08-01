namespace Mimir.Server.Components.Shared;

/// <summary>
/// Whether a §8.2 confirmation is armed, and the record it is armed against. The whole of
/// <see cref="ConfirmDelete"/>'s state, kept here rather than in its <c>@code</c> block because
/// what a rule *decides* stays on this side of the renderer even now that bUnit renders components
/// (the placement ladder in <c>.claude/rules/blazor-ui.md</c>, #130): "selecting another row must
/// not carry an armed Delete onto it" is exactly such a rule, and #106 exists because a host
/// quietly stopped honouring it. <c>ConfirmArmingTests</c> pins what this decides;
/// <c>ConfirmDeleteTests</c> renders the markup that asks it, which is the half #106 actually broke.
/// </summary>
internal sealed class ConfirmArming
{
    /// <summary>
    /// The record the current arming is against. Not a nullable "never bound yet": nothing can arm
    /// before the first <see cref="Bind"/>, since Blazor sets parameters before it renders the
    /// button that would, so the two cases are the same disarmed state and a nullable would only
    /// distinguish them where nothing can look.
    /// </summary>
    private Guid _subject;

    /// <summary>Whether the consequence and its explicit choice are on screen.</summary>
    internal bool Armed { get; private set; }

    /// <summary>
    /// Points this at <paramref name="subject"/>, disarming if that is a different record from the
    /// one the arming was against. Called on every parameter set, so it must be idempotent for an
    /// unchanged subject: a re-render while the curator reads the consequence is not a reason to
    /// take it away.
    /// </summary>
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

    /// <summary>
    /// Consumes the arming: true exactly once per <see cref="Arm"/>, false if there is nothing
    /// armed to consume.
    ///
    /// The caller asks rather than checks-then-acts because a disarmed confirmation is reachable.
    /// Blazor Server holds a disposed handler's binding until the client acknowledges the render
    /// batch that dropped it — deliberately, since the browser can still dispatch against the DOM
    /// it has — so a "Delete forever" click queued behind a selection change arrives after
    /// <see cref="Bind"/> has already disarmed and repointed at the incoming record. Answering
    /// false there is what stops that click hard-deleting a record whose prompt was never on
    /// screen.
    /// </summary>
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
