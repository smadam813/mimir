using Bunit;
using Microsoft.AspNetCore.Components;

namespace Mimir.Server.Tests.Components;

/// <summary>
/// Waiting for a live surface to stop moving. Every §8 surface schedules a debounced refresh from
/// its own mount — <c>SurfaceSearch.Claim</c> raises <c>Changed</c>, and the search lane picks it
/// up — so for a quarter-second after the first query lands the surface will re-render again
/// unprompted. A test that finds a button in that window and clicks it afterwards gets
/// <c>UnknownEventHandlerIdException</c>: the handler id it captured belonged to the render before.
/// <para>
/// So the fix is not a longer timeout, it is waiting for quiescence — and it lives here, once,
/// rather than as a hand-tuned <c>Task.Delay</c> per test. <c>Debouncer</c> hard-codes 250 ms with
/// no <c>TimeProvider</c> seam (<c>DebouncerTests</c> records why), so a real wait is the only
/// instrument available today; <c>.claude/rules/tests.md</c> and #144 carry that argument. One
/// place to change is the point: when that seam gains a quiescence hook, this is the body that
/// changes and no test does.
/// </para>
/// </summary>
internal static class RenderQuiescence
{
    /// <summary>
    /// How long the surface must go unrendered before it counts as settled. Comfortably past
    /// <c>Debouncer.DefaultDelay</c>, since what is being waited out is one scheduled refresh plus
    /// the query it runs.
    /// </summary>
    private static readonly TimeSpan Quiet = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// How long a Postgres-tier render test waits for rows. Seconds rather than milliseconds
    /// because what it is usually waiting on is the *second* query — the claim taken on mount
    /// supersedes the first — plus the run's first EF model build and Npgsql connect. Beside
    /// <see cref="Quiet"/> because the two are the same measurement seen from either end.
    /// </summary>
    internal static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Returns once <paramref name="component"/> has gone <see cref="Quiet"/> without rendering.
    /// Anything that dispatches an event — a click, a keystroke — wants this first.
    /// </summary>
    internal static async Task SettleAsync<TComponent>(this IRenderedComponent<TComponent> component)
        where TComponent : IComponent
    {
        var renders = -1;
        while (renders != component.RenderCount)
        {
            renders = component.RenderCount;
            await Task.Delay(Quiet, TestContext.Current.CancellationToken);
        }
    }

    /// <summary>
    /// Finds and clicks the one element matching <paramref name="selector"/> whose trimmed text is
    /// <paramref name="label"/>, both inside one dispatch so no render can land between them —
    /// which is the other half of the fix, since a handler id captured by an earlier <c>Find</c>
    /// is exactly what goes stale.
    /// </summary>
    internal static Task ClickAsync<TComponent>(
        this IRenderedComponent<TComponent> component, string selector, string label)
        where TComponent : IComponent
        => component.InvokeAsync(() => component.FindAll(selector)
            .Single(e => e.TextContent.Trim() == label).Click());

    /// <summary>Same dispatch guarantee, for the sole element matching <paramref name="selector"/>.</summary>
    internal static Task ClickAsync<TComponent>(
        this IRenderedComponent<TComponent> component, string selector)
        where TComponent : IComponent
        => component.InvokeAsync(() => component.Find(selector).Click());
}
