// Spec §13: the host companion. `hook` relays Claude Code hooks to Mimir; `mcp` arrives with the
// recall ticket. Registration of both is documented in the README.
//
// `hook` deliberately exits 0 on every path, including argument mistakes and a malformed
// MIMIR_URL: a hook that fails must never break or slow the session that invoked it (spec §1, §4).

using Mimir.Cli;

const string Usage = """
    mimir — host companion for the Mimir memory service

    Usage:
      mimir hook <event>   Relay a Claude Code hook to Mimir (see the README for registration).
      mimir mcp            Serve the Mimir MCP tools over stdio. Not implemented yet.

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
                BaseAddress = new(Environment.GetEnvironmentVariable("MIMIR_URL") is { Length: > 0 } url
                    ? url
                    : "http://127.0.0.1:6464"),
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
        await Console.Error.WriteLineAsync("mimir mcp is not implemented yet.");
        return 1;

    default:
        await Console.Error.WriteLineAsync(Usage);
        return 1;
}
