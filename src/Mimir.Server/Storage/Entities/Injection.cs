namespace Mimir.Server.Storage.Entities;

public sealed class Injection
{
    public Guid Id { get; set; }

    public required string SessionId { get; set; }

    /// <summary>Means something different per lane; <see cref="Recall.InjectionContext"/> is the
    /// one statement of which each passes.</summary>
    public Guid ProjectId { get; set; }

    public DateTimeOffset At { get; set; }

    public InjectionLane Lane { get; set; }

    public string? QueryContext { get; set; }

    public int Chars { get; set; }

    public List<InjectionItem> Items { get; set; } = [];

    public InjectionVerdict? Verdict { get; set; }

    public DateTimeOffset? VerdictAt { get; set; }
}

public sealed class InjectionItem
{
    public Guid WisdomId { get; set; }

    public double Score { get; set; }
}

public enum InjectionLane
{
    Brief,
    Prompt,
    Mcp,
}

public enum InjectionVerdict
{
    Useful,
    Noise,
}
