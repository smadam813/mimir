using Mimir.Contracts.Health;

namespace Mimir.Server.Storage;

public interface IStorageProbe
{
    Task<StorageTile> ProbeAsync(CancellationToken cancellationToken);
}
