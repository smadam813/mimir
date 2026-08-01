namespace Mimir.Server.Distillation;

internal static class ModelAnswer
{
    public const int MaxTextLength = 500;

    public static string Cap(string text)
        => text.Length <= MaxTextLength ? text : text[..MaxTextLength].TrimEnd();

    /// <summary>JSON mode should preclude fences; a stray ```json wrapper is cheap to shed anyway.</summary>
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
