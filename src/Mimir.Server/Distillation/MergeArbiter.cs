using System.Text.Json;
using Microsoft.Extensions.AI;
using Mimir.Server.Storage.Entities;

namespace Mimir.Server.Distillation;

/// <summary>
/// <see cref="IMergeArbiter"/> on the distiller model through the §2 model-client layer: one
/// JSON-mode call (qwen3:8b, <c>/no_think</c>, temperature 0) classifying the matched pair and
/// producing the §6 rewrite or adjudication. Rewrites are capped at <see cref="MaxTextLength"/>;
/// anything else unusable in the answer throws <see cref="MergeArbiterException"/>.
/// </summary>
internal sealed class MergeArbiter(IChatClient chat) : IMergeArbiter
{
    /// <summary>The §6 candidate text budget, applied to every rewrite the model returns.</summary>
    public const int MaxTextLength = 500;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

    // Temperature 0 keeps the verdict as reproducible as the model allows; num_ctx is the §11
    // distiller context, mapped to Ollama by OllamaSharp's option passthrough.
    private static readonly ChatOptions Options = new()
    {
        Temperature = 0,
        ResponseFormat = ChatResponseFormat.Json,
        AdditionalProperties = new AdditionalPropertiesDictionary { ["num_ctx"] = 16384 },
    };

    private const string Instructions = """
        You are the merge arbiter of a memory system. You are given two memory notes on the same
        topic: an EXISTING note already stored, and a CANDIDATE note newly extracted. Decide how
        they relate and answer with one JSON object, nothing else.

        - If they agree (restate, refine, or complement the same lesson):
          {"verdict":"agreement","merged_text":"..."} — one self-contained note under 500
          characters, rewritten from both, keeping every concrete detail worth keeping.
        - If they contradict and the CANDIDATE makes the EXISTING note obsolete (the world
          changed, or the existing note is wrong): {"verdict":"supersede"}
        - If they contradict but both hold in different scopes — one holds everywhere, the other
          only in one project: {"verdict":"scope_split","global_text":"...","project_text":"..."}
          — each under 500 characters. Only choose this when at least one of the scope notes
          below mentions a project.

        When unsure whether they contradict, prefer "agreement".
        """;

    public async Task<MergeRuling> RuleAsync(
        Wisdom existing, WisdomCandidate candidate, CancellationToken cancellationToken)
    {
        ChatMessage[] messages =
        [
            new(ChatRole.System, Instructions),
            new(ChatRole.User, $"""
                EXISTING ({existing.Kind}, {ScopeOf(existing.ScopeProjectId)}):
                {existing.Text}

                CANDIDATE ({candidate.Kind}, {OriginOf(candidate, existing)}):
                {candidate.Text}
                /no_think
                """),
        ];

        var response = await chat.GetResponseAsync(messages, Options, cancellationToken);
        return Parse(response.Text);
    }

    private static string ScopeOf(Guid scopeProjectId)
        => scopeProjectId == Project.GlobalId ? "scoped Global" : "scoped to one project";

    private static string OriginOf(WisdomCandidate candidate, Wisdom existing)
        => candidate.ScopeProjectId == Project.GlobalId ? "proposed as Global"
            : candidate.ScopeProjectId == existing.ScopeProjectId ? "from the same project"
            : "from a different project";

    private static MergeRuling Parse(string answer)
    {
        Verdict? verdict;
        try
        {
            verdict = JsonSerializer.Deserialize<Verdict>(Unfence(answer), JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new MergeArbiterException($"the arbiter's answer is not JSON: {ex.Message}");
        }

        return verdict?.Kind?.Trim().ToLowerInvariant() switch
        {
            "agreement" => new MergeRuling.Agreement(RequireText(verdict.MergedText, "merged_text")),
            "supersede" => new MergeRuling.Supersede(),
            "scope_split" => new MergeRuling.ScopeSplit(
                RequireText(verdict.GlobalText, "global_text"),
                RequireText(verdict.ProjectText, "project_text")),
            var kind => throw new MergeArbiterException($"unknown verdict '{kind}'"),
        };
    }

    private static string RequireText(string? text, string field)
        => string.IsNullOrWhiteSpace(text)
            ? throw new MergeArbiterException($"the verdict needs a non-empty '{field}'")
            : Cap(text.Trim());

    private static string Cap(string text)
        => text.Length <= MaxTextLength ? text : text[..MaxTextLength].TrimEnd();

    /// <summary>JSON mode should preclude fences, but a stray ```json wrapper is cheap to shed.</summary>
    private static string Unfence(string answer)
    {
        var text = answer.Trim();
        if (text.StartsWith("```", StringComparison.Ordinal))
        {
            var open = text.IndexOf('\n');
            var close = text.LastIndexOf("```", StringComparison.Ordinal);
            if (open >= 0 && close > open)
            {
                text = text[open..close].Trim();
            }
        }

        return text;
    }

    private sealed record Verdict(string? MergedText, string? GlobalText, string? ProjectText)
    {
        [System.Text.Json.Serialization.JsonPropertyName("verdict")]
        public string? Kind { get; init; }
    }
}
