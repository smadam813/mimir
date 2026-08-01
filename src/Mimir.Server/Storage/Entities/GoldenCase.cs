namespace Mimir.Server.Storage.Entities;

public sealed class GoldenCase
{
    public Guid Id { get; set; }

    public required string QueryContext { get; set; }

    public Guid ProjectId { get; set; }

    public Guid ExpectedWisdomId { get; set; }

    public Guid? CreatedFromInjectionId { get; set; }

    public required string Note { get; set; }
}
