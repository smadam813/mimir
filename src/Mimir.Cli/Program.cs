// Spec §13: the host companion. `hook` relays Claude Code hooks to Mimir; `mcp` serves the §7
// MCP tools over stdio. Registration of both is documented in the README.
//
// `hook` deliberately exits 0 on every path, including argument mistakes and a malformed
// MIMIR_URL: a hook that fails must never break or slow the session that invoked it (spec §1, §4).
// `mcp` is the deliberate lane: it may fail loudly — Claude Code surfaces a dead MCP server.

using Mimir.Cli;

const string Usage = """
    mimir — host companion for the Mimir memory service

    Usage:
      mimir hook <event>   Relay a Claude Code hook to Mimir (see the README for registration).
      mimir mcp            Serve the Mimir MCP tools over stdio (see the README for registration).

    Environment:
      MIMIR_URL            Mimir's base address (default http://127.0.0.1:6464).
    """;

switch (args)
{
    case ["hook", var hookEvent, ..]:
        try
        {
            using var http = new HttpClient
            {
                BaseAddress = ServiceAddress(),
                // Redundant with RunAsync's cap, which always fires first — kept as a backstop
                // so a future request that forgets to thread the cap token still cannot hang
                // the session for HttpClient's default 100 s.
                Timeout = HookCommand.Cap,
            };
            return await new HookCommand(http, Console.In, Console.Out).RunAsync(hookEvent);
        }
        catch (Exception)
        {
            return 0;
        }

    case ["hook"]:
        return 0;

    case ["mcp"]:
        using (var http = new HttpClient { BaseAddress = ServiceAddress(), Timeout = McpServer.RequestTimeout })
        {
            var location = await McpServer.ResolveProjectAsync(CancellationToken.None);
            var sessionId = $"mcp-{Guid.NewGuid():N}";
            return await new McpServer(http, Console.In, Console.Out, location, sessionId).RunAsync();
        }

    default:
        await Console.Error.WriteLineAsync(Usage);
        return 1;
}

static Uri ServiceAddress()
    => new(Environment.GetEnvironmentVariable("MIMIR_URL") is { Length: > 0 } url
        ? url
        : "http://127.0.0.1:6464");
