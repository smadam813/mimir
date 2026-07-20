namespace Mimir.Server.Storage.Entities;

/// <summary>
/// Spec §3: the repository a memory belongs to. Identity follows the repository, not the directory
/// — two clones of one repository are one Project (§3.1). Full column set lands here; identity
/// upgrade and clone merge are a follow-up ticket.
/// </summary>
public sealed class Project
{
    /// <summary>
    /// The reserved Global pseudo-project (§3): the one representation of <c>scope=Global</c>. It
    /// holds Global Wisdom and no Episodes, and its row is seeded by migration so every later
    /// ticket can rely on it existing.
    /// </summary>
    public static readonly Guid GlobalId = new("00000000-0000-0000-0000-000000000001");

    /// <summary>
    /// The Global row's identity. Deliberately not a normalized remote (<c>host/owner/repo</c>)
    /// and not an absolute path, so no real repository can ever collide with it.
    /// </summary>
    public const string GlobalIdentity = "mimir:global";

    public Guid Id { get; set; }

    /// <summary>Normalized git remote URL, else the root path (spec §3.1). Unique.</summary>
    public required string Identity { get; set; }

    /// <summary>Every root this Project has been seen at; unseen roots are appended on match.</summary>
    public string[] RootPaths { get; set; } = [];

    public required string DisplayName { get; set; }
}
