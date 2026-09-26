using ILD.Data.Entities;
using ILD.Data.Stores;
using Microsoft.EntityFrameworkCore;

namespace ILD.Tests;

public class ProviderStoreTagTests
{
    private static AiProvider MakeProvider(string name) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        Type = "claude-code",
        Model = "m",
        CreatedAt = DateTime.UtcNow,
    };

    private static List<string> TagsOf(AppDbContext db, Guid providerId)
        => db.Set<AiProviderTag>().Where(t => t.AiProviderId == providerId).Select(t => t.Name).ToList();

    [Fact]
    public async Task The_database_refuses_a_second_holder_of_the_same_tag_whatever_its_case()
    {
        using var db = new TestDb();
        var a = MakeProvider("a");
        var b = MakeProvider("b");
        db.Context.AiProviders.AddRange(a, b);
        await db.Context.SaveChangesAsync(TestContext.Current.CancellationToken);

        // Straight past the store: the invariant has to hold for any writer.
        await using var raw = db.Fresh();
        raw.Set<AiProviderTag>().Add(new AiProviderTag
        {
            Id = Guid.NewGuid(), AiProviderId = a.Id, Name = "qa", NormalizedName = "QA", CreatedAt = DateTime.UtcNow,
        });
        await raw.SaveChangesAsync(TestContext.Current.CancellationToken);
        raw.Set<AiProviderTag>().Add(new AiProviderTag
        {
            Id = Guid.NewGuid(), AiProviderId = b.Id, Name = "QA", NormalizedName = "QA", CreatedAt = DateTime.UtcNow,
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => raw.SaveChangesAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_failed_save_does_not_move_the_tag_off_its_holder()
    {
        using var db = new TestDb();
        var store = new ProviderStore(db.Context);
        var holder = MakeProvider("holder");
        var victim = MakeProvider("victim");
        await store.CreateAiProviderAsync(holder, ["QA", "Fast"]);
        await store.CreateAiProviderAsync(victim);

        // Collide with `victim`'s id so the save fails after the move has been
        // staged — without one transaction around both, holder loses "QA".
        var collision = MakeProvider("collision");
        collision.Id = victim.Id;
        await Assert.ThrowsAnyAsync<Exception>(() => store.CreateAiProviderAsync(collision, ["qa"]));

        await using var verify = db.Fresh();
        Assert.Equal(["Fast", "QA"], TagsOf(verify, holder.Id).Order(StringComparer.OrdinalIgnoreCase));
        Assert.Single(verify.Set<AiProviderTag>().AsEnumerable(),
            t => string.Equals(t.Name, "qa", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Deleting_a_provider_deletes_its_tags_and_frees_the_names()
    {
        using var db = new TestDb();
        var gone = MakeProvider("gone");
        await new ProviderStore(db.Context).CreateAiProviderAsync(gone, ["QA", "Fast"]);

        await using (var other = db.Fresh())
        {
            var store = new ProviderStore(other);
            await store.DeleteAiProviderAsync((await store.GetAiProviderByIdAsync(gone.Id))!);
        }

        await using (var verify = db.Fresh())
            Assert.Empty(verify.Set<AiProviderTag>());

        var next = MakeProvider("next");
        await using (var other = db.Fresh())
            await new ProviderStore(other).CreateAiProviderAsync(next, ["qa"]);

        await using var check = db.Fresh();
        Assert.Equal(["qa"], TagsOf(check, next.Id));
    }
}
