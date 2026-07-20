using Microsoft.EntityFrameworkCore;
using Mimir.Server.Storage.Entities;

namespace Mimir.Server.Storage;

/// <summary>
/// The single store (ADR-0005): vectors, full-text and relational metadata in one Postgres.
/// Tables and columns are snake_case, matching the spec §3 entity descriptions, because the
/// ranking queries this schema exists for are hand-written SQL. The ticket that creates an entity
/// builds its full §3 column set — consumers of the later columns arrive with later tickets.
/// </summary>
public sealed class MimirDbContext(DbContextOptions<MimirDbContext> options) : DbContext(options)
{
    public DbSet<Project> Projects => Set<Project>();

    public DbSet<Episode> Episodes => Set<Episode>();

    public DbSet<Event> Events => Set<Event>();

    public DbSet<HarvestedItem> HarvestedItems => Set<HarvestedItem>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasPostgresExtension("vector");

        modelBuilder.Entity<Project>(project =>
        {
            project.ToTable("projects");
            project.Property(p => p.Id).HasColumnName("id").ValueGeneratedNever();
            project.Property(p => p.Identity).HasColumnName("identity");
            project.Property(p => p.RootPaths).HasColumnName("root_paths");
            project.Property(p => p.DisplayName).HasColumnName("display_name");

            project.HasIndex(p => p.Identity).IsUnique();
            // GIN over text[] answers "which Project has been seen at this root" (§3.1, §5).
            project.HasIndex(p => p.RootPaths).HasMethod("gin");

            // The reserved Global pseudo-project (§3), fixed at migration time.
            project.HasData(new Project
            {
                Id = Project.GlobalId,
                Identity = Project.GlobalIdentity,
                RootPaths = [],
                DisplayName = "Global",
            });
        });

        modelBuilder.Entity<Episode>(episode =>
        {
            episode.ToTable("episodes");
            episode.Property(e => e.Id).HasColumnName("id").ValueGeneratedNever();
            episode.Property(e => e.SessionId).HasColumnName("session_id");
            episode.Property(e => e.ProjectId).HasColumnName("project_id");
            episode.Property(e => e.StartedAt).HasColumnName("started_at");
            episode.Property(e => e.SealedAt).HasColumnName("sealed_at");
            episode.Property(e => e.SealReason).HasColumnName("seal_reason");
            episode.Property(e => e.Cwd).HasColumnName("cwd");
            episode.Property(e => e.Distillation).HasColumnName("distillation").HasConversion<string>();
            episode.Property(e => e.DistilledAt).HasColumnName("distilled_at");

            episode.HasIndex(e => e.SessionId).IsUnique();
            episode.HasIndex(e => e.ProjectId);
            episode.HasOne<Project>().WithMany().HasForeignKey(e => e.ProjectId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<Event>(evt =>
        {
            evt.ToTable("events");
            evt.Property(e => e.Id).HasColumnName("id").ValueGeneratedNever();
            evt.Property(e => e.EpisodeId).HasColumnName("episode_id");
            evt.Property(e => e.Seq).HasColumnName("seq");
            evt.Property(e => e.Type).HasColumnName("type").HasConversion<string>();
            evt.Property(e => e.At).HasColumnName("at");
            evt.Property(e => e.Payload).HasColumnName("payload").HasColumnType("jsonb");
            evt.Property(e => e.PayloadFullSize).HasColumnName("payload_full_size");
            evt.Property(e => e.Salient).HasColumnName("salient");

            // Stored generated column over the payload's string values (§3): the Episode FTS leg.
            evt.Property(e => e.Tsv)
                .HasColumnName("tsv")
                .HasColumnType("tsvector")
                .HasComputedColumnSql("""jsonb_to_tsvector('english', payload, '["string"]')""", stored: true);

            evt.HasIndex(e => new { e.EpisodeId, e.Seq }).IsUnique();
            evt.HasIndex(e => e.Tsv).HasMethod("gin");
            // §8.2: hard-deleting an Episode removes its Events with it.
            evt.HasOne<Episode>().WithMany().HasForeignKey(e => e.EpisodeId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<HarvestedItem>(item =>
        {
            item.ToTable("harvested_items");
            item.Property(i => i.Id).HasColumnName("id").ValueGeneratedNever();
            item.Property(i => i.ProjectId).HasColumnName("project_id");
            item.Property(i => i.Path).HasColumnName("path");
            item.Property(i => i.ContentHash).HasColumnName("content_hash");
            item.Property(i => i.Content).HasColumnName("content");
            item.Property(i => i.FirstSeen).HasColumnName("first_seen");
            item.Property(i => i.LastChanged).HasColumnName("last_changed");
            item.Property(i => i.GoneAt).HasColumnName("gone_at");

            // The scanner's working set is "the latest row per path" (§5 item mechanics).
            item.HasIndex(i => i.Path);
            item.HasIndex(i => i.ProjectId);
            item.HasOne<Project>().WithMany().HasForeignKey(i => i.ProjectId).OnDelete(DeleteBehavior.Restrict);
        });

        base.OnModelCreating(modelBuilder);
    }
}
