using System.Data.Common;
using Npgsql;

namespace ILD.WorkItemServer;

/// <summary>
/// Caps the connection pool of an operator-supplied Npgsql connection string so the
/// services sharing one Postgres stay inside its connection budget
/// (docs/configuration.md). A key the operator set, under any of Npgsql's aliases,
/// is kept as written.
/// </summary>
public static class NpgsqlPoolDefaults
{
    public const int ConnectionIdleLifetimeSeconds = 60;

    public static string Apply(string connectionString, string applicationName, int maxPoolSize)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        // The Npgsql builder answers ContainsKey for every keyword it knows; its
        // ConnectionString lists only the keys that were set, under their canonical names.
        var set = new DbConnectionStringBuilder { ConnectionString = builder.ConnectionString };

        // Npgsql refuses a maximum below the minimum and an idle lifetime shorter than
        // the pruning interval, so a default never undercuts what the operator set.
        if (!set.ContainsKey("Maximum Pool Size"))
            builder.MaxPoolSize = Math.Max(maxPoolSize, builder.MinPoolSize);
        if (!set.ContainsKey("Connection Idle Lifetime"))
            builder.ConnectionIdleLifetime = Math.Max(ConnectionIdleLifetimeSeconds, builder.ConnectionPruningInterval);
        if (!set.ContainsKey("Application Name"))
            builder.ApplicationName = applicationName;

        return builder.ConnectionString;
    }
}
