using System.Text;
using Mimir.Cli;
using Mimir.Contracts.Hooks;

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
                // Backstop only: RunAsync's cap always fires first.
                Timeout = HookLimits.RoundTripCap,
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
            // Console.In/Out inherit the Windows console code page, and StreamWriter defaults to CRLF.
            using var stdin = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
            using var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false))
            {
                NewLine = "\n",
            };
            var location = await McpServer.ResolveProjectAsync(CancellationToken.None);
            var sessionId = $"mcp-{Guid.NewGuid():N}";
            return await new McpServer(http, stdin, stdout, location, sessionId).RunAsync();
        }

    default:
        await Console.Error.WriteLineAsync(Usage);
        return 1;
}

static Uri ServiceAddress()
    => new(Environment.GetEnvironmentVariable("MIMIR_URL") is { Length: > 0 } url
        ? url
        : "http://127.0.0.1:6464");
