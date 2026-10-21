using ILD.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ILD.Tests.Integration;

/// <summary>
/// A test database in a file, for a host whose requests or background work use
/// the database at the same time. Every DbContext gets its own connection to the
/// file, as Postgres gives each its own session: one SqliteConnection cannot be
/// shared across threads, which is what <see cref="ApiFactory"/>'s in-memory
/// database does.
/// </summary>
internal static class SqliteDatabaseFile
{
    public static string ConnectionString(string file) => $"Data Source={file}";

    /// <summary>A copy of the migrated schema in <c>ild.db</c> under <paramref name="directory"/>.</summary>
    public static string Create(string directory)
    {
        Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, "ild.db");
        using var schema = SqliteSchemaTemplate<AppDbContext>.OpenCopy(options => new AppDbContext(options));
        using var target = new SqliteConnection(ConnectionString(file));
        target.Open();
        schema.BackupDatabase(target);
        // Readers never wait on a writer, and a writer waits its turn rather than failing.
        using var wal = target.CreateCommand();
        wal.CommandText = "PRAGMA journal_mode=WAL;";
        wal.ExecuteNonQuery();
        return file;
    }

    /// <summary>Points the host's AppDbContext at <paramref name="file"/> in place of ApiFactory's shared connection.</summary>
    public static void Use(IServiceCollection services, string file)
    {
        var connectionString = ConnectionString(file);
        services.RemoveAll<DbContextOptions<AppDbContext>>();
        services.RemoveAll<IDbContextOptionsConfiguration<AppDbContext>>();
        services.AddDbContext<AppDbContext>(options => options.UseSqlite(connectionString,
            sql => sql.MigrationsAssembly(typeof(AppDbContext).Assembly)).WithPostgresSemantics());
    }
}
