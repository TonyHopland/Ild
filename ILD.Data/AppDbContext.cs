using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using ILD.Data.Enums;
using ILD.Data.Security;

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

    public DbSet<LoopRun> LoopRuns => Set<LoopRun>();
    public DbSet<LoopRunNode> LoopRunNodes => Set<LoopRunNode>();
    public DbSet<LoopRunAnalyticsBucket> LoopRunAnalyticsBuckets => Set<LoopRunAnalyticsBucket>();
    public DbSet<AdapterSessionSnapshot> AdapterSessionSnapshots => Set<AdapterSessionSnapshot>();
    public DbSet<LoopRunSessionBinding> LoopRunSessionBindings => Set<LoopRunSessionBinding>();
    public DbSet<LoopRunVariable> LoopRunVariables => Set<LoopRunVariable>();
    public DbSet<EventLog> EventLogs => Set<EventLog>();
    public DbSet<AiProvider> AiProviders => Set<AiProvider>();
    public DbSet<User> Users => Set<User>();
    public DbSet<AppSetting> AppSettings => Set<AppSetting>();
    public DbSet<ChatSession> ChatSessions => Set<ChatSession>();
    public DbSet<ChatMessage> ChatMessages => Set<ChatMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        ConfigureIndexes(modelBuilder);
        ConfigureTimestamps(modelBuilder);
        ConfigureConstraints(modelBuilder);
        ConfigureEnumConversions(modelBuilder);
        ConfigureSecretProtection(modelBuilder);
    }

    /// <summary>
    /// Transparently encrypts credential columns at rest via <see cref="SecretProtector"/>.
    /// When no encryption key is configured this is a passthrough, and legacy plaintext
    /// rows remain readable, so toggling the feature never breaks an existing database.
    /// Columns are widened to accommodate the encrypted envelope.
    /// </summary>
    private static void ConfigureSecretProtection(ModelBuilder modelBuilder)
    {
        var converter = new ValueConverter<string?, string?>(
            v => SecretProtector.Protect(v),
            v => SecretProtector.Unprotect(v));

        modelBuilder.Entity<RemoteProvider>(e =>
        {
            e.Property(r => r.ApiKey).HasConversion(converter).HasMaxLength(2048);
            e.Property(r => r.WebhookSecret).HasConversion(converter).HasMaxLength(1024);
        });

        modelBuilder.Entity<AiProvider>(e =>
        {
            e.Property(a => a.ApiKey).HasConversion(converter).HasMaxLength(2048);
        });
    }

    private void ConfigureEnumConversions(ModelBuilder modelBuilder)
    {
        // AI cost is money: fix the precision so Postgres stores a numeric(18,6)
        // rather than defaulting to a lossy/ambiguous column.
        modelBuilder.Entity<LoopRunNode>()
            .Property(rn => rn.CostUsd)
            .HasPrecision(18, 6);
        modelBuilder.Entity<LoopRunAnalyticsBucket>()
            .Property(b => b.TotalCostUsd)
            .HasPrecision(18, 6);
        modelBuilder.Entity<LoopTemplate>()
            .Property(t => t.RecoveryPolicy)
            .HasConversion<string>()
            .HasMaxLength(128);
        modelBuilder.Entity<LoopRun>()
            .Property(r => r.RecoveryPolicy)
            .HasConversion<string>()
            .HasMaxLength(128);
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

        modelBuilder.Entity<LoopRun>(e =>
        {
            e.HasIndex(l => l.WorkItemId);
            e.HasIndex(l => l.LoopTemplateVersionId);
            e.HasIndex(l => l.Status);
            e.HasIndex(l => l.PrUrl);
        });

        modelBuilder.Entity<LoopRunNode>(e =>
        {
            e.HasIndex(l => l.LoopRunId);
            e.HasIndex(l => l.LoopNodeId);
            e.HasIndex(l => l.Status);
        });

        modelBuilder.Entity<LoopRunAnalyticsBucket>(e =>
        {
            e.HasIndex(b => new { b.BucketDate, b.LoopTemplateId, b.AiProvider }).IsUnique();
            e.HasIndex(b => b.BucketDate);
        });

        modelBuilder.Entity<AdapterSessionSnapshot>(e =>
        {
            // Surrogate key: the owner (LoopRun OR ChatSession) is nullable, so a
            // composite key over LoopRunId no longer works. Uniqueness per owner is
            // enforced by the two filtered indexes below.
            e.HasKey(s => s.Id);
            e.HasIndex(s => new { s.LoopRunId, s.AdapterName, s.SessionId })
                .IsUnique()
                .HasFilter(null);
            e.HasIndex(s => new { s.ChatSessionId, s.AdapterName, s.SessionId })
                .IsUnique()
                .HasFilter(null);
        });

        modelBuilder.Entity<ChatSession>(e =>
        {
            e.HasIndex(c => c.UserId).IsUnique();
        });

        modelBuilder.Entity<ChatMessage>(e =>
        {
            e.HasIndex(m => new { m.ChatSessionId, m.Sequence });
        });

        modelBuilder.Entity<LoopRunSessionBinding>(e =>
        {
            e.HasKey(s => new { s.LoopRunId, s.AdapterName, s.PlaceholderId });
            e.HasIndex(s => new { s.LoopRunId, s.AdapterName, s.SessionId });
        });

        modelBuilder.Entity<LoopRunVariable>(e =>
        {
            e.HasKey(v => new { v.LoopRunId, v.Name });
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

        modelBuilder.Entity<AppSetting>(e =>
        {
            e.HasIndex(s => s.Key).IsUnique();
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

        modelBuilder.Entity<LoopRun>(e =>
        {
            e.Property(l => l.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
        });

        modelBuilder.Entity<AdapterSessionSnapshot>(e =>
        {
            e.Property(s => s.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
        });

        modelBuilder.Entity<LoopRunSessionBinding>(e =>
        {
            e.Property(s => s.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
        });

        modelBuilder.Entity<LoopRunVariable>(e =>
        {
            e.Property(v => v.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
        });

        modelBuilder.Entity<AiProvider>(e =>
        {
            e.Property(a => a.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
        });

        modelBuilder.Entity<User>(e =>
        {
            e.Property(u => u.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
        });

        modelBuilder.Entity<ChatSession>(e =>
        {
            e.Property(c => c.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
        });

        modelBuilder.Entity<ChatMessage>(e =>
        {
            e.Property(m => m.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
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

        modelBuilder.Entity<AdapterSessionSnapshot>()
            .HasOne(s => s.LoopRun)
            .WithMany()
            .HasForeignKey(s => s.LoopRunId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<AdapterSessionSnapshot>()
            .HasOne(s => s.ChatSession)
            .WithMany()
            .HasForeignKey(s => s.ChatSessionId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<ChatMessage>()
            .HasOne(m => m.ChatSession)
            .WithMany(c => c.Messages)
            .HasForeignKey(m => m.ChatSessionId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<LoopRunSessionBinding>()
            .HasOne(s => s.LoopRun)
            .WithMany()
            .HasForeignKey(s => s.LoopRunId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<LoopRunVariable>()
            .HasOne(v => v.LoopRun)
            .WithMany(lr => lr.Variables)
            .HasForeignKey(v => v.LoopRunId)
            .OnDelete(DeleteBehavior.Cascade);
    }

    private void TouchUpdatedAt()
    {
        var now = DateTime.UtcNow;
        foreach (var entry in ChangeTracker.Entries<IHasUpdatedAt>())
        {
            if (entry.State == EntityState.Modified)
                entry.Entity.UpdatedAt = now;
        }
    }

    public override int SaveChanges()
    {
        TouchUpdatedAt();
        return base.SaveChanges();
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        TouchUpdatedAt();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        TouchUpdatedAt();
        return base.SaveChangesAsync(cancellationToken);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        TouchUpdatedAt();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }
}
