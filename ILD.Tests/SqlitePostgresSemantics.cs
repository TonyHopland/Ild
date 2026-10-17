using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;

namespace ILD.Tests;

/// <summary>
/// Makes a test SQLite database answer the way production's Postgres does where
/// production code depends on it, so that code stays written for Postgres alone
/// (AGENTS.md, "Databases").
/// </summary>
public static class SqlitePostgresSemantics
{
    private const int SqliteConstraint = 19;
    private const int SqliteConstraintUnique = 2067;
    private const int SqliteConstraintPrimaryKey = 1555;

    /// <summary>
    /// A save refused by a unique index or primary key fails with the
    /// <see cref="PostgresException"/> Postgres would give, and <c>lower()</c> folds
    /// every letter as Postgres does, not only ASCII ones, on each connection EF
    /// opens. A connection that is already open needs <see cref="AddUnicodeLower"/>.
    /// </summary>
    public static TBuilder WithPostgresSemantics<TBuilder>(this TBuilder options) where TBuilder : DbContextOptionsBuilder
    {
        options.AddInterceptors(UniqueViolations.Instance, UnicodeLowerOnOpen.Instance);
        return options;
    }

    /// <summary>Replaces SQLite's ASCII-only <c>lower()</c> on <paramref name="connection"/> with a Unicode one.</summary>
    public static void AddUnicodeLower(SqliteConnection connection)
        => connection.CreateFunction("lower", (string? value) => value?.ToLowerInvariant(), isDeterministic: true);

    private sealed class UniqueViolations : SaveChangesInterceptor
    {
        public static readonly UniqueViolations Instance = new();

        public override void SaveChangesFailed(DbContextErrorEventData eventData)
            => ThrowIfUniqueViolation(eventData.Exception);

        public override Task SaveChangesFailedAsync(DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
        {
            ThrowIfUniqueViolation(eventData.Exception);
            return Task.CompletedTask;
        }

        private static void ThrowIfUniqueViolation(Exception exception)
        {
            if (exception is DbUpdateException { InnerException: SqliteException sqlite } update
                && sqlite.SqliteErrorCode == SqliteConstraint
                && sqlite.SqliteExtendedErrorCode is SqliteConstraintUnique or SqliteConstraintPrimaryKey)
            {
                throw new DbUpdateException(
                    update.Message,
                    new PostgresException(sqlite.Message, "ERROR", "ERROR", PostgresErrorCodes.UniqueViolation),
                    update.Entries);
            }
        }
    }

    private sealed class UnicodeLowerOnOpen : DbConnectionInterceptor
    {
        public static readonly UnicodeLowerOnOpen Instance = new();

        public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
            => AddUnicodeLower((SqliteConnection)connection);

        public override Task ConnectionOpenedAsync(
            DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
        {
            AddUnicodeLower((SqliteConnection)connection);
            return Task.CompletedTask;
        }
    }
}
