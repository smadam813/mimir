using Microsoft.EntityFrameworkCore;

namespace Mimir.Server.Storage;

/// <summary>
/// The single store (ADR-0005): vectors, full-text and relational metadata in one Postgres.
/// No entities yet — the domain tables arrive with the tickets that own them. What exists today
/// is the migration pipeline and the pgvector extension they will all depend on.
/// </summary>
public sealed class MimirDbContext(DbContextOptions<MimirDbContext> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasPostgresExtension("vector");
        base.OnModelCreating(modelBuilder);
    }
}
