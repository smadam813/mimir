namespace Mimir.Server.Distillation;

/// <summary>Shared cleanup of a distiller-model answer before JSON parsing.</summary>
internal static class ModelAnswer
{
    /// <summary>
    /// The §6 candidate text budget, applied to every text the model hands back — the Distiller's
    /// candidates and the arbiter's rewrites alike.
    /// </summary>
    public const int MaxTextLength = 500;

    /// <summary>Enforces <see cref="MaxTextLength"/> on an answer's text.</summary>
    public static string Cap(string text)
        => text.Length <= MaxTextLength ? text : text[..MaxTextLength].TrimEnd();

    /// <summary>JSON mode should preclude fences, but a stray ```json wrapper is cheap to shed.</summary>
    public static string Unfence(string answer)
    {
        var text = answer.Trim();
        if (text.StartsWith("```", StringComparison.Ordinal))
        {
            var open = text.IndexOf('\n');
            var close = text.LastIndexOf("```", StringComparison.Ordinal);
            if (open >= 0 && close > open)
            {
                text = text[open..close].Trim();
            }
        }

        return text;
    }
}
