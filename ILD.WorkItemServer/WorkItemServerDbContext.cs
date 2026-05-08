using ILD.WorkItemServer.Domain;
using Microsoft.EntityFrameworkCore;

namespace ILD.WorkItemServer;

public sealed class WorkItemServerDbContext : DbContext
{
    public WorkItemServerDbContext(DbContextOptions<WorkItemServerDbContext> options)
        : base(options) { }

    public DbSet<WorkItem> WorkItems => Set<WorkItem>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<WorkItem>(b =>
        {
            b.HasKey(w => w.Id);
            b.HasIndex(w => w.Status);
            b.Property(w => w.Status).HasConversion<int>();
            b.Property(w => w.Priority).HasConversion<int>();
        });
    }
}
