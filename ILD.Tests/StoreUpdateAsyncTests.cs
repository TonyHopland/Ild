using FluentAssertions;
using ILD.Data.Entities;
using ILD.Data.Enums;
using ILD.Data.Stores;

namespace ILD.Tests;

public class StoreUpdateAsyncTests
{
    [Fact]
    public async Task WorkItemStore_UpdateAsync_persists_changes_across_contexts()
    {
        using var db = new TestDb();

        var rp = new RemoteProvider
        {
            Id = Guid.NewGuid(),
            Name = "rp",
            Type = "GitHub",
            Url = "https://x",
        };
        db.Context.RemoteProviders.Add(rp);

        var repo = new Repository
        {
            Id = Guid.NewGuid(),
            Name = "r",
            CloneUrl = "u",
            DefaultIntakeStatus = WorkItemStatus.Backlog,
            RemoteProviderId = rp.Id,
        };
        db.Context.Repositories.Add(repo);

        var id = Guid.NewGuid();
        db.Context.WorkItems.Add(new WorkItem
        {
            Id = id,
            Title = "t",
            Description = "d",
            Status = WorkItemStatus.Backlog,
            RepositoryId = repo.Id,
        });
        await db.Context.SaveChangesAsync();

        // Load in one context, update via another (mirrors scoped DI in production).
        WorkItem loaded;
        using (var ctx = db.Fresh())
        {
            loaded = (await ctx.WorkItems.FindAsync(id))!;
        }
        loaded.Status = WorkItemStatus.Ready;
        loaded.Title = "updated";

        using (var ctx = db.Fresh())
        {
            var store = new WorkItemStore(ctx);
            await store.UpdateAsync(loaded);
        }

        using var verify = db.Fresh();
        var reloaded = await verify.WorkItems.FindAsync(id);
        reloaded!.Status.Should().Be(WorkItemStatus.Ready);
        reloaded.Title.Should().Be("updated");
    }

    [Fact]
    public async Task ProviderStore_UpdateRemoteProviderAsync_persists_changes_across_contexts()
    {
        using var db = new TestDb();

        var id = Guid.NewGuid();
        db.Context.RemoteProviders.Add(new RemoteProvider
        {
            Id = id,
            Name = "old",
            Type = "GitHub",
            Url = "https://api.github.com",
            ApiKey = "k",
            WebhookSecret = "s",
        });
        await db.Context.SaveChangesAsync();

        RemoteProvider loaded;
        using (var ctx = db.Fresh())
        {
            loaded = (await ctx.RemoteProviders.FindAsync(id))!;
        }
        loaded.Name = "new";

        using (var ctx = db.Fresh())
        {
            var store = new ProviderStore(ctx);
            await store.UpdateRemoteProviderAsync(loaded);
        }

        using var verify = db.Fresh();
        var reloaded = await verify.RemoteProviders.FindAsync(id);
        reloaded!.Name.Should().Be("new");
    }
}
