using System.Collections.Concurrent;
using System.Data.Common;
using ILD.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace ILD.Tests.Integration;

/// <summary>
/// Records the SQL of every query the API runs. SQLite happens to return rows tied on
/// the sort key in key order, which Postgres does not promise, so an ordering bug can
/// only be seen in the query itself here. Postgres returns rows equal on every
/// ORDER BY expression "in an implementation-dependent order":
/// https://www.postgresql.org/docs/current/sql-select.html#SQL-ORDERBY
/// </summary>
internal sealed class SqlCapture : DbCommandInterceptor
{
    private readonly ConcurrentQueue<string> _commands = new();

    public IEnumerable<string> Commands => _commands;

    public void Register(IServiceCollection services)
        => services.ConfigureDbContext<AppDbContext>(options => options.AddInterceptors(this));

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        _commands.Enqueue(command.CommandText);
        return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
    }
}
