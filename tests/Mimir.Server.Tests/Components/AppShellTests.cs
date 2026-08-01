namespace Mimir.Server.Tests.Components;

/// <summary>
/// Spec §8's one-line premise, read off the shell as text. The whole UI renders Interactive
/// Server, and the SignalR circuit that render mode opens is what pushes live health, unsealed
/// Episodes and queue depth without anything polling — every debounced feed subscriber in
/// <c>Components/</c> is built on that being true. Drop the render mode and the app still boots,
/// still routes and still looks right on first paint; what stops is everything arriving after it.
/// <para>
/// A text scan rather than a render test, and one of the few places that is the honest instrument:
/// <c>@rendermode</c> is resolved by the host at prerender, above the renderer bUnit gives out, so
/// neither tier can see it. <c>App.razor</c> is copied into the test output the way
/// <c>OfflineAssetsTests</c>' stylesheets are.
/// </para>
/// </summary>
public class AppShellTests
{
    private static readonly string ShellPath =
        Path.Combine(AppContext.BaseDirectory, "Components", "App.razor");

    private static string Shell() => File.ReadAllText(ShellPath);

    [Fact]
    public void TheRouter_IsMountedInteractive()
        => Shell().ShouldContain("""<Routes @rendermode="InteractiveServer" />""");

    /// <summary>
    /// The head goes with it: a static <c>HeadOutlet</c> leaves every <c>PageTitle</c> frozen at
    /// its prerendered value, so the tab keeps naming the Project the curator navigated away from.
    /// </summary>
    [Fact]
    public void TheHeadOutlet_IsMountedInteractiveToo()
        => Shell().ShouldContain("""<HeadOutlet @rendermode="InteractiveServer" />""");

    /// <summary>
    /// "The whole UI", stated as the absence of an exception: no second render mode is named
    /// anywhere in the shell. A page opting itself into static rendering would keep the two
    /// assertions above green while quietly leaving one surface unable to receive anything.
    /// </summary>
    [Fact]
    public void NoOtherRenderMode_IsNamedInTheShell()
        => Shell().Split("@rendermode").Skip(1)
            .ShouldAllBe(after => after.TrimStart().StartsWith(
                "=\"InteractiveServer\"", StringComparison.Ordinal));
}
