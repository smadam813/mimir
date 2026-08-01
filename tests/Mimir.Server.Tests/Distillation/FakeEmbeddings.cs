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

    private readonly HashSet<string> _poisoned = new(StringComparer.Ordinal);

    public void Map(string text, float[] vector) => _mapped[text] = vector;

    /// <summary>Batches served — lets a test assert an embedding was reused, not regenerated.</summary>
    public int Batches { get; private set; }

    /// <summary>Any batch containing this text throws — a deterministically unembeddable item.</summary>
    public void Poison(string text) => _poisoned.Add(text);

    /// <summary>
    /// Runs as a batch is served, with the batch's texts — the gate's first step, so a test can
    /// make the world change exactly as an Admission begins (a caller giving up, say) or read what
    /// the gate is holding at the moment it embeds.
    /// </summary>
    public Action<IReadOnlyList<string>>? OnGenerate { get; set; }

    /// <summary>
    /// Serves every batch one vector short — a generator whose answer does not line up with its
    /// input, which <c>Zip</c> would otherwise truncate to silently.
    /// </summary>
    public bool ShortBatch { get; set; }

    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values,
        EmbeddingGenerationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        Batches++;
        var texts = values.ToList();
        OnGenerate?.Invoke(texts);
        if (texts.Any(_poisoned.Contains))
        {
            throw new InvalidOperationException("poisoned text cannot embed");
        }

        var served = ShortBatch ? texts.Take(texts.Count - 1) : texts;
        return Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(
            served.Select(v => new Embedding<float>(VectorFor(v)))));
    }

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
