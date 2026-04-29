using Microsoft.EntityFrameworkCore;
using ILD.Data.Enums;

namespace ILD.Data.Entities;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
    {
    }

    public DbSet<RemoteProvider> RemoteProviders => Set<RemoteProvider>();
    public DbSet<Repository> Repositories => Set<Repository>();
    public DbSet<LoopTemplate> LoopTemplates => Set<LoopTemplate>();
    public DbSet<LoopTemplateVersion> LoopTemplateVersions => Set<LoopTemplateVersion>();
    public DbSet<LoopNode> LoopNodes => Set<LoopNode>();
    public DbSet<LoopNodeEdge> LoopNodeEdges => Set<LoopNodeEdge>();
    public DbSet<WorkItem> WorkItems => Set<WorkItem>();
    public DbSet<WorkItemDependency> WorkItemDependencies => Set<WorkItemDependency>();
    public DbSet<LoopRun> LoopRuns => Set<LoopRun>();
    public DbSet<LoopRunNode> LoopRunNodes => Set<LoopRunNode>();
    public DbSet<LoopRunEdgeTraversal> LoopRunEdgeTraversals => Set<LoopRunEdgeTraversal>();
    public DbSet<EventLog> EventLogs => Set<EventLog>();
    public DbSet<AiProvider> AiProviders => Set<AiProvider>();
    public DbSet<User> Users => Set<User>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        ConfigureIndexes(modelBuilder);
        ConfigureTimestamps(modelBuilder);
        ConfigureConstraints(modelBuilder);
    }

    private void ConfigureIndexes(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<RemoteProvider>(e =>
        {
            e.HasIndex(r => r.Name);
        });

        modelBuilder.Entity<Repository>(e =>
        {
            e.HasIndex(r => r.RemoteProviderId);
            e.HasIndex(r => r.Name);
        });

        modelBuilder.Entity<LoopTemplate>(e =>
        {
            e.HasIndex(l => l.Name);
        });

        modelBuilder.Entity<LoopTemplateVersion>(e =>
        {
            e.HasIndex(l => new { l.LoopTemplateId, l.VersionNumber }).IsUnique();
        });

        modelBuilder.Entity<LoopNode>(e =>
        {
            e.HasIndex(l => l.LoopTemplateVersionId);
        });

        modelBuilder.Entity<LoopNodeEdge>(e =>
        {
            e.HasIndex(l => new { l.SourceNodeId, l.TargetNodeId });
        });

        modelBuilder.Entity<WorkItem>(e =>
        {
            e.HasIndex(w => w.RepositoryId);
            e.HasIndex(w => w.Status);
            e.HasIndex(w => w.CurrentLoopRunId);
        });

        modelBuilder.Entity<WorkItemDependency>(e =>
        {
            e.HasIndex(w => new { w.WorkItemId, w.DependencyWorkItemId }).IsUnique();
        });

        modelBuilder.Entity<LoopRun>(e =>
        {
            e.HasIndex(l => l.WorkItemId);
            e.HasIndex(l => l.LoopTemplateVersionId);
            e.HasIndex(l => l.Status);
        });

        modelBuilder.Entity<LoopRunNode>(e =>
        {
            e.HasIndex(l => l.LoopRunId);
            e.HasIndex(l => l.LoopNodeId);
            e.HasIndex(l => l.Status);
        });

        modelBuilder.Entity<LoopRunEdgeTraversal>(e =>
        {
            e.HasIndex(l => new { l.LoopRunId, l.EdgeId });
        });

        modelBuilder.Entity<EventLog>(e =>
        {
            e.HasIndex(e => new { e.LoopRunId, e.Sequence });
            e.HasIndex(e => e.Timestamp);
        });

        modelBuilder.Entity<AiProvider>(e =>
        {
            e.HasIndex(a => a.Name);
        });

        modelBuilder.Entity<User>(e =>
        {
            e.HasIndex(u => u.Username).IsUnique();
            e.HasIndex(u => u.SessionToken);
        });
    }

    private void ConfigureTimestamps(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<RemoteProvider>(e =>
        {
            e.Property(r => r.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
        });

        modelBuilder.Entity<Repository>(e =>
        {
            e.Property(r => r.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
        });

        modelBuilder.Entity<LoopTemplate>(e =>
        {
            e.Property(l => l.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
        });

        modelBuilder.Entity<LoopTemplateVersion>(e =>
        {
            e.Property(l => l.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
        });

        modelBuilder.Entity<LoopNode>(e =>
        {
            e.Property(l => l.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
        });

        modelBuilder.Entity<LoopNodeEdge>(e =>
        {
            e.Property(l => l.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
        });

        modelBuilder.Entity<WorkItem>(e =>
        {
            e.Property(w => w.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
        });

        modelBuilder.Entity<WorkItemDependency>(e =>
        {
            e.Property(w => w.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
        });

        modelBuilder.Entity<LoopRun>(e =>
        {
            e.Property(l => l.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
        });

        modelBuilder.Entity<LoopRunEdgeTraversal>(e =>
        {
            e.Property(l => l.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
        });

        modelBuilder.Entity<AiProvider>(e =>
        {
            e.Property(a => a.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
        });

        modelBuilder.Entity<User>(e =>
        {
            e.Property(u => u.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
        });
    }

    private void ConfigureConstraints(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<LoopNodeEdge>()
            .HasOne(e => e.SourceNode)
            .WithMany(n => n.OutgoingEdges)
            .HasForeignKey(e => e.SourceNodeId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<LoopNodeEdge>()
            .HasOne(e => e.TargetNode)
            .WithMany(n => n.IncomingEdges)
            .HasForeignKey(e => e.TargetNodeId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<LoopRunNode>()
            .HasOne(rn => rn.LoopRun)
            .WithMany(lr => lr.RunNodes)
            .HasForeignKey(rn => rn.LoopRunId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<WorkItemDependency>()
            .HasOne(wd => wd.WorkItem)
            .WithMany(w => w.Dependencies)
            .HasForeignKey(wd => wd.WorkItemId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<WorkItemDependency>()
            .HasOne(wd => wd.DependentWorkItem)
            .WithMany(w => w.DependentDependencies)
            .HasForeignKey(wd => wd.DependencyWorkItemId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
