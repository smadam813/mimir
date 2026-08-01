using Bunit;

namespace Mimir.Server.Tests.Components;

/// <summary>
/// The disconnected render tier (#130): a bUnit context with no database behind it, for a
/// component whose whole behaviour arrives through its parameters — <c>ConfirmDelete</c>,
/// <c>KindGlyph</c>. Nothing here touches Postgres, so these pins run and fail on a machine with
/// no Docker, which is the whole point of the tier: the "never inherit
/// <see cref="PostgresTestBase"/> without issuing SQL" rule decides the tier, unchanged.
/// <para>
/// A surface that injects one of the <c>Ui/</c> browsers belongs on the other tier instead —
/// <see cref="PostgresTestBase.CreateRenderContext"/>, which builds a context over the same
/// seeders, fakes and truncation-reset the rest of the harness hands out.
/// </para>
/// <para>
/// It <em>is</em> the renderer rather than holding one: <see cref="BunitContext"/> is public and
/// non-sealed precisely to be a test class's base, and it already ships both dispose paths with
/// virtual hooks. A wrapper would forfeit the async one — which xunit.v3 prefers — and would go
/// silently unrun the day a derived class implemented <c>IAsyncDisposable</c> for a resource of
/// its own.
/// </para>
/// </summary>
public abstract class RenderTestBase : BunitContext;
