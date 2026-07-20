using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Mimir.Cli.Tests;

/// <summary>
/// The hook relay at its public boundary: stdin JSON in, one HTTP POST out, stdout and exit code
/// back. The fail-open rules (spec §4) are the contract that matters most — a dead Mimir must
/// never break or slow a session.
/// </summary>
public class HookCommandTests
{
    [Theory]
    [InlineData("SessionStart")]
    [InlineData("UserPromptSubmit")]
    [InlineData("PostToolUse")]
    [InlineData("Stop")]
    [InlineData("SessionEnd")]
    public async Task ADeadServer_MeansExitZeroNoOutputWellUnderTheCap(string hookEvent)
    {
        using var http = new HttpClient { BaseAddress = ClosedPort() };
        var output = new StringWriter();
        var stopwatch = Stopwatch.StartNew();

        var exitCode = await new HookCommand(http, new StringReader(Stdin()), output).RunAsync(hookEvent);

        stopwatch.Stop();
        exitCode.ShouldBe(0);
        output.ToString().ShouldBeEmpty();
        stopwatch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(3), "a refused connection must fail fast, not eat the cap");
    }

    [Fact]
    public async Task GarbageStdin_StillExitsZeroSilently()
    {
        using var http = new HttpClient { BaseAddress = ClosedPort() };
        var output = new StringWriter();

        var exitCode = await new HookCommand(http, new StringReader("not json at all"), output).RunAsync("Stop");

        exitCode.ShouldBe(0);
        output.ToString().ShouldBeEmpty();
    }

    [Fact]
    public async Task UserPromptSubmit_IsOneRoundTripThatPrintsTheInjection()
    {
        var handler = new RecordingHandler("""{"injection":"remembered wisdom"}""");
        var output = await RunAsync(handler, "UserPromptSubmit", Stdin(prompt: "how do I deploy?"));

        handler.Path.ShouldBe("/api/hooks/user-prompt");
        output.Trim().ShouldBe("remembered wisdom");

        var sent = JsonDocument.Parse(handler.Body!).RootElement;
        sent.GetProperty("sessionId").GetString().ShouldBe("sess-123");
        sent.GetProperty("hookEvent").GetString().ShouldBe("UserPromptSubmit");
        sent.GetProperty("payload").GetProperty("prompt").GetString().ShouldBe("how do I deploy?");
    }

    [Fact]
    public async Task AnEmptyInjection_PrintsNothingAtAll()
    {
        var handler = new RecordingHandler("""{"injection":""}""");
        var output = await RunAsync(handler, "UserPromptSubmit", Stdin());

        output.ShouldBeEmpty();
    }

    [Fact]
    public async Task SessionStart_PostsToItsRouteAndPrintsTheBrief()
    {
        var handler = new RecordingHandler("""{"brief":"the brief"}""");
        var output = await RunAsync(handler, "SessionStart", Stdin());

        handler.Path.ShouldBe("/api/hooks/session-start");
        output.Trim().ShouldBe("the brief");
    }

    [Theory]
    [InlineData("PostToolUse")]
    [InlineData("Stop")]
    [InlineData("SessionEnd")]
    public async Task AsyncCaptureEvents_PostToTheCaptureRouteAndPrintNothing(string hookEvent)
    {
        var handler = new RecordingHandler("{}");
        var output = await RunAsync(handler, hookEvent, Stdin());

        handler.Path.ShouldBe("/api/capture/events");
        output.ShouldBeEmpty();
    }

    [Fact]
    public async Task TheWholeStdinTravelsAsThePayload_WithHostResolvedIdentity()
    {
        var cwd = Directory.CreateTempSubdirectory("mimir-hook-test-").FullName;
        try
        {
            var handler = new RecordingHandler("{}");
            await RunAsync(handler, "PostToolUse", Stdin(cwd: cwd));

            var sent = JsonDocument.Parse(handler.Body!).RootElement;
            sent.GetProperty("cwd").GetString().ShouldBe(cwd);
            sent.GetProperty("projectIdentity").GetString().ShouldBe(cwd, "a non-repo cwd is its own identity (spec §3.1)");
            sent.GetProperty("projectRoot").GetString().ShouldBe(cwd);
            sent.GetProperty("payload").GetProperty("tool_name").GetString().ShouldBe("Bash");
        }
        finally
        {
            Directory.Delete(cwd, recursive: true);
        }
    }

    private static async Task<string> RunAsync(RecordingHandler handler, string hookEvent, string stdin)
    {
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://mimir.test") };
        var output = new StringWriter();

        var exitCode = await new HookCommand(http, new StringReader(stdin), output).RunAsync(hookEvent);

        exitCode.ShouldBe(0);
        return output.ToString();
    }

    /// <summary>A realistic Claude Code hook stdin document.</summary>
    private static string Stdin(string? cwd = null, string? prompt = null)
    {
        var fields = new Dictionary<string, object?>
        {
            ["session_id"] = "sess-123",
            ["transcript_path"] = @"C:\Users\someone\.claude\projects\x\sess-123.jsonl",
            ["cwd"] = cwd ?? Environment.CurrentDirectory,
            ["hook_event_name"] = "whatever-fired",
            ["tool_name"] = "Bash",
            ["tool_input"] = new { command = "ls" },
        };
        if (prompt is not null)
        {
            fields["prompt"] = prompt;
        }

        return JsonSerializer.Serialize(fields);
    }

    /// <summary>A loopback port that was just proven closed: bind, note, release.</summary>
    private static Uri ClosedPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return new Uri($"http://127.0.0.1:{port}");
    }

    /// <summary>Captures the one request the relay makes and answers with canned JSON.</summary>
    private sealed class RecordingHandler(string responseJson) : HttpMessageHandler
    {
        public string? Path { get; private set; }

        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Path = request.RequestUri!.AbsolutePath;
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseJson, Encoding.UTF8, "application/json"),
            };
        }
    }
}
