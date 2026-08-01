namespace Mimir.Server.Tests.Distillation;

internal static class TestVectors
{
    public const int Dimensions = 1024;

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
