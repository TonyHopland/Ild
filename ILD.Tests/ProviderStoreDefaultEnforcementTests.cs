using ILD.Data.Entities;
using ILD.Data.Stores;

namespace ILD.Tests;

public class ProviderStoreDefaultEnforcementTests
{
    private static AiProvider MakeProvider(string name, bool isDefault) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        Type = "opencode",
        BaseUrl = "https://example.com",
        Model = "gpt-test",
        IsDefault = isDefault,
        CreatedAt = DateTime.UtcNow,
    };

    [Fact]
    public async Task Creating_provider_as_default_clears_previous_default()
    {
        using var db = new TestDb();
        var store = new ProviderStore(db.Context);

        var first = MakeProvider("first", isDefault: true);
        await store.CreateAiProviderAsync(first);

        var second = MakeProvider("second", isDefault: true);
        await store.CreateAiProviderAsync(second);

        using var verify = db.Fresh();
        var firstReloaded = await verify.AiProviders.FindAsync(first.Id);
        var secondReloaded = await verify.AiProviders.FindAsync(second.Id);
        Assert.False(firstReloaded!.IsDefault);
        Assert.True(secondReloaded!.IsDefault);
    }

    [Fact]
    public async Task Creating_non_default_provider_leaves_existing_default_intact()
    {
        using var db = new TestDb();
        var store = new ProviderStore(db.Context);

        var first = MakeProvider("first", isDefault: true);
        await store.CreateAiProviderAsync(first);

        var second = MakeProvider("second", isDefault: false);
        await store.CreateAiProviderAsync(second);

        using var verify = db.Fresh();
        var firstReloaded = await verify.AiProviders.FindAsync(first.Id);
        var secondReloaded = await verify.AiProviders.FindAsync(second.Id);
        Assert.True(firstReloaded!.IsDefault);
        Assert.False(secondReloaded!.IsDefault);
    }

    [Fact]
    public async Task Updating_provider_to_default_clears_previous_default()
    {
        using var db = new TestDb();
        var store = new ProviderStore(db.Context);

        var first = MakeProvider("first", isDefault: true);
        var second = MakeProvider("second", isDefault: false);
        await store.CreateAiProviderAsync(first);
        await store.CreateAiProviderAsync(second);

        // Promote second to default via update.
        var loaded = (await store.GetAiProviderByIdAsync(second.Id))!;
        loaded.IsDefault = true;
        await store.UpdateAiProviderAsync(loaded);

        using var verify = db.Fresh();
        var firstReloaded = await verify.AiProviders.FindAsync(first.Id);
        var secondReloaded = await verify.AiProviders.FindAsync(second.Id);
        Assert.False(firstReloaded!.IsDefault);
        Assert.True(secondReloaded!.IsDefault);
        Assert.Single(verify.AiProviders.Where(p => p.IsDefault));
    }

    [Fact]
    public async Task Failed_create_rolls_back_clearing_of_previous_default()
    {
        using var db = new TestDb();
        var store = new ProviderStore(db.Context);

        var currentDefault = MakeProvider("currentDefault", isDefault: true);
        var victim = MakeProvider("victim", isDefault: false);
        await store.CreateAiProviderAsync(currentDefault);
        await store.CreateAiProviderAsync(victim);

        // Collide with `victim` (not the default) so the ExecuteUpdate inside
        // the transaction actually demotes `currentDefault` before Add throws —
        // otherwise the test would pass even without rollback.
        var collision = MakeProvider("collision", isDefault: true);
        collision.Id = victim.Id;

        await Assert.ThrowsAnyAsync<Exception>(() => store.CreateAiProviderAsync(collision));

        using var verify = db.Fresh();
        var reloaded = await verify.AiProviders.FindAsync(currentDefault.Id);
        Assert.True(reloaded!.IsDefault);
        Assert.Single(verify.AiProviders.Where(p => p.IsDefault));
    }

    [Fact]
    public async Task At_most_one_default_after_multiple_promotions()
    {
        using var db = new TestDb();
        var store = new ProviderStore(db.Context);

        var a = MakeProvider("a", isDefault: true);
        var b = MakeProvider("b", isDefault: false);
        var c = MakeProvider("c", isDefault: false);
        await store.CreateAiProviderAsync(a);
        await store.CreateAiProviderAsync(b);
        await store.CreateAiProviderAsync(c);

        var bLoaded = (await store.GetAiProviderByIdAsync(b.Id))!;
        bLoaded.IsDefault = true;
        await store.UpdateAiProviderAsync(bLoaded);

        var cLoaded = (await store.GetAiProviderByIdAsync(c.Id))!;
        cLoaded.IsDefault = true;
        await store.UpdateAiProviderAsync(cLoaded);

        using var verify = db.Fresh();
        var defaults = verify.AiProviders.Where(p => p.IsDefault).ToList();
        Assert.Single(defaults);
        Assert.Equal(c.Id, defaults[0].Id);
    }
}
