using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace ILD.Data;

/// <summary>What a failed save says about why it failed, on the Postgres ILD runs on.</summary>
public static class DbErrors
{
    /// <summary>
    /// The save was refused by a unique index or primary key. Any other failure,
    /// a foreign key one included, is not.
    /// </summary>
    public static bool IsUniqueViolation(DbUpdateException ex)
        => ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };

    /// <summary>
    /// Postgres refused a statement of a repeatable-read or serializable transaction because a
    /// concurrent one changed what it touches; running the transaction again may succeed.
    /// </summary>
    public static bool IsSerializationFailure(Exception ex)
        => (ex as PostgresException ?? ex.InnerException as PostgresException)?.SqlState == PostgresErrorCodes.SerializationFailure;
}
