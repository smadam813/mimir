using System.ComponentModel.DataAnnotations;

namespace Mimir.Server.Configuration;

public sealed class ModelOptions
{
    public const string SectionName = "Mimir:Models";

    [Required]
    public Uri Endpoint { get; init; } = new("http://ollama:11434");

    [Required(AllowEmptyStrings = false)]
    public string Distiller { get; init; } = "qwen3:8b";

    [Required(AllowEmptyStrings = false)]
    public string Embedding { get; init; } = "qwen3-embedding:0.6b";

    [Range(1, 4096)]
    public int EmbeddingDimensions { get; init; } = 1024;

    public IReadOnlyList<string> Provisioned => [Distiller, Embedding];
}
