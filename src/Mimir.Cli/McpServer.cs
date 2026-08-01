using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using Mimir.Contracts.Mcp;

namespace Mimir.Cli;

internal sealed class McpServer(
    HttpClient http,
    TextReader input,
    TextWriter output,
    ProjectLocation location,
    string sessionId)
{
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    private const string ProtocolVersion = "2025-06-18";

    private static readonly string[] WisdomKinds = ["Fact", "Preference", "Lesson", "Procedure"];

    private static readonly JsonSerializerOptions Wire = new(JsonSerializerDefaults.Web);

    public async Task<int> RunAsync()
    {
        while (await input.ReadLineAsync() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var response = await HandleAsync(line);
            if (response is not null)
            {
                await output.WriteLineAsync(JsonSerializer.Serialize(response));
                await output.FlushAsync();
            }
        }

        return 0;
    }

    public static Task<ProjectLocation> ResolveProjectAsync(CancellationToken cancellationToken)
        => ResolveProjectAsync(
            Environment.CurrentDirectory,
            Environment.GetEnvironmentVariable("CLAUDE_PROJECT_DIR"),
            cancellationToken);

    internal static async Task<ProjectLocation> ResolveProjectAsync(
        string cwd, string? projectDir, CancellationToken cancellationToken)
    {
        var location = await ProjectLocator.LocateAsync(cwd, cancellationToken);
        if (location.Identity == cwd
            && projectDir is { Length: > 0 }
            && Directory.Exists(projectDir))
        {
            location = await ProjectLocator.LocateAsync(Path.GetFullPath(projectDir), cancellationToken);
        }

        return location;
    }

    private async Task<object?> HandleAsync(string line)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(line);
        }
        catch (JsonException)
        {
            return new { jsonrpc = "2.0", id = (object?)null, error = new { code = -32700, message = "Parse error" } };
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return new
                {
                    jsonrpc = "2.0",
                    id = (object?)null,
                    error = new
                    {
                        code = -32600,
                        message = "Invalid Request: expected a single JSON-RPC object (batching is not supported)",
                    },
                };
            }

            var method = root.TryGetProperty("method", out var m) ? m.GetString() : null;
            var hasId = root.TryGetProperty("id", out var idElement);
            object? id = hasId ? idElement.Clone() : null;

            if (!hasId)
            {
                return null;
            }

            return method switch
            {
                "initialize" => Result(id, Initialize()),
                "ping" => Result(id, new { }),
                "tools/list" => Result(id, ToolCatalog()),
                "tools/call" => await CallToolAsync(id, root),
                _ => Error(id, -32601, $"Method not found: {method}"),
            };
        }
    }

    private static object Initialize() => new
    {
        protocolVersion = ProtocolVersion,
        capabilities = new { tools = new { } },
        serverInfo = new { name = "mimir", version = "1.0" },
    };

    private async Task<object> CallToolAsync(object? id, JsonElement root)
    {
        if (!root.TryGetProperty("params", out var parameters)
            || parameters.ValueKind != JsonValueKind.Object
            || !parameters.TryGetProperty("name", out var name)
            || name.ValueKind != JsonValueKind.String
            || name.GetString() is not { Length: > 0 } tool)
        {
            return Error(id, -32602, "tools/call needs a tool name");
        }

        var args = parameters.TryGetProperty("arguments", out var a) ? a : default;
        try
        {
            return tool switch
            {
                "mimir_search" => await SearchAsync(id, args),
                "mimir_timeline" => await TimelineAsync(id, args),
                "mimir_remember" => await RememberAsync(id, args),
                _ => Error(id, -32602, $"Unknown tool: {tool}"),
            };
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return ToolError(id,
                $"Mimir is unreachable at {http.BaseAddress} — is the service running? ({ex.Message})");
        }
    }

    private async Task<object> SearchAsync(object? id, JsonElement args)
    {
        if (Text(args, "query") is not { Length: > 0 } query)
        {
            return ToolError(id, "mimir_search needs a query.");
        }

        if (!TryParseSince(args, out var since, out var sinceError))
        {
            return ToolError(id, sinceError);
        }

        return await PostAsync(id, "api/mcp/search", new McpSearchRequest
        {
            SessionId = sessionId,
            ProjectIdentity = location.Identity,
            ProjectRoot = location.Root,
            Query = query,
            Project = Text(args, "project"),
            Kind = Text(args, "kind"),
            Since = since,
            IncludeEpisodes = Flag(args, "include_episodes") ?? true,
            IncludeRetired = Flag(args, "include_retired") ?? false,
        });
    }

    private async Task<object> TimelineAsync(object? id, JsonElement args)
    {
        if (!TryParseSince(args, out var since, out var sinceError))
        {
            return ToolError(id, sinceError);
        }

        return await PostAsync(id, "api/mcp/timeline", new McpTimelineRequest
        {
            Project = Text(args, "project"),
            Since = since,
        });
    }

    private async Task<object> RememberAsync(object? id, JsonElement args)
    {
        if (Text(args, "content") is not { Length: > 0 } content)
        {
            return ToolError(id, "mimir_remember needs content.");
        }

        if (Text(args, "kind") is not { Length: > 0 } kind
            || !WisdomKinds.Contains(kind, StringComparer.OrdinalIgnoreCase))
        {
            return ToolError(id,
                $"mimir_remember needs a kind — one of: {string.Join(", ", WisdomKinds)}.");
        }

        return await PostAsync(id, "api/mcp/remember", new McpRememberRequest
        {
            ProjectIdentity = location.Identity,
            ProjectRoot = location.Root,
            Content = content,
            Kind = kind,
        });
    }

    private async Task<object> PostAsync<TRequest>(object? id, string route, TRequest request)
    {
        using var response = await http.PostAsJsonAsync(route, request, Wire);
        if (!response.IsSuccessStatusCode)
        {
            return ToolError(id, $"Mimir answered {(int)response.StatusCode} for {route}.");
        }

        McpToolReply? reply;
        try
        {
            reply = await response.Content.ReadFromJsonAsync<McpToolReply>(Wire);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            return ToolError(id,
                $"Mimir's reply for {route} was not readable — is something else answering at"
                + $" {http.BaseAddress}? ({ex.Message})");
        }

        return Result(id, new
        {
            content = new object[] { new { type = "text", text = reply?.Text ?? "" } },
        });
    }

    private static bool TryParseSince(JsonElement args, out DateTimeOffset? since, out string error)
    {
        since = null;
        error = "";
        if (Text(args, "since") is not { Length: > 0 } text)
        {
            return true;
        }

        if (!DateTimeOffset.TryParse(
                text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
        {
            error = $"since must be an ISO 8601 instant, not '{text}'.";
            return false;
        }

        // Npgsql refuses a non-UTC DateTimeOffset against timestamptz.
        since = parsed.ToUniversalTime();
        return true;
    }

    private static string? Text(JsonElement args, string name)
        => args.ValueKind == JsonValueKind.Object
            && args.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool? Flag(JsonElement args, string name)
        => args.ValueKind == JsonValueKind.Object
            && args.TryGetProperty(name, out var value)
            && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : null;

    private static object Result(object? id, object result)
        => new { jsonrpc = "2.0", id, result };

    private static object Error(object? id, int code, string message)
        => new { jsonrpc = "2.0", id, error = new { code, message } };

    private static object ToolError(object? id, string message)
        => Result(id, new
        {
            content = new object[] { new { type = "text", text = message } },
            isError = true,
        });

    private static object ToolCatalog() => new
    {
        tools = new object[]
        {
            new
            {
                name = "mimir_search",
                description =
                    "Search Mimir's memory: distilled Wisdom (hybrid semantic + full-text, every "
                    + "project) and raw Episode events (full-text). Retired Wisdom surfaces only "
                    + "with include_retired.",
                inputSchema = new
                {
                    type = "object",
                    properties = new Dictionary<string, object>
                    {
                        ["query"] = new { type = "string", description = "What to search for." },
                        ["project"] = new
                        {
                            type = "string",
                            description = "Narrow to one project, by display name or identity.",
                        },
                        ["kind"] = new Dictionary<string, object>
                        {
                            ["type"] = "string",
                            ["enum"] = WisdomKinds,
                            ["description"] = "Narrow Wisdom to one kind.",
                        },
                        ["since"] = new
                        {
                            type = "string",
                            description = "ISO 8601 instant; keep only results confirmed/captured after it.",
                        },
                        ["include_episodes"] = new
                        {
                            type = "boolean",
                            description = "Also search raw Episode events (default true).",
                        },
                        ["include_retired"] = new
                        {
                            type = "boolean",
                            description = "Reach Retired Wisdom too (default false).",
                        },
                    },
                    required = new[] { "query" },
                },
            },
            new
            {
                name = "mimir_timeline",
                description = "List recent Claude Code sessions (Episodes) newest first, with "
                    + "their seal state.",
                inputSchema = new
                {
                    type = "object",
                    properties = new Dictionary<string, object>
                    {
                        ["project"] = new
                        {
                            type = "string",
                            description = "Narrow to one project, by display name or identity.",
                        },
                        ["since"] = new
                        {
                            type = "string",
                            description = "ISO 8601 instant; keep only Episodes started after it.",
                        },
                    },
                },
            },
            new
            {
                name = "mimir_remember",
                description = "Deliberately save something to Mimir's memory. Attached to the "
                    + "live session's Episode it is salient — it outranks inferred memories; "
                    + "with no live Episode it goes straight to the Wisdom tier as an ordinary "
                    + "candidate.",
                inputSchema = new
                {
                    type = "object",
                    properties = new Dictionary<string, object>
                    {
                        ["content"] = new { type = "string", description = "What to remember." },
                        ["kind"] = new Dictionary<string, object>
                        {
                            ["type"] = "string",
                            ["enum"] = WisdomKinds,
                            ["description"] = "What kind of memory this is.",
                        },
                    },
                    required = new[] { "content", "kind" },
                },
            },
        },
    };
}
