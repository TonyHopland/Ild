using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ILD.Tests;

/// <summary>
/// Opens private in-memory SQLite databases that already hold
/// <typeparamref name="TContext"/>'s schema. The schema is built with
/// <c>EnsureCreated</c> once per test process and each new database is a page
/// copy of it, which is far cheaper than running <c>EnsureCreated</c> per test.
/// </summary>
public static class SqliteSchemaTemplate<TContext> where TContext : DbContext
{
    private static readonly Lock Gate = new();
    private static SqliteConnection? _template;

    public static SqliteConnection OpenCopy(Func<DbContextOptions<TContext>, TContext> createContext)
    {
        var copy = new SqliteConnection("Filename=:memory:");
        copy.Open();
        // A SqliteConnection is not thread-safe, so parallel tests take turns reading the template.
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
