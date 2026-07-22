using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Mimir.Cli.Tests;

/// <summary>
/// <c>mimir mcp</c> at its stdio boundary: newline-delimited JSON-RPC in, one-line responses out,
/// each tool call one HTTP POST carrying the §7.1-resolved Project. The lane is deliberate, so a
/// dead Mimir answers an honest MCP tool error (<c>isError</c>) — never hook-style silence.
/// </summary>
public class McpServerTests
{
    private static readonly ProjectLocation Location = new("github.com/test/repo", @"C:\repo");

    [Fact]
    public async Task Initialize_EchoesTheClientsProtocolVersion_AndNamesTheServer()
    {
        var responses = await RunAsync(
            ReplyingHandler(),
            """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-03-26","capabilities":{}}}""",
            """{"jsonrpc":"2.0","method":"notifications/initialized"}""");

        var result = responses.ShouldHaveSingleItem().RootElement.GetProperty("result");
        result.GetProperty("protocolVersion").GetString().ShouldBe("2025-03-26");
        result.GetProperty("serverInfo").GetProperty("name").GetString().ShouldBe("mimir");
        result.GetProperty("capabilities").TryGetProperty("tools", out _).ShouldBeTrue();
    }

    [Fact]
    public async Task ToolsList_OffersTheThreeMimirTools()
    {
        var responses = await RunAsync(
            ReplyingHandler(),
            """{"jsonrpc":"2.0","id":1,"method":"tools/list"}""");

        var tools = responses.ShouldHaveSingleItem().RootElement
            .GetProperty("result").GetProperty("tools").EnumerateArray()
            .Select(t => t.GetProperty("name").GetString())
            .ToList();
        tools.ShouldBe(["mimir_search", "mimir_timeline", "mimir_remember"]);
    }

    [Fact]
    public async Task ASearchCall_PostsTheProjectAndPseudoSession_AndRelaysTheReplyText()
    {
        var handler = ReplyingHandler("""{"text":"the remembered answer"}""");

        var responses = await RunAsync(
            handler,
            """{"jsonrpc":"2.0","id":7,"method":"tools/call","params":{"name":"mimir_search","arguments":{"query":"deploy steps","include_retired":true}}}""");

        var (uri, body) = handler.Requests.ShouldHaveSingleItem();
        uri!.AbsolutePath.ShouldBe("/api/mcp/search");
        using var posted = JsonDocument.Parse(body);
        posted.RootElement.GetProperty("query").GetString().ShouldBe("deploy steps");
        posted.RootElement.GetProperty("projectIdentity").GetString().ShouldBe(Location.Identity);
        posted.RootElement.GetProperty("sessionId").GetString().ShouldStartWith("mcp-");
        posted.RootElement.GetProperty("includeRetired").GetBoolean().ShouldBeTrue();
        posted.RootElement.GetProperty("includeEpisodes").GetBoolean()
            .ShouldBeTrue("include_episodes defaults to true (§7)");

        var result = responses.ShouldHaveSingleItem().RootElement.GetProperty("result");
        result.GetProperty("content")[0].GetProperty("text").GetString()!
            .ShouldBe("the remembered answer");
        result.TryGetProperty("isError", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task ARememberCall_WithAnUnknownKind_FailsClientSide_WithoutAnyPost()
    {
        var handler = ReplyingHandler();

        var responses = await RunAsync(
            handler,
            """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"mimir_remember","arguments":{"content":"x","kind":"hunch"}}}""");

        handler.Requests.ShouldBeEmpty();
        var result = responses.ShouldHaveSingleItem().RootElement.GetProperty("result");
        result.GetProperty("isError").GetBoolean().ShouldBeTrue();
        result.GetProperty("content")[0].GetProperty("text").GetString()!
            .ShouldContain("Fact, Preference, Lesson, Procedure");
    }

    [Fact]
    public async Task AnUnparsableSince_FailsClientSide_WithoutAnyPost()
    {
        var handler = ReplyingHandler();

        var responses = await RunAsync(
            handler,
            """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"mimir_timeline","arguments":{"since":"yesterdayish"}}}""");

        handler.Requests.ShouldBeEmpty();
        var result = responses.ShouldHaveSingleItem().RootElement.GetProperty("result");
        result.GetProperty("isError").GetBoolean().ShouldBeTrue();
        result.GetProperty("content")[0].GetProperty("text").GetString()!
            .ShouldContain("ISO 8601");
    }

    [Fact]
    public async Task ADeadServer_AnswersAnHonestToolError_NotSilence()
    {
        using var http = new HttpClient { BaseAddress = ClosedPort() };
        var output = new StringWriter();
        var server = new McpServer(
            http,
            new StringReader(
                """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"mimir_search","arguments":{"query":"anything"}}}"""
                + "\n"),
            output,
            Location,
            "mcp-test");

        (await server.RunAsync()).ShouldBe(0);

        using var response = JsonDocument.Parse(output.ToString());
        var result = response.RootElement.GetProperty("result");
        result.GetProperty("isError").GetBoolean().ShouldBeTrue();
        result.GetProperty("content")[0].GetProperty("text").GetString()!
            .ShouldContain("unreachable");
    }

    [Fact]
    public async Task UnknownMethodsAndGarbage_AnswerJsonRpcErrors_ButUnknownNotificationsStaySilent()
    {
        var responses = await RunAsync(
            ReplyingHandler(),
            """{"jsonrpc":"2.0","id":1,"method":"resources/list"}""",
            """{"jsonrpc":"2.0","method":"notifications/cancelled"}""",
            "this is not json");

        responses.Count.ShouldBe(2, "a notification never gets a response");
        responses[0].RootElement.GetProperty("error").GetProperty("code").GetInt32().ShouldBe(-32601);
        responses[1].RootElement.GetProperty("error").GetProperty("code").GetInt32().ShouldBe(-32700);
    }

    /// <summary>Feeds the lines to a server backed by <paramref name="handler"/>, returning one
    /// parsed document per response line.</summary>
    private static async Task<List<JsonDocument>> RunAsync(RecordingHandler handler, params string[] lines)
    {
        using var http = new HttpClient(handler) { BaseAddress = new("http://127.0.0.1:6464") };
        var output = new StringWriter();
        var server = new McpServer(
            http, new StringReader(string.Join('\n', lines) + "\n"), output, Location, "mcp-test");

        (await server.RunAsync()).ShouldBe(0);

        return output.ToString()
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => JsonDocument.Parse(line))
            .ToList();
    }

    private static RecordingHandler ReplyingHandler(string replyJson = """{"text":"ok"}""")
        => new(replyJson);

    /// <summary>A port with nothing listening, for the dead-server path.</summary>
    private static Uri ClosedPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return new Uri($"http://127.0.0.1:{port}");
    }

    private sealed class RecordingHandler(string replyJson) : HttpMessageHandler
    {
        public List<(Uri? Uri, string Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.RequestUri, await request.Content!.ReadAsStringAsync(cancellationToken)));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(replyJson, Encoding.UTF8, "application/json"),
            };
        }
    }
}
