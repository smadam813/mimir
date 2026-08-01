using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Mimir.Server.Distillation;

internal static class DistillerCall
{
    public const int ContextTokens = 16_384;

    /// <summary>The switch qwen3:8b reads as "answer without a reasoning block".</summary>
    public const string NoThink = "/no_think";

    /// <summary>OllamaSharp passes <paramref name="schema"/> through as the request's
    /// <c>format</c>, so Ollama constrains decoding to it rather than merely being asked for
    /// JSON.</summary>
    /// <param name="formatName">Names the schema in the request; the model never sees it.</param>
    public static ChatOptions ChatSettings(JsonElement schema, string formatName) => new()
    {
        Temperature = 0,
        ResponseFormat = ChatResponseFormat.ForJsonSchema(schema, formatName),
        AdditionalProperties = new AdditionalPropertiesDictionary { ["num_ctx"] = ContextTokens },
    };
}
