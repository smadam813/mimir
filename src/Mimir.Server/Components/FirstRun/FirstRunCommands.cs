namespace Mimir.Server.Components.FirstRun;

internal static class FirstRunCommands
{
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

    internal const string Mcp = "claude mcp add --scope user mimir -- mimir mcp";

    internal static string Both { get; } =
        $"""
        Capture hooks — merge into your user-level ~/.claude/settings.json:

        {Hooks}

        Deliberate recall — run once, at user scope:

        {Mcp}
        """;
}
