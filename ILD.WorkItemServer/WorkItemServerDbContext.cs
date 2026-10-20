using ILD.WorkItemServer.Domain;
using Microsoft.EntityFrameworkCore;

namespace ILD.WorkItemServer;

public sealed class WorkItemServerDbContext : DbContext
{
    private readonly TimeProvider _clock;
    private readonly Dictionary<WorkItem, string> _historyReasons = new(ReferenceEqualityComparer.Instance);

    public WorkItemServerDbContext(DbContextOptions<WorkItemServerDbContext> options)
        : this(options, TimeProvider.System) { }

    public WorkItemServerDbContext(DbContextOptions<WorkItemServerDbContext> options, TimeProvider clock)
        : base(options)
    {
        _clock = clock;
    }

    public DbSet<WorkItem> WorkItems => Set<WorkItem>();

    public DbSet<WorkItemAttachment> WorkItemAttachments => Set<WorkItemAttachment>();

    public DbSet<WorkItemEditProposal> WorkItemEditProposals => Set<WorkItemEditProposal>();

    public DbSet<WorkItemHistoryEntry> WorkItemHistoryEntries => Set<WorkItemHistoryEntry>();

    /// <summary>
    /// Who this context's writes are made for: the request's actor, or a hosted
    /// service's own. A save that changes a tracked work item field without one
    /// is refused, so history never holds an unattributed row.
    /// </summary>
    public WorkItemActor? HistoryActor { get; set; }

    /// <summary>
    /// Why the next save changes <paramref name="item"/>, recorded on each of its
    /// history rows. Consumed by that save, whether it succeeds or fails.
    /// </summary>
    public void SetHistoryReason(WorkItem item, string? reason)
    {
        if (WorkItemHistoryFields.NormalizeReason(reason) is { } normalized)
            _historyReasons[item] = normalized;
        else
            _historyReasons.Remove(item);
    }

    /// <summary>
    /// Add one save's history rows for a write the change tracker does not see —
    /// a new item's Created save, or a bulk update. Written by the next
    /// SaveChanges, so a caller that needs it atomic with its update holds both
    /// in one transaction.
    /// </summary>
    public void AddHistorySave(string workItemId, WorkItemHistoryKind kind, IEnumerable<(string Field, string? Value)> fields, string? reason)
        => AddHistoryRows(RequireActor(), NewSaveId(), _clock.GetUtcNow().UtcDateTime, workItemId, kind, fields, reason);

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        CaptureHistory();
        try
        {
            return base.SaveChanges(acceptAllChangesOnSuccess);
        }
        catch
        {
            DetachUnsavedHistory();
            throw;
        }
    }

    public override async Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        CaptureHistory();
        try
        {
            return await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
        }
        catch
        {
            DetachUnsavedHistory();
            throw;
        }
    }

    /// <summary>
    /// One Changed save for every modified work item whose tracked fields changed,
    /// and one Deleted save for every deleted one, all sharing one save id. Added
    /// items are left to their creator, which only knows the id the rows are keyed
    /// by after the insert.
    /// </summary>
    private void CaptureHistory()
    {
        try
        {
            ChangeTracker.DetectChanges();
            var changes = ChangeTracker.Entries<WorkItem>()
                .Select(e => e.State switch
                {
                    EntityState.Modified => (Item: e.Entity, Kind: WorkItemHistoryKind.Changed,
                        Fields: WorkItemHistoryFields.Changed(e.OriginalValues, e.CurrentValues)),
                    EntityState.Deleted => (Item: e.Entity, Kind: WorkItemHistoryKind.Deleted,
                        Fields: [(WorkItemHistoryFields.Deleted, e.Entity.Title)]),
                    _ => (Item: e.Entity, Kind: WorkItemHistoryKind.Changed, Fields: []),
                })
                .Where(c => c.Fields.Count > 0)
                .ToList();
            if (changes.Count == 0) return;

            var actor = RequireActor();
            var saveId = NewSaveId();
            var now = _clock.GetUtcNow().UtcDateTime;
            foreach (var (item, kind, fields) in changes)
                AddHistoryRows(actor, saveId, now, item.Id, kind, fields, _historyReasons.GetValueOrDefault(item));
        }
        finally
        {
            _historyReasons.Clear();
        }
    }

    private void AddHistoryRows(
        WorkItemActor actor, string saveId, DateTime timestamp, string workItemId, WorkItemHistoryKind kind,
        IEnumerable<(string Field, string? Value)> fields, string? reason)
    {
        var rows = fields.Select(f => new WorkItemHistoryEntry
        {
            WorkItemId = workItemId,
            Timestamp = timestamp,
            SaveId = saveId,
            Kind = kind,
            Username = actor.Username,
            ActorKind = actor.Kind,
            ActorDetail = actor.Detail,
            ActorWorkItemId = actor.WorkItemId,
            Field = f.Field,
            Value = f.Value,
            Reason = WorkItemHistoryFields.NormalizeReason(reason),
        }).ToList();
        WorkItemHistoryEntries.AddRange(rows);
    }

    private WorkItemActor RequireActor()
        => HistoryActor ?? throw new InvalidOperationException(
            "A work item change needs an actor for its history, and this context has none.");

    private static string NewSaveId() => Guid.NewGuid().ToString("N");

    /// <summary>A failed save's history rows go with it, so a retry does not write them twice.</summary>
    private void DetachUnsavedHistory()
    {
        foreach (var row in ChangeTracker.Entries<WorkItemHistoryEntry>().Where(e => e.State == EntityState.Added).ToList())
            row.State = EntityState.Detached;
    }

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

        modelBuilder.Entity<WorkItemHistoryEntry>(b =>
        {
            b.HasKey(e => e.Id);
            b.HasIndex(e => new { e.WorkItemId, e.Field, e.Timestamp });
            b.Property(e => e.Kind).HasConversion<int>();
            b.Property(e => e.ActorKind).HasConversion<int>();
            b.Property(e => e.Value).HasColumnType("text");
        });
    }
}
