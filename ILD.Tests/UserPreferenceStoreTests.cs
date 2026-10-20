using ILD.Data;
using ILD.Data.Entities;
using ILD.Data.Stores;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace ILD.Tests;

public sealed class UserPreferenceStoreTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<Guid> AddUser()
    {
        var user = new User { Id = Guid.NewGuid(), Username = "user-one", PasswordHash = "x" };
        await _db.Auth.CreateUserAsync(user);
        return user.Id;
    }

    [Fact]
    public async Task Two_devices_setting_the_same_new_key_at_once_keep_one_row_with_the_later_value()
    {
        var userId = await AddUser();
        // The other device's insert lands after this store looked and found no
        // row, just before its own insert is saved.
        var otherDevice = new InsertBeforeSave(() => _db.Fresh(), userId, "chat.enabled", "true");
        using var context = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_db.Context.Database.GetDbConnection())
            .WithPostgresSemantics()
            .AddInterceptors(otherDevice)
            .Options);

        await new UserPreferenceStore(context).UpsertAsync(userId, "chat.enabled", "false", Ct);

        Assert.True(otherDevice.Inserted);
        using var after = _db.Fresh();
        var rows = await after.UserPreferences.AsNoTracking().Where(p => p.UserId == userId).ToListAsync(Ct);
        Assert.Equal("false", Assert.Single(rows).Value);
    }

    [Fact]
    public async Task A_save_refused_for_another_reason_is_not_taken_for_the_race()
    {
        // No such user: the foreign key refuses the insert, and that is no unique
        // violation to recover from.
        await Assert.ThrowsAsync<DbUpdateException>(
            () => new UserPreferenceStore(_db.Fresh()).UpsertAsync(Guid.NewGuid(), "chat.enabled", "false", Ct));
    }

    private sealed class InsertBeforeSave(Func<AppDbContext> other, Guid userId, string key, string value)
        : SaveChangesInterceptor
    {
        public bool Inserted { get; private set; }

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            var adding = eventData.Context!.ChangeTracker.Entries<UserPreference>().Any(e => e.State == EntityState.Added);
            if (!Inserted && adding)
            {
                Inserted = true;
                using var db = other();
                db.UserPreferences.Add(new UserPreference { Id = Guid.NewGuid(), UserId = userId, Key = key, Value = value });
                await db.SaveChangesAsync(cancellationToken);
            }
            return result;
        }
    }
}
