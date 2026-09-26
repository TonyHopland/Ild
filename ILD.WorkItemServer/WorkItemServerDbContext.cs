using ILD.WorkItemServer.Domain;
using Microsoft.EntityFrameworkCore;

namespace ILD.WorkItemServer;

public sealed class WorkItemServerDbContext : DbContext
{
    public WorkItemServerDbContext(DbContextOptions<WorkItemServerDbContext> options)
        : base(options) { }

    public DbSet<WorkItem> WorkItems => Set<WorkItem>();

    public DbSet<WorkItemAttachment> WorkItemAttachments => Set<WorkItemAttachment>();

    public DbSet<WorkItemEditProposal> WorkItemEditProposals => Set<WorkItemEditProposal>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<WorkItem>(b =>
        {
            b.HasKey(w => w.InternalId);
            b.Property(w => w.InternalId).ValueGeneratedOnAdd();
            b.Property(w => w.Id).IsRequired();
            b.HasIndex(w => w.Id).IsUnique();
            b.HasIndex(w => w.Status);
            b.Property(w => w.Status).HasConversion<int>();
            b.Property(w => w.Priority).HasConversion<int>();
            b.Property(w => w.AiProviderOverride).HasConversion<int>();
            // Description is intentionally unbounded — map to PostgreSQL text.
            b.Property(w => w.Description).HasColumnType("text");
            // Items that predate the column read as "no PRs recorded yet"
            // rather than as an empty string the JSON reader has to special-case.
            b.Property(w => w.PullRequestsJson).HasDefaultValue("[]");
        });

        modelBuilder.Entity<WorkItemAttachment>(b =>
        {
            b.HasKey(a => a.Id);
            b.HasIndex(a => a.WorkItemId);
            // Deleting a work item never loads its attachments, so the database's
            // own cascade is the only thing that collects them.
            b.HasOne<WorkItem>()
                .WithMany()
                .HasForeignKey(a => a.WorkItemId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<WorkItemEditProposal>(b =>
        {
            b.HasKey(p => p.Id);
            b.HasIndex(p => new { p.WorkItemId, p.Status });
            b.HasIndex(p => p.CreatedByChatSessionId);
            b.Property(p => p.Status).HasConversion<int>();
            b.Property(p => p.ProposedDescription).HasColumnType("text");
            b.Property(p => p.SnapshotDescription).HasColumnType("text");
            // Deleting a work item never loads its proposals either.
            b.HasOne<WorkItem>()
                .WithMany()
                .HasForeignKey(p => p.WorkItemId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
