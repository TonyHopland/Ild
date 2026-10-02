using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ILD.Tests;

/// <summary>Opens private in-memory SQLite databases that already hold <typeparamref name="TContext"/>'s schema.</summary>
public static class SqliteSchemaTemplate<TContext> where TContext : DbContext
{
    private static readonly Lock Gate = new();
    private static SqliteConnection? _template;

    public static SqliteConnection OpenCopy(Func<DbContextOptions<TContext>, TContext> createContext)
    {
        var copy = new SqliteConnection("Filename=:memory:");
        copy.Open();
        // A SqliteConnection cannot be used from several threads at once:
        // https://learn.microsoft.com/dotnet/standard/data/sqlite/database-errors#locking-retries-and-timeouts
        lock (Gate)
        {
            _template ??= Build(createContext);
            _template.BackupDatabase(copy);
        }
        return copy;
    }

    private static SqliteConnection Build(Func<DbContextOptions<TContext>, TContext> createContext)
    {
        var template = new SqliteConnection("Filename=:memory:");
        template.Open();
        using var context = createContext(new DbContextOptionsBuilder<TContext>().UseSqlite(template).Options);
        context.Database.EnsureCreated();
        return template;
    }
}
