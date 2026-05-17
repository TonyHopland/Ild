using ILD.Data.Entities;
using ILD.Data.Stores;

namespace ILD.Tests;

public class StoreUpdateAsyncTests
{
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
        Assert.Equal("new", reloaded!.Name);
    }
}
