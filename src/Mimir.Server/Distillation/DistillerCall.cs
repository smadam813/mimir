using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Mimir.Server.Distillation;

/// <summary>
/// The request side of a distiller-model call. §6's two model-driven steps — the
/// <see cref="EpisodeDistiller"/> and the <see cref="MergeArbiter"/> — talk to the same model in
/// the same way: §11 runs it non-reasoning (<see cref="NoThink"/>) in a fixed context
/// (<see cref="ContextTokens"/>), and both steps want one JSON object back, so both constrain
/// generation to a schema at the most reproducible sampling the model offers. That is one model's
/// knowledge, not two steps' each, so it is stated here once; the schema and the prompt text stay
/// the caller's. <see cref="ModelAnswer"/> is the answer-side counterpart.
/// </summary>
internal static class DistillerCall
{
    /// <summary>
    /// §11's distiller context, mapped onto Ollama's <c>num_ctx</c> by OllamaSharp's option
    /// passthrough. A constant rather than a knob: chunking budgets its windows inside this number
    /// (<see cref="Mimir.Server.Configuration.DistillationOptions.ChunkTokens"/> takes it as the
    /// ceiling of its range), and the two only stay honest while one of them is fixed.
    /// </summary>
    public const int ContextTokens = 16_384;

    /// <summary>
    /// The switch qwen3:8b reads as "answer without a reasoning block". §11 runs the distiller
    /// model non-reasoning, so it ends the user turn of every call made here — both steps want
    /// JSON back, not thinking in front of JSON.
    /// </summary>
    public const string NoThink = "/no_think";

    /// <summary>
    /// The settings shared by every distiller-model call: temperature 0, for the most reproducible
    /// answer the model allows; the §11 context; and <paramref name="schema"/> as a grammar
    /// constraint — OllamaSharp passes it through as the request's <c>format</c>, so Ollama
    /// constrains decoding to it rather than merely being asked for JSON. What the schema can't
    /// express stays the caller's parser to enforce.
    /// </summary>
    /// <param name="formatName">Names the schema in the request; the model never sees it.</param>
    public static ChatOptions ChatSettings(JsonElement schema, string formatName) => new()
    {
        Temperature = 0,
        ResponseFormat = ChatResponseFormat.ForJsonSchema(schema, formatName),
        AdditionalProperties = new AdditionalPropertiesDictionary { ["num_ctx"] = ContextTokens },
    };
}
