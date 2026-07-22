using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using Mimir.Server.Configuration;
using Mimir.Server.Storage.Entities;

namespace Mimir.Server.Distillation;

/// <summary>
/// The §6 Distiller: one Sealed Episode's Event stream in, zero or more Wisdom candidates out,
/// on the distiller model (qwen3:8b, <c>/no_think</c>, <c>num_ctx</c> 16384) through the §2
/// model-client layer. Oversized Episodes are chunked by <see cref="EpisodeChunker"/> and
/// distilled per chunk — the Merge Gate downstream is the reduce. Events are labelled
/// <c>[eN]</c> by seq so the model's provenance references map back to Event ids.
/// </summary>
internal sealed class EpisodeDistiller(IChatClient chat, IOptions<DistillationOptions> options)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// Structured output, grammar-constrained by Ollama like the <see cref="MergeArbiter"/>'s:
    /// the kind and scope enums are enforced at generation time; the per-candidate semantic
    /// checks (usable text, real event refs) stay in <see cref="Parse"/>.
    /// </summary>
    private static readonly JsonElement Schema = JsonSerializer.Deserialize<JsonElement>("""
        {
          "type": "object",
          "properties": {
            "candidates": {
              "type": "array",
              "items": {
                "type": "object",
                "properties": {
                  "kind": { "type": "string", "enum": ["fact", "preference", "lesson", "procedure"] },
                  "scope": { "type": "string", "enum": ["global", "project"] },
                  "text": { "type": "string" },
                  "events": { "type": "array", "items": { "type": "integer" } }
                },
                "required": ["kind", "scope", "text", "events"]
              }
            }
          },
          "required": ["candidates"]
        }
        """);

    private static readonly ChatOptions Options = new()
    {
        Temperature = 0,
        ResponseFormat = ChatResponseFormat.ForJsonSchema(Schema, "wisdom_candidates"),
        AdditionalProperties = new AdditionalPropertiesDictionary { ["num_ctx"] = 16384 },
    };

    // The §6 prompting guidance verbatim: durable, reusable lessons only — not session
    // narration; prefer no candidates over weak ones. A Remember Event is the user deliberately
    // saving something (§4), so it always deserves a candidate — its explicit salience must not
    // be lost to the model's selectivity.
    private const string Instructions = """
        You distill one finished Claude Code session into durable memory notes for a memory
        system shared by every future session, in this project and others. The session is given
        as numbered EVENTS. Extract only durable, reusable lessons: how this environment works,
        how the user wants things done, procedures that will be repeated, facts learned the hard
        way. Never narrate the session; never store one-off trivia, file contents, or anything a
        future session could not act on. Prefer no candidates over weak ones — most sessions
        yield only a few, and an empty list is a fine answer.

        Answer with one JSON object, nothing else: {"candidates": [...]} where each candidate is
        {"kind": "...", "scope": "...", "text": "...", "events": [12, 17]}.
        - kind: "fact" (how the world is), "preference" (how the user wants things done),
          "lesson" (learned the hard way), or "procedure" (how to do something here).
        - scope: "project" when it holds only for this project, "global" when it holds
          everywhere.
        - text: one self-contained note, under 500 characters.
        - events: the [eN] numbers of the events the note derives from.
        An event marked "deliberate save" is the user explicitly asking to remember its content —
        always produce a candidate from it.
        """;

    public async Task<IReadOnlyList<WisdomCandidate>> DistillAsync(
        Episode episode,
        string projectIdentity,
        IReadOnlyList<Event> events,
        CancellationToken cancellationToken)
    {
        var candidates = new List<WisdomCandidate>();
        foreach (var chunk in EpisodeChunker.Chunk(events, options.Value.ChunkTokens))
        {
            candidates.AddRange(await DistillChunkAsync(episode, projectIdentity, chunk, cancellationToken));
        }

        return candidates;
    }

    private async Task<IReadOnlyList<WisdomCandidate>> DistillChunkAsync(
        Episode episode,
        string projectIdentity,
        IReadOnlyList<Event> chunk,
        CancellationToken cancellationToken)
    {
        ChatMessage[] messages =
        [
            new(ChatRole.System, Instructions),
            new(ChatRole.User, $"""
                PROJECT: {projectIdentity}

                EVENTS:
                {Render(chunk)}
                /no_think
                """),
        ];

        var response = await chat.GetResponseAsync(messages, Options, cancellationToken);
        return Parse(response.Text, episode, chunk);
    }

    private static string Render(IReadOnlyList<Event> chunk)
    {
        var rendered = new StringBuilder();
        foreach (var evt in chunk)
        {
            rendered.Append("[e").Append(evt.Seq).Append("] ").Append(evt.Type);
            if (evt.Type == EventType.Remember)
            {
                rendered.Append(" — deliberate save");
            }

            rendered.AppendLine().AppendLine(evt.Payload).AppendLine();
        }

        return rendered.ToString().TrimEnd();
    }

    private static IReadOnlyList<WisdomCandidate> Parse(string answer, Episode episode, IReadOnlyList<Event> chunk)
    {
        Answer? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<Answer>(ModelAnswer.Unfence(answer), JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new DistillerException($"the distiller's answer is not JSON: {ex.Message}");
        }

        if (parsed?.Candidates is null)
        {
            throw new DistillerException("the distiller's answer has no candidate list");
        }

        var eventsBySeq = chunk.ToDictionary(e => e.Seq, e => e.Id);
        var candidates = new List<WisdomCandidate>();
        foreach (var candidate in parsed.Candidates)
        {
            // A blank note is the model failing to decline; declining is the answer we keep.
            var text = candidate.Text?.Trim();
            if (string.IsNullOrEmpty(text))
            {
                continue;
            }

            // Provenance refs outside the chunk are hallucinated; the Episode-level Provenance
            // row the gate writes for a ref-less candidate is the honest fallback.
            var eventIds = candidate.Events?
                .Where(eventsBySeq.ContainsKey)
                .Select(seq => eventsBySeq[seq])
                .Distinct()
                .ToList();

            candidates.Add(new WisdomCandidate(
                ParseKind(candidate.Kind),
                ParseScope(candidate.Scope, episode),
                Cap(text),
                EpisodeId: episode.Id,
                EventIds: eventIds is { Count: > 0 } ? eventIds : null));
        }

        return candidates;
    }

    private static WisdomKind ParseKind(string? kind) => kind?.Trim().ToLowerInvariant() switch
    {
        "fact" => WisdomKind.Fact,
        "preference" => WisdomKind.Preference,
        "lesson" => WisdomKind.Lesson,
        "procedure" => WisdomKind.Procedure,
        var other => throw new DistillerException($"unknown candidate kind '{other}'"),
    };

    private static Guid ParseScope(string? scope, Episode episode) => scope?.Trim().ToLowerInvariant() switch
    {
        "global" => Project.GlobalId,
        "project" => episode.ProjectId,
        var other => throw new DistillerException($"unknown candidate scope '{other}'"),
    };

    /// <summary>The §6 candidate text budget — the same 500 the arbiter caps rewrites at.</summary>
    private static string Cap(string text)
        => text.Length <= MergeArbiter.MaxTextLength ? text : text[..MergeArbiter.MaxTextLength].TrimEnd();

    private sealed record Answer(List<AnswerCandidate>? Candidates);

    private sealed record AnswerCandidate(string? Kind, string? Scope, string? Text, List<int>? Events);
}

/// <summary>The distiller model's answer could not be turned into candidates.</summary>
internal sealed class DistillerException(string message) : Exception(message);
