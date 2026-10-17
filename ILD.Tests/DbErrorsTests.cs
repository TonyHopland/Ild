using ILD.Data;
using ILD.Data.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace ILD.Tests;

/// <summary>
/// A save refused by a unique index or primary key is told apart from every
/// other failure. Production runs on Postgres only; the tests' SQLite contexts
/// report a unique or primary key refusal the way Postgres does, so the code
/// that tells them apart is exercised by the tests as it runs in production.
/// </summary>
public sealed class DbErrorsTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static DbUpdateException PostgresFailure(string sqlState)
        => new("save failed", new PostgresException("refused", "ERROR", "ERROR", sqlState));

    [Fact]
    public void A_postgres_unique_violation_is_one()
        => Assert.True(DbErrors.IsUniqueViolation(PostgresFailure(PostgresErrorCodes.UniqueViolation)));

    [Theory]
    [InlineData(PostgresErrorCodes.ForeignKeyViolation)]
    [InlineData(PostgresErrorCodes.NotNullViolation)]
    [InlineData(PostgresErrorCodes.CheckViolation)]
    [InlineData(PostgresErrorCodes.SerializationFailure)]
    public void Any_other_postgres_failure_is_not(string sqlState)
        => Assert.False(DbErrors.IsUniqueViolation(PostgresFailure(sqlState)));

    [Fact]
    public void A_failure_without_a_database_error_is_not()
        => Assert.False(DbErrors.IsUniqueViolation(new DbUpdateException("save failed", new InvalidOperationException())));

    [Fact]
    public void A_failure_without_an_inner_exception_is_not()
        => Assert.False(DbErrors.IsUniqueViolation(new DbUpdateException("save failed")));

    [Fact]
    public void A_raw_sqlite_unique_violation_is_not_one_production_only_knows_postgres()
        => Assert.False(DbErrors.IsUniqueViolation(
            new DbUpdateException("save failed", new SqliteException("UNIQUE constraint failed", 19, 2067))));

    private static AppSetting Setting(string key, Guid? id = null)
        => new() { Id = id ?? Guid.NewGuid(), Key = key, Value = "v" };

    private async Task SeedAsync(AppSetting setting)
    {
        await using var db = _db.Fresh();
        db.AppSettings.Add(setting);
        await db.SaveChangesAsync(Ct);
    }

    private static void AssertReportedAsPostgresUniqueViolation(DbUpdateException ex)
    {
        var pg = Assert.IsType<PostgresException>(ex.InnerException);
        Assert.Equal(PostgresErrorCodes.UniqueViolation, pg.SqlState);
        Assert.True(DbErrors.IsUniqueViolation(ex));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_test_save_refused_by_a_unique_index_is_reported_as_postgres_reports_it(bool ownConnection)
    {
        await SeedAsync(Setting("theme"));

        await using var db = ownConnection ? _db.OnOwnConnection() : _db.Fresh();
        db.AppSettings.Add(Setting("theme"));
        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(Ct));

        AssertReportedAsPostgresUniqueViolation(ex);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_test_save_refused_by_a_primary_key_is_reported_as_postgres_reports_it(bool asyncSave)
    {
        var id = Guid.NewGuid();
        await SeedAsync(Setting("theme", id));

        await using var db = _db.Fresh();
        db.AppSettings.Add(Setting("other", id));
        var ex = asyncSave
            ? await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(Ct))
            : Assert.Throws<DbUpdateException>(() => db.SaveChanges());

        AssertReportedAsPostgresUniqueViolation(ex);
    }

    [Fact]
    public async Task A_test_save_refused_by_a_foreign_key_is_not_a_unique_violation()
    {
        await using var db = _db.Fresh();
        db.ChatMessages.Add(new ChatMessage
        {
            Id = Guid.NewGuid(),
            ChatSessionId = Guid.NewGuid(),
            Role = "user",
            Content = "hi",
            CreatedAt = DateTime.UtcNow,
        });
        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(Ct));

        Assert.False(DbErrors.IsUniqueViolation(ex));
    }

    [Fact]
    public async Task A_test_save_refused_by_a_not_null_column_is_not_a_unique_violation()
    {
        await using var db = _db.Fresh();
        db.AppSettings.Add(new AppSetting { Id = Guid.NewGuid(), Key = "theme", Value = null! });
        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(Ct));

        Assert.False(DbErrors.IsUniqueViolation(ex));
    }
}
