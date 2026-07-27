namespace Mimir.Server.Ui;

/// <summary>
/// The contains-match pattern every browser's search box narrows by. Postgres reads <c>%</c> and
/// <c>_</c> in a LIKE pattern as syntax, so a curator searching for "100%" or "created_at" would
/// otherwise match half the table — each is escaped, and the query passes
/// <see cref="EscapeCharacter"/> as ILIKE's own ESCAPE so the escapes are read as escapes.
///
/// One copy on purpose. Two browsers each holding their own would let a later change — a new
/// metacharacter, a different escape — be fixed at one search box and missed at the other, and each
/// box's own tests would stay green while the two silently disagreed about what a "%" means.
/// </summary>
internal static class LikePattern
{
    /// <summary>
    /// What the pattern escapes with, and what the caller must hand ILIKE:
    /// <c>EF.Functions.ILike(column, LikePattern.Contains(term), LikePattern.EscapeCharacter)</c>.
    /// Passing a different one leaves the escapes in the pattern as literal backslashes.
    /// </summary>
    public const string EscapeCharacter = @"\";

    /// <summary>A pattern matching any text containing <paramref name="term"/> literally.</summary>
    public static string Contains(string term) => "%" + term
        .Replace(@"\", @"\\")
        .Replace("%", @"\%")
        .Replace("_", @"\_") + "%";
}
