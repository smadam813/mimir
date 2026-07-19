using Mimir.Contracts.Health;

namespace Mimir.Server.Storage;

/// <summary>Measures whatever is currently in the database for the spec §8 Storage tile.</summary>
public interface IStorageProbe
{
    Task<StorageTile> ProbeAsync(CancellationToken cancellationToken);
}
