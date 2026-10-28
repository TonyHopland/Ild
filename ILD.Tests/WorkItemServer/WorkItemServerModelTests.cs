using ILD.WorkItemServer;
using ILD.WorkItemServer.Domain;
using Microsoft.EntityFrameworkCore;

namespace ILD.Tests.WorkItemServer;

/// <summary>The WorkItem server's model and migrations, read without starting a server.</summary>
public sealed class WorkItemServerModelTests
{
    [Fact]
    public void The_servers_model_has_no_change_that_lacks_a_scaffolded_migration()
    {
        // The tests run the server on SQLite through EnsureCreated, so only this
        // sees a model change that production's Migrate() would never apply.
        using var db = new DesignTimeDbContextFactory().CreateDbContext([]);

        Assert.False(db.Database.HasPendingModelChanges());
    }

    [Fact]
    public void Attachments_are_a_cascading_child_of_the_work_items_key()
    {
        using var db = new DesignTimeDbContextFactory().CreateDbContext([]);
        var entity = db.Model.FindEntityType(typeof(WorkItemAttachment));

        Assert.NotNull(entity);
        Assert.Equal(nameof(WorkItemAttachment.Id), Assert.Single(entity!.FindPrimaryKey()!.Properties).Name);
        Assert.Equal(typeof(Guid), entity.FindPrimaryKey()!.Properties[0].ClrType);

        var foreignKey = Assert.Single(entity.GetForeignKeys());
        Assert.Equal(typeof(WorkItem), foreignKey.PrincipalEntityType.ClrType);
        Assert.Equal(nameof(WorkItem.InternalId), Assert.Single(foreignKey.PrincipalKey.Properties).Name);
        Assert.Equal(nameof(WorkItemAttachment.WorkItemId), Assert.Single(foreignKey.Properties).Name);
        // The server deletes a work item without ever loading its attachments,
        // so nothing but the database's own cascade collects them.
        Assert.Equal(DeleteBehavior.Cascade, foreignKey.DeleteBehavior);
    }

    [Fact]
    public void One_scaffolded_migration_adds_the_attachments_table()
    {
        var migrations = Directory.GetFiles(Path.Combine(RepositoryFiles.Root, "ILD.WorkItemServer", "Migrations"), "*.cs")
            .Where(f => !f.EndsWith(".Designer.cs", StringComparison.Ordinal))
            .Where(f => !f.EndsWith("ModelSnapshot.cs", StringComparison.Ordinal))
            .Where(f => File.ReadAllText(f).Contains("WorkItemAttachments", StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .ToList();

        Assert.Single(migrations);
        // The snapshot is only updated by scaffolding; a hand-written migration leaves it behind.
        Assert.Contains("WorkItemAttachments", RepositoryFiles.ReadAllText(
            Path.Combine("ILD.WorkItemServer", "Migrations", "WorkItemServerDbContextModelSnapshot.cs")), StringComparison.Ordinal);
    }
}
