using ILD.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace ILD.Tests;

/// <summary>
/// A save refused by a unique index or primary key is told apart from every
/// other failure, on Postgres (production) and SQLite (the tests) alike.
/// </summary>
public sealed class DbErrorsTests : IDisposable
{
    private readonly SqliteConnection _sqlite = new("Data Source=:memory:");

    public DbErrorsTests()
    {
        _sqlite.Open();
        Run("""
            PRAGMA foreign_keys = ON;
            CREATE TABLE parent (id INTEGER PRIMARY KEY);
            CREATE TABLE child (id INTEGER PRIMARY KEY, parent_id INTEGER NOT NULL REFERENCES parent(id), name TEXT NOT NULL);
            CREATE UNIQUE INDEX ix_child_name ON child(name);
            INSERT INTO parent VALUES (1);
            INSERT INTO child VALUES (1, 1, 'a');
            """);
    }

    public void Dispose() => _sqlite.Dispose();

    private void Run(string sql)
    {
        using var command = _sqlite.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private DbUpdateException SqliteFailure(string sql)
        => new("save failed", Assert.Throws<SqliteException>(() => Run(sql)));

    private static DbUpdateException PostgresFailure(string sqlState)
        => new("save failed", new PostgresException("refused", "ERROR", "ERROR", sqlState));

    [Fact]
    public void A_postgres_unique_violation_is_one()
        => Assert.True(DbErrors.IsUniqueViolation(PostgresFailure(PostgresErrorCodes.UniqueViolation)));

    [Fact]
    public void A_postgres_foreign_key_violation_is_not()
        => Assert.False(DbErrors.IsUniqueViolation(PostgresFailure(PostgresErrorCodes.ForeignKeyViolation)));

    [Fact]
    public void A_sqlite_unique_index_violation_is_one()
        => Assert.True(DbErrors.IsUniqueViolation(SqliteFailure("INSERT INTO child VALUES (2, 1, 'a')")));

    [Fact]
    public void A_sqlite_primary_key_violation_is_one()
        => Assert.True(DbErrors.IsUniqueViolation(SqliteFailure("INSERT INTO child VALUES (1, 1, 'b')")));

    [Fact]
    public void A_sqlite_foreign_key_violation_is_not()
        => Assert.False(DbErrors.IsUniqueViolation(SqliteFailure("INSERT INTO child VALUES (2, 99, 'b')")));

    [Fact]
    public void A_sqlite_not_null_violation_is_not()
        => Assert.False(DbErrors.IsUniqueViolation(SqliteFailure("INSERT INTO child VALUES (2, 1, NULL)")));

    [Fact]
    public void A_failure_without_a_database_error_is_not()
        => Assert.False(DbErrors.IsUniqueViolation(new DbUpdateException("save failed", new InvalidOperationException())));
}
