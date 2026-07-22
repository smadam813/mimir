namespace Mimir.Server.Distillation;

/// <summary>Shared cleanup of a distiller-model answer before JSON parsing.</summary>
internal static class ModelAnswer
{
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
