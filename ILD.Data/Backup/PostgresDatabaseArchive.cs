using System.Data;
using System.Reflection;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

// Also compiled into ILD.WorkItemServer, which does not reference ILD.Data.
namespace ILD.Data.Backup;

/// <summary>
/// A PostgreSQL database as an <see cref="IDatabaseArchive"/>: every table of the public schema
/// the connected role owns, copied as PostgreSQL's own text COPY format.
///
/// <para>COPY works on the stored text and bypasses EF Core, so a value protected with
/// <c>ILD_SECRET_KEY</c> leaves and returns exactly as stored, never decrypted. The schema is not
/// archived: an import drops the tables and has EF's migrator rebuild them to the migration level
/// of the archive, which is also what lets an archive from an older build, or from an older
/// PostgreSQL major version, restore into a newer one.</para>
/// </summary>
public sealed class PostgresDatabaseArchive<TContext>(TContext context, string database) : IDatabaseArchive
    where TContext : DbContext
{
    private const string MigrationsTable = "__EFMigrationsHistory";

    public async Task<ArchiveInfo> DescribeAsync(CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = new NpgsqlCommand("SELECT pg_database_size(current_database())", connection);
        var bytes = (long?)await command.ExecuteScalarAsync(ct);
        return new ArchiveInfo(Version(), context.Database.GetMigrations().ToList(), bytes);
    }

    public async Task ExportAsync(Stream output, string? keyCheck, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        await ExecuteAsync(connection, "SET TRANSACTION READ ONLY", ct);

        var schema = await ReadSchemaAsync(connection, ct);
        var tables = (await InDependencyOrderAsync(connection, schema.Keys, ct))
            .Select(name => new ArchiveTable(name, schema[name])).ToList();
        var applied = await ReadAppliedMigrationsAsync(connection, ct);

        await ArchiveManifest.WriteAsync(
            output, new ArchiveManifest(ArchiveManifest.CurrentFormat, database, Version(), applied, keyCheck, DateTime.UtcNow, tables), ct);

        foreach (var table in tables)
        {
            var framed = new FramedWriteStream(output);
            await using (var writer = new StreamWriter(framed, new UTF8Encoding(false), leaveOpen: true))
            {
                using var reader = await connection.BeginTextExportAsync(CopySql(table, "TO STDOUT"), ct);
                var buffer = new char[16 * 1024];
                int read;
                while ((read = await reader.ReadAsync(buffer, ct)) > 0)
                    await writer.WriteAsync(buffer.AsMemory(0, read), ct);
            }
            await framed.CompleteAsync(ct);
        }
        await Framing.WriteEndMarkerAsync(output, ct);
        await transaction.CommitAsync(ct);
    }

    public async Task ImportAsync(Stream input, CancellationToken ct)
    {
        var manifest = await ArchiveManifest.ReadAsync(input, ct);
        manifest.RequireRestorableInto(database, await DescribeAsync(ct));
        if (manifest.Migrations.Count == 0)
            throw new BackupRefusedException("This backup lists no migrations, so there is no schema to rebuild.");

        await using var connection = await OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        // The DROP and TRUNCATE below wait for every other session using these tables; a session that
        // never lets go would otherwise hold the restore, and with it the instance, indefinitely.
        await ExecuteAsync(connection, "SET LOCAL lock_timeout = '60s'", ct);

        var owned = (await ReadSchemaAsync(connection, ct)).Keys;
        if (owned.Count > 0)
            await ExecuteAsync(connection, $"DROP TABLE IF EXISTS {string.Join(", ", owned.Select(Quote))} CASCADE", ct);

        var known = context.Database.GetMigrations().ToList();
        var last = known.Last(migration => manifest.Migrations.Contains(migration));
        await ExecuteAsync(connection, context.GetService<IMigrator>()
            .GenerateScript(null, last, MigrationsSqlGenerationOptions.NoTransactions), ct);

        var schema = await ReadSchemaAsync(connection, ct);
        foreach (var table in manifest.Tables)
            if (!schema.TryGetValue(table.Name, out var columns) || table.Columns.Except(columns).Any())
                throw new BackupRefusedException(
                    $"The backup's table '{table.Name}' does not fit the schema of its migrations. It was not made by ILD, or it was altered.");

        await ExecuteAsync(connection, $"TRUNCATE TABLE {string.Join(", ", schema.Keys.Select(Quote))} CASCADE", ct);

        foreach (var table in manifest.Tables)
        {
            using var reader = new StreamReader(new FramedReadStream(input), new UTF8Encoding(false), false, leaveOpen: true);
            await using var writer = await connection.BeginTextImportAsync(CopySql(table, "FROM STDIN"), ct);
            try
            {
                var buffer = new char[16 * 1024];
                int read;
                while ((read = await reader.ReadAsync(buffer, ct)) > 0)
                    await writer.WriteAsync(buffer.AsMemory(0, read), ct);
            }
            catch
            {
                // Disposing a writer completes the COPY; a damaged archive must surface as itself and not
                // as the server's complaint about the half of a row it was just handed.
                writer.Cancel();
                throw;
            }
        }
        await Framing.RequireEndMarkerAsync(input, ct);

        await AdvanceSequencesAsync(connection, ct);
        await transaction.CommitAsync(ct);
    }

    /// <summary>Every owned table with its columns that hold data (generated ones are computed again).</summary>
    private static async Task<Dictionary<string, List<string>>> ReadSchemaAsync(NpgsqlConnection connection, CancellationToken ct)
    {
        var schema = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        await using var command = new NpgsqlCommand(
            """
            SELECT c.relname, a.attname
            FROM pg_class c
            JOIN pg_namespace n ON n.oid = c.relnamespace
            JOIN pg_attribute a ON a.attrelid = c.oid AND a.attnum > 0 AND NOT a.attisdropped AND a.attgenerated = ''
            WHERE n.nspname = 'public' AND c.relkind = 'r' AND pg_get_userbyid(c.relowner) = current_user
            ORDER BY c.relname, a.attnum
            """, connection);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var table = reader.GetString(0);
            if (!schema.TryGetValue(table, out var columns)) schema[table] = columns = [];
            columns.Add(reader.GetString(1));
        }
        return schema;
    }

    /// <summary>
    /// Parents before the tables that refer to them: COPY checks foreign keys at the end of each
    /// statement, so a child loaded first would be refused. Tables in a cycle come last.
    /// </summary>
    private static async Task<List<string>> InDependencyOrderAsync(
        NpgsqlConnection connection, IEnumerable<string> names, CancellationToken ct)
    {
        var parents = names.ToDictionary(name => name, _ => new HashSet<string>(StringComparer.Ordinal), StringComparer.Ordinal);
        await using (var command = new NpgsqlCommand(
            """
            SELECT child.relname, parent.relname
            FROM pg_constraint k
            JOIN pg_class child ON child.oid = k.conrelid
            JOIN pg_class parent ON parent.oid = k.confrelid
            JOIN pg_namespace n ON n.oid = child.relnamespace AND n.oid = parent.relnamespace
            WHERE k.contype = 'f' AND n.nspname = 'public'
            """, connection))
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                var (child, parent) = (reader.GetString(0), reader.GetString(1));
                if (child != parent && parents.TryGetValue(child, out var set) && parents.ContainsKey(parent)) set.Add(parent);
            }
        }

        var ordered = new List<string>();
        var remaining = parents.Keys.Order(StringComparer.Ordinal).ToList();
        while (remaining.Count > 0)
        {
            var ready = remaining.Where(name => parents[name].All(ordered.Contains)).ToList();
            if (ready.Count == 0)
            {
                ordered.AddRange(remaining);
                break;
            }
            ordered.AddRange(ready);
            remaining.RemoveAll(ready.Contains);
        }
        return ordered;
    }

    private static async Task<List<string>> ReadAppliedMigrationsAsync(NpgsqlConnection connection, CancellationToken ct)
    {
        var applied = new List<string>();
        await using var command = new NpgsqlCommand($"SELECT \"MigrationId\" FROM public.{Quote(MigrationsTable)} ORDER BY \"MigrationId\"", connection);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) applied.Add(reader.GetString(0));
        return applied;
    }

    /// <summary>Sets every serial and identity sequence to the highest value its column now holds.</summary>
    private static async Task AdvanceSequencesAsync(NpgsqlConnection connection, CancellationToken ct)
    {
        var sequences = new List<(string Table, string Column, string Sequence)>();
        await using (var command = new NpgsqlCommand(
            """
            SELECT c.relname, a.attname, pg_get_serial_sequence(format('%I.%I', n.nspname, c.relname), a.attname)
            FROM pg_class c
            JOIN pg_namespace n ON n.oid = c.relnamespace
            JOIN pg_attribute a ON a.attrelid = c.oid AND a.attnum > 0 AND NOT a.attisdropped
            WHERE n.nspname = 'public' AND c.relkind = 'r' AND pg_get_userbyid(c.relowner) = current_user
              AND pg_get_serial_sequence(format('%I.%I', n.nspname, c.relname), a.attname) IS NOT NULL
            """, connection))
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
                sequences.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        }

        foreach (var (table, column, sequence) in sequences)
            await ExecuteAsync(connection,
                $"SELECT setval('{sequence.Replace("'", "''")}', COALESCE(MAX({Quote(column)}), 1), MAX({Quote(column)}) IS NOT NULL) FROM {Quote(table)}", ct);
    }

    private static string CopySql(ArchiveTable table, string direction)
        => $"COPY {Quote(table.Name)} ({string.Join(", ", table.Columns.Select(Quote))}) {direction}";

    private static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"") + "\"";

    private static string Version()
        => typeof(PostgresDatabaseArchive<>).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? "unknown";

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// A connection of its own: no command timeout, since copying a large table is one command, and
    /// no pooling, so nothing about this session is handed to the next request.
    /// </summary>
    private async Task<NpgsqlConnection> OpenAsync(CancellationToken ct)
    {
        var connectionString = new NpgsqlConnectionStringBuilder(context.Database.GetConnectionString())
        {
            CommandTimeout = 0,
            Pooling = false,
        };
        var connection = new NpgsqlConnection(connectionString.ConnectionString);
        try
        {
            await connection.OpenAsync(ct);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }
}
