using ILD.Data.Entities;
using ILD.Data.Enums;

namespace ILD.Tests;

public class AppDbContextUpdatedAtTests
{
    [Fact]
    public async Task SaveChanges_auto_sets_UpdatedAt_on_modified_entities()
    {
        using var db = new TestDb();
        var template = new LoopTemplate { Id = Guid.NewGuid(), Name = "t", RecoveryPolicy = RecoveryPolicy.AutoResume };
        db.Context.LoopTemplates.Add(template);
        await db.Context.SaveChangesAsync();

        // Initial UpdatedAt should be null (only set on modify, not on insert).
        Assert.Null(template.UpdatedAt);

        var before = DateTime.UtcNow;
        template.Name = "renamed";
        await db.Context.SaveChangesAsync();

        var reloaded = db.Fresh().LoopTemplates.First(t => t.Id == template.Id);
        Assert.NotNull(reloaded.UpdatedAt);
        Assert.True(reloaded.UpdatedAt!.Value >= before.AddSeconds(-1));
    }
}
