using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace ILD.Data;

/// <summary>What a failed save says about why it failed, on either database ILD runs on.</summary>
public static class DbErrors
{
    private const int SqliteConstraint = 19;
    private const int SqliteConstraintUnique = 2067;
    private const int SqliteConstraintPrimaryKey = 1555;

    /// <summary>
    /// The save was refused by a unique index or primary key, on Postgres or on
    /// SQLite (the tests). Any other failure, a foreign key one included, is not.
    /// </summary>
    public static bool IsUniqueViolation(DbUpdateException ex) => ex.InnerException switch
    {
        PostgresException pg => pg.SqlState == PostgresErrorCodes.UniqueViolation,
        SqliteException sqlite => sqlite.SqliteErrorCode == SqliteConstraint
            && sqlite.SqliteExtendedErrorCode is SqliteConstraintUnique or SqliteConstraintPrimaryKey,
        _ => false,
    };
}
