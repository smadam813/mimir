namespace Mimir.Server.Components.Shared;

/// <summary>
/// Whether a §8.2 confirmation is armed, and the record it is armed against. The whole of
/// <see cref="ConfirmDelete"/>'s state, kept here rather than in its <c>@code</c> block because
/// nothing renders a component in a test in this repo: a rule that must hold lives in a pure
/// companion the tests can reach (CLAUDE.md), and "selecting another row must not carry an armed
/// Delete onto it" is exactly such a rule — #106 exists because a host quietly stopped honouring
/// it and no test could notice.
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
    /// The record a confirmation right now would take. Read back out rather than left for the host
    /// to re-derive: a host's own notion of what is selected can already have moved on to the row
    /// whose read has not landed yet, while what the curator is looking at — and what the prompt
    /// they are agreeing to describes — is still this one.
    /// </summary>
    internal Guid Subject => _subject;

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
}
