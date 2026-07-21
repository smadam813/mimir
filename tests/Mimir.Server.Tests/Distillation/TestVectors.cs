namespace Mimir.Server.Tests.Distillation;

/// <summary>
/// Hand-built 1024-dim unit vectors with exact cosine geometry against <see cref="Basis"/>:
/// <c>WithCosine(c)</c> = [c, √(1−c²), 0, …], so its cosine similarity to the basis is c itself.
/// </summary>
internal static class TestVectors
{
    public const int Dimensions = 1024;

    /// <summary>[1, 0, 0, …] — the reference direction queries embed to.</summary>
    public static float[] Basis { get; } = WithCosine(1.0);

    public static float[] WithCosine(double cosine)
    {
        var vector = new float[Dimensions];
        vector[0] = (float)cosine;
        vector[1] = (float)Math.Sqrt(1 - (cosine * cosine));
        return vector;
    }

    public static float[] Normalized(float[] vector)
    {
        var norm = (float)Math.Sqrt(vector.Sum(v => (double)v * v));
        for (var i = 0; i < vector.Length; i++)
        {
            vector[i] /= norm;
        }

        return vector;
    }
}
