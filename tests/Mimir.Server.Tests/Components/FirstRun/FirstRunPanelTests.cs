using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using Mimir.Server.Components.FirstRun;

namespace Mimir.Server.Tests.Components.FirstRun;

/// <summary>
/// The first-run panel's one piece of JavaScript, and the three things that can happen to it.
/// <see cref="FirstRunCommandsTests"/> pins what the registrations say; this pins that the panel
/// hands the clipboard the same constants it printed, and that neither a refusal nor a teardown
/// mid-import leaves the curator with a broken screen.
/// <para>
/// Disconnected tier, and it has to be: a first run is exactly the machine where nothing is
/// provisioned yet, so a pin that skipped without Postgres would skip on the install it is about.
/// </para>
/// </summary>
public class FirstRunPanelTests : RenderTestBase
{
    private const string Blocked = "Copying is blocked here — select the blocks above instead.";

    /// <summary>
    /// The panel's own module, stood in for so the test owns both answers <c>copyText</c> can give
    /// and can see the release. bUnit's loose-mode stub answers <c>default</c> to everything, which
    /// would make "refused" and "never called" the same observation; its strict
    /// <c>SetupModule</c> hands out a module of its own, which cannot report being disposed.
    /// </summary>
    private sealed class FakeModule : IJSObjectReference
    {
        /// <summary>What the clipboard was handed, or null if it was never asked.</summary>
        internal string? Copied { get; private set; }

        internal bool Disposed { get; private set; }

        internal bool Answer { get; init; } = true;

        internal Exception? Throws { get; init; }

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
        {
            identifier.ShouldBe("copyText");
            if (Throws is not null)
            {
                throw Throws;
            }

            Copied = (string?)args?.Single();
            return ValueTask.FromResult((TValue)(object)Answer);
        }

        public ValueTask<TValue> InvokeAsync<TValue>(
            string identifier, CancellationToken cancellationToken, object?[]? args)
            => InvokeAsync<TValue>(identifier, args);

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>
    /// Answers the panel's one <c>import</c>, and only when the test says so — the teardown case
    /// below is entirely about what happens while that import is still in flight, so the moment it
    /// completes has to be the test's to choose.
    /// </summary>
    private sealed class PendingImport : IJSRuntime
    {
        private readonly TaskCompletionSource<IJSObjectReference> _import = new();

        internal void Lands(IJSObjectReference module) => _import.SetResult(module);

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
        {
            identifier.ShouldBe("import");
            return new ValueTask<TValue>(_import.Task.ContinueWith(
                t => (TValue)t.Result, TaskScheduler.Default));
        }

        public ValueTask<TValue> InvokeAsync<TValue>(
            string identifier, CancellationToken cancellationToken, object?[]? args)
            => InvokeAsync<TValue>(identifier, args);
    }

    private readonly PendingImport _js = new();

    public FirstRunPanelTests()
    {
        Services.AddSingleton(new ResourceAssetCollection([]));
        Services.AddSingleton<IJSRuntime>(_js);
        Services.AddLogging();
    }

    /// <summary>
    /// AngleSharp normalises CRLF to LF while parsing the rendered markup, and the constants carry
    /// whatever the checkout's line endings are (CRLF on Windows, LF in CI) — so the comparison is
    /// made on the separators' behalf rather than pinning one platform's.
    /// </summary>
    private static string Normalised(string text) => text.ReplaceLineEndings("\n");

    /// <summary>
    /// The rendered blocks and the clipboard payload come from one keeper, so "Copy both commands"
    /// cannot hand over something other than what is on the screen — the failure mode a curator
    /// would never catch, because the thing they pasted is the thing they could not see.
    /// </summary>
    [Fact]
    public void TheRenderedRegistrations_AreTheOnesTheClipboardWouldReceive()
    {
        var panel = Render<FirstRunPanel>();

        var printed = panel.FindAll("pre.first-run-command")
            .Select(b => Normalised(b.TextContent)).ToArray();
        printed.ShouldBe([Normalised(FirstRunCommands.Hooks), Normalised(FirstRunCommands.Mcp)]);
        foreach (var block in printed)
        {
            Normalised(FirstRunCommands.Both).ShouldContain(block);
        }
    }

    [Fact]
    public async Task CopyingSuccessfully_SaysSoAndSendsBothRegistrations()
    {
        var module = new FakeModule();
        var panel = Render<FirstRunPanel>();
        var copying = panel.Find("button.btn-primary").ClickAsync(new());

        _js.Lands(module);
        await copying;

        module.Copied.ShouldBe(FirstRunCommands.Both);
        panel.Find("span.first-run-copy-state").TextContent.ShouldBe("Both registrations copied.");
    }

    /// <summary>
    /// A browser that refuses the write — the Clipboard API is unavailable outside a secure
    /// context, so a http:// LAN address is exactly this case — gets the fallback rather than a
    /// claim that the copy happened. The commands stay selectable on screen, which is why one
    /// message covers this and the throw below: the curator's next move is the same either way.
    /// </summary>
    [Fact]
    public async Task ARefusedWrite_NamesTheFallbackRatherThanClaimingSuccess()
    {
        var panel = Render<FirstRunPanel>();
        var copying = panel.Find("button.btn-primary").ClickAsync(new());

        _js.Lands(new FakeModule { Answer = false });
        await copying;

        panel.Find("span.first-run-copy-state").TextContent.ShouldBe(Blocked);
    }

    /// <summary>
    /// The other failure mode — the call never got that far — is not an error boundary either. A
    /// tearing-down circuit throws here, and swapping the whole first-run screen for an error page
    /// because a clipboard call lost its race would be the worse outcome by a distance.
    /// </summary>
    [Fact]
    public async Task AThrownCall_LandsOnTheSameFallbackAndNotAnErrorBoundary()
    {
        var panel = Render<FirstRunPanel>();
        var copying = panel.Find("button.btn-primary").ClickAsync(new());

        _js.Lands(new FakeModule { Throws = new JSDisconnectedException("gone") });
        await copying;

        panel.Find("span.first-run-copy-state").TextContent.ShouldBe(Blocked);
    }

    /// <summary>
    /// The panel is swapped out the moment the first hook lands, which can be mid-import if the
    /// curator copies as their first session starts. <c>DisposeAsync</c> ran with no module to
    /// release, so the import that lands afterwards has to release its own — otherwise the JS
    /// reference is held by the server for the life of the circuit with nothing left to use it.
    /// <para>
    /// The import is left pending across the teardown deliberately: that window is the whole rule,
    /// and it is unreachable if the module arrives before <c>DisposeAsync</c> is called.
    /// </para>
    /// </summary>
    [Fact]
    public async Task AnImportLandingAfterTeardown_ReleasesItsOwnModule()
    {
        var panel = Render<FirstRunPanel>();
        var copying = panel.Find("button.btn-primary").ClickAsync(new());

        await panel.Instance.DisposeAsync();
        var module = new FakeModule();
        _js.Lands(module);
        await copying;

        module.Disposed.ShouldBeTrue();
        module.Copied.ShouldBeNull();
    }
}
