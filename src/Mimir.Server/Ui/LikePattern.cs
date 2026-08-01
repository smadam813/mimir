namespace Mimir.Server.Ui;

internal static class LikePattern
{
    public const string EscapeCharacter = @"\";

    public static string Contains(string term) => "%" + term
        .Replace(@"\", @"\\")
        .Replace("%", @"\%")
        .Replace("_", @"\_") + "%";
}
