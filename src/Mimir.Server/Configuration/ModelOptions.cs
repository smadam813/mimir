using System.ComponentModel.DataAnnotations;

namespace Mimir.Server.Configuration;

/// <summary>
/// Spec §11: models (distiller / embedding). All access goes through the Microsoft.Extensions.AI
/// abstractions backed by OllamaSharp (spec §2); these names are what gets provisioned on startup.
/// </summary>
public sealed class ModelOptions
{
    public const string SectionName = "Mimir:Models";

    /// <summary>Ollama's base address. Defaults to the Compose service name (spec §12).</summary>
    [Required]
    public Uri Endpoint { get; init; } = new("http://ollama:11434");

    /// <summary>Spec §6: distillation runs on qwen3:8b in non-reasoning mode.</summary>
    [Required(AllowEmptyStrings = false)]
    public string Distiller { get; init; } = "qwen3:8b";

    /// <summary>Spec §6: embeddings are qwen3-embedding:0.6b at 1024 dimensions.</summary>
    [Required(AllowEmptyStrings = false)]
    public string Embedding { get; init; } = "qwen3-embedding:0.6b";

    [Range(1, 4096)]
    public int EmbeddingDimensions { get; init; } = 1024;

    /// <summary>Every model Mimir provisions on startup (spec §12).</summary>
    public IReadOnlyList<string> Provisioned => [Distiller, Embedding];
}
