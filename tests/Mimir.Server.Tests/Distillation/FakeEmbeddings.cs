using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.AI;

namespace Mimir.Server.Tests.Distillation;

/// <summary>
/// A deterministic stand-in for qwen3-embedding: mapped texts return their mapped vector; any
/// other text hashes to a pseudo-random unit vector. Identical text always embeds identically
/// (cosine 1), while unrelated texts land nearly orthogonal (|cos| ≲ 0.1 at 1024 dims) — far on
/// either side of the 0.80 gate, which is what makes gate tests deterministic.
/// </summary>
internal sealed class FakeEmbeddings : IEmbeddingGenerator<string, Embedding<float>>
{
    private readonly Dictionary<string, float[]> _mapped = new(StringComparer.Ordinal);

    public void Map(string text, float[] vector) => _mapped[text] = vector;

    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values,
        EmbeddingGenerationOptions? options = null,
        CancellationToken cancellationToken = default)
        => Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(
            values.Select(v => new Embedding<float>(VectorFor(v)))));

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }

    private float[] VectorFor(string text)
        => _mapped.TryGetValue(text, out var vector) ? vector : HashVector(text);

    private static float[] HashVector(string text)
    {
        var seed = BitConverter.ToInt32(SHA256.HashData(Encoding.UTF8.GetBytes(text)), 0);
        var random = new Random(seed);
        var vector = new float[TestVectors.Dimensions];
        for (var i = 0; i < vector.Length; i++)
        {
            vector[i] = (float)(random.NextDouble() * 2 - 1);
        }

        return TestVectors.Normalized(vector);
    }
}
