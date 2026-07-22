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

    public DbSet<Wisdom> Wisdom => Set<Wisdom>();

    public DbSet<WisdomVersion> WisdomVersions => Set<WisdomVersion>();

    public DbSet<Provenance> Provenance => Set<Provenance>();

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
            episode.Property(e => e.DistillationStartedAt).HasColumnName("distillation_started_at");
            episode.Property(e => e.DistilledAt).HasColumnName("distilled_at");

            episode.HasIndex(e => e.SessionId).IsUnique();
            episode.HasIndex(e => e.ProjectId);
            // The §6 queue's working set: sealed Episodes still owed distillation. Done rows —
            // the table's eventual bulk — stay out of the index.
            episode.HasIndex(e => e.Distillation)
                .HasFilter("sealed_at IS NOT NULL AND distillation <> 'Done'");
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
            item.Property(i => i.ConvertedAt).HasColumnName("converted_at");

            // The scanner's working set is "the latest row per path" (§5 item mechanics).
            item.HasIndex(i => i.Path);
            item.HasIndex(i => i.ProjectId);
            // The converter's working set: versions the gate has not seen yet.
            item.HasIndex(i => i.ConvertedAt).HasFilter("converted_at IS NULL");
            item.HasOne<Project>().WithMany().HasForeignKey(i => i.ProjectId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<Wisdom>(wisdom =>
        {
            wisdom.ToTable("wisdom");
            wisdom.Property(w => w.Id).HasColumnName("id").ValueGeneratedNever();
            wisdom.Property(w => w.Kind).HasColumnName("kind").HasConversion<string>();
            wisdom.Property(w => w.ScopeProjectId).HasColumnName("scope_project_id");
            wisdom.Property(w => w.Text).HasColumnName("text");
            // The dimension count is schema, not config: qwen3-embedding:0.6b is 1024-d (§3, §6).
            wisdom.Property(w => w.Embedding).HasColumnName("embedding").HasColumnType("vector(1024)");
            wisdom.Property(w => w.Reinforcement).HasColumnName("reinforcement");
            wisdom.Property(w => w.LastConfirmedAt).HasColumnName("last_confirmed_at");
            wisdom.Property(w => w.ContestedAt).HasColumnName("contested_at");
            wisdom.Property(w => w.RetiredAt).HasColumnName("retired_at");
            wisdom.Property(w => w.SupersededBy).HasColumnName("superseded_by");

            // Stored generated column over the text (§3): the FTS leg of the hybrid search.
            wisdom.Property(w => w.Tsv)
                .HasColumnName("tsv")
                .HasColumnType("tsvector")
                .HasComputedColumnSql("to_tsvector('english', text)", stored: true);

            wisdom.HasIndex(w => w.ScopeProjectId);
            wisdom.HasIndex(w => w.Tsv).HasMethod("gin");
            // ANN for the cosine KNN leg. HNSW over IVFFlat: it needs no training rows, so it
            // works from the first Wisdom onward.
            wisdom.HasIndex(w => w.Embedding).HasMethod("hnsw").HasOperators("vector_cosine_ops");
            wisdom.HasOne<Project>().WithMany().HasForeignKey(w => w.ScopeProjectId)
                .OnDelete(DeleteBehavior.Restrict);
            // Deleting the superseder leaves the retired loser retired, just unlinked.
            wisdom.HasOne<Wisdom>().WithMany().HasForeignKey(w => w.SupersededBy)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<WisdomVersion>(version =>
        {
            version.ToTable("wisdom_versions");
            version.HasKey(v => new { v.WisdomId, v.Version });
            version.Property(v => v.WisdomId).HasColumnName("wisdom_id");
            version.Property(v => v.Version).HasColumnName("version");
            version.Property(v => v.Text).HasColumnName("text");
            version.Property(v => v.CreatedAt).HasColumnName("created_at");
            version.Property(v => v.Cause).HasColumnName("cause").HasConversion<string>();

            // §10: deleting a Wisdom cascades its version chain; nothing else touches it.
            version.HasOne<Wisdom>().WithMany().HasForeignKey(v => v.WisdomId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Provenance>(provenance =>
        {
            provenance.ToTable("provenance");
            provenance.Property(p => p.Id).HasColumnName("id").ValueGeneratedNever();
            provenance.Property(p => p.WisdomId).HasColumnName("wisdom_id");
            provenance.Property(p => p.EpisodeId).HasColumnName("episode_id");
            provenance.Property(p => p.EventId).HasColumnName("event_id");
            provenance.Property(p => p.HarvestedItemId).HasColumnName("harvested_item_id");

            provenance.HasIndex(p => p.WisdomId);
            provenance.HasIndex(p => p.EpisodeId);
            provenance.HasIndex(p => p.EventId);
            provenance.HasIndex(p => p.HarvestedItemId);
            provenance.HasOne<Wisdom>().WithMany().HasForeignKey(p => p.WisdomId)
                .OnDelete(DeleteBehavior.Cascade);
            // The §3 deletion contract: hard-deleting an Event or Episode (§8.2) removes the
            // Provenance rows referencing it — the sole operation that removes Provenance. The
            // Wisdom itself survives. HarvestedItems are never hard-deleted, so theirs restricts.
            provenance.HasOne<Episode>().WithMany().HasForeignKey(p => p.EpisodeId)
                .OnDelete(DeleteBehavior.Cascade);
            provenance.HasOne<Event>().WithMany().HasForeignKey(p => p.EventId)
                .OnDelete(DeleteBehavior.Cascade);
            provenance.HasOne<HarvestedItem>().WithMany().HasForeignKey(p => p.HarvestedItemId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        base.OnModelCreating(modelBuilder);
    }
}
