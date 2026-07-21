using Microsoft.EntityFrameworkCore;
using Mimir.Server.Storage;
using Mimir.Server.Storage.Entities;

namespace Mimir.Server.Tests;

/// <summary>Builders and cleanup shared by the Wisdom-tier test classes.</summary>
internal static class TestData
{
    /// <summary>A Project with a unique identity, so per-scope assertions see only one test.</summary>
    public static Project NewProject(string prefix)
    {
        var suffix = Guid.NewGuid().ToString("N");
        return new Project
        {
            Id = Guid.CreateVersion7(),
            Identity = $"github.com/test/{prefix}-{suffix}",
            DisplayName = $"{prefix}-{suffix}",
        };
    }

    /// <summary>
    /// Empties the Wisdom tables in FK-safe order — Provenance and WisdomVersions cascade from
    /// Wisdom, so they go first. Search leg membership and vector matching are global to the
    /// table, so classes whose tests map embeddings into the shared 0/1 plane start each test
    /// from clean Wisdom instead of meeting a prior test's rows.
    /// </summary>
    public static async Task ResetWisdomAsync(this MimirDbContext db, CancellationToken cancellationToken)
    {
        await db.Provenance.ExecuteDeleteAsync(cancellationToken);
        await db.WisdomVersions.ExecuteDeleteAsync(cancellationToken);
        await db.Wisdom.ExecuteDeleteAsync(cancellationToken);
    }
}
