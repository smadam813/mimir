namespace Mimir.Server.Components.FirstRun;

/// <summary>
/// The two registrations an install that has never been introduced to Claude Code still needs —
/// the §4 capture hooks and the §7 MCP server. One keeper for both: <c>FirstRunPanel</c>
/// renders these and <see cref="Both"/> is what the clipboard receives, so "Copy both commands"
/// can never hand over something other than what is on the screen. Pure by construction, so its
/// pins run with no Postgres — which is the machine a first run actually happens on (#90).
/// </summary>
/// <remarks>
/// README.md's "Registering the hooks" and "Searching your memory from a session" sections state
/// the same two registrations for a reader who never opens the app. Change one, change the other.
/// </remarks>
internal static class FirstRunCommands
{
    /// <summary>
    /// The §4 hooks, shaped for the user-level <c>~/.claude/settings.json</c>. All five, not the
    /// three the design abbreviated to: a block behind a Copy button has to work when pasted, and
    /// dropping Stop and SessionEnd would leave every Episode to be crash-swept instead of sealed
    /// with its own reason. Only the fire-and-forget three carry <c>async</c> — SessionStart and
    /// UserPromptSubmit print their reply (the Brief, the Prompt-lane injection) into the session.
    /// </summary>
    internal const string Hooks = """
        {
          "hooks": {
            "SessionStart": [
              { "hooks": [{ "type": "command", "command": "mimir hook SessionStart" }] }
            ],
            "UserPromptSubmit": [
              { "hooks": [{ "type": "command", "command": "mimir hook UserPromptSubmit" }] }
            ],
            "PostToolUse": [
              { "matcher": "*", "hooks": [{ "type": "command", "command": "mimir hook PostToolUse", "async": true }] }
            ],
            "Stop": [
              { "hooks": [{ "type": "command", "command": "mimir hook Stop", "async": true }] }
            ],
            "SessionEnd": [
              { "hooks": [{ "type": "command", "command": "mimir hook SessionEnd", "async": true }] }
            ]
          }
        }
        """;

    /// <summary>The §7 MCP server, registered once at user scope so every repository gets it.</summary>
    internal const string Mcp = "claude mcp add --scope user mimir -- mimir mcp";

    /// <summary>
    /// Both registrations as one clipboard payload. Prose between them rather than comment
    /// markers: the first half is JSON to merge into a file and the second is a shell command, so
    /// nothing here is a single thing to paste in one place and the payload says so plainly.
    /// </summary>
    internal static string Both { get; } =
        $"""
        Capture hooks — merge into your user-level ~/.claude/settings.json:

        {Hooks}

        Deliberate recall — run once, at user scope:

        {Mcp}
        """;
}
