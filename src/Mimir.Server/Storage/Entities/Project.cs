namespace Mimir.Server.Storage.Entities;

public sealed class Project
{
    public static readonly Guid GlobalId = new("00000000-0000-0000-0000-000000000001");

    /// <summary>Deliberately neither a normalized remote nor an absolute path, so no real
    /// repository can collide with it.</summary>
    public const string GlobalIdentity = "mimir:global";

    public Guid Id { get; set; }

    public required string Identity { get; set; }

    public string[] RootPaths { get; set; } = [];

    public required string DisplayName { get; set; }

    public bool IsPathBorn => Array.IndexOf(RootPaths, Identity) >= 0;
}
