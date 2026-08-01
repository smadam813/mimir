using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.AI;

namespace Mimir.Server.Tests.Distillation;

internal sealed class FakeEmbeddings : IEmbeddingGenerator<string, Embedding<float>>
{
    private readonly Dictionary<string, float[]> _mapped = new(StringComparer.Ordinal);

    private readonly HashSet<string> _poisoned = new(StringComparer.Ordinal);

    public void Map(string text, float[] vector) => _mapped[text] = vector;

    public int Batches { get; private set; }

    public void Poison(string text) => _poisoned.Add(text);

    public Action? OnGenerate { get; set; }

    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values,
        EmbeddingGenerationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        Batches++;
        OnGenerate?.Invoke();
        var texts = values.ToList();
        if (texts.Any(_poisoned.Contains))
        {
            throw new InvalidOperationException("poisoned text cannot embed");
        }

        return Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(
            texts.Select(v => new Embedding<float>(VectorFor(v)))));
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
