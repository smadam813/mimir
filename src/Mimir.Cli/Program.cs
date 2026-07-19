// Spec §13: the host companion. Both verbs are stubs — the capture ticket fills in `hook`, the
// recall ticket fills in `mcp`. What exists today is the shape and the publish configuration.
//
// `hook` deliberately exits 0 on every path, including this one: a hook that fails must never
// break or slow the session that invoked it (spec §1, §4).

const string Usage = """
    mimir — host companion for the Mimir memory service

    Usage:
      mimir hook <event>   Relay a Claude Code hook to Mimir. Not implemented yet.
      mimir mcp            Serve the Mimir MCP tools over stdio. Not implemented yet.

    Environment:
      MIMIR_URL            Mimir's base address (default http://127.0.0.1:6464).
    """;

switch (args)
{
    case ["hook", ..]:
        return 0;

    case ["mcp"]:
        await Console.Error.WriteLineAsync("mimir mcp is not implemented yet.");
        return 1;

    default:
        await Console.Error.WriteLineAsync(Usage);
        return 1;
}
