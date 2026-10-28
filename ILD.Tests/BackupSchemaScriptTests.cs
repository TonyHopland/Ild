using ILD.Data.Backup;
using ILD.Data.Entities;
using ILD.WorkItemServer;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace ILD.Tests;

/// <summary>
/// A restore rebuilds the schema from the migrations' generated SQL and loads the rows into it. A
/// migration whose own SQL (<c>migrationBuilder.Sql</c>) is left unterminated, or has a blank line in it,
/// would make that script unrunnable, and every restore of any backup fail. The SQL is run on PostgreSQL
/// in production only, so what can be pinned here is the shape.
/// </summary>
public sealed class BackupSchemaScriptTests
{
    private static string Script<TContext>(TContext context) where TContext : DbContext
    {
        var target = context.Database.GetMigrations().Last();
        return PostgresDatabaseArchive<TContext>.BuildSchemaScript(context, target);
    }

    private static void AssertEveryStatementTerminated(string script)
    {
        var unterminated = script.Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
            .Select(block => block.Trim())
            .Where(block => !block.EndsWith(';'))
            .Select(block => block[..Math.Min(block.Length, 120)])
            .ToList();
        Assert.True(unterminated.Count == 0,
            "End each migrationBuilder.Sql statement with a semicolon and keep blank lines out of it, or a restore "
            + "cannot rebuild the schema. Unterminated:\n" + string.Join("\n---\n", unterminated));
    }

    [Fact]
    public void The_core_schema_script_has_every_statement_terminated()
    {
        using var context = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=localhost;Database=x;Username=u", n => n.MigrationsHistoryTable("__EFMigrationsHistory", "public")).Options);

        AssertEveryStatementTerminated(Script(context));
    }

    [Fact]
    public void The_work_item_schema_script_has_every_statement_terminated()
    {
        using var context = new WorkItemServerDbContext(new DbContextOptionsBuilder<WorkItemServerDbContext>()
            .UseNpgsql("Host=localhost;Database=x;Username=u", n => n.MigrationsHistoryTable("__EFMigrationsHistory", "public")).Options);

        AssertEveryStatementTerminated(Script(context));
    }
}
