using System.Data;
using ILD.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace ILD.Data.Migrations;

/// <summary>
/// One-time, idempotent data migration that carries the sign-ins held in the
/// retired <c>Users.SessionToken</c> column into <see cref="UserSession"/> rows,
/// so the deploy that makes sessions plural does not sign anybody out.
///
/// It comes in two halves because the schema migration both destroys the source
/// (dropping the column) and creates the destination (the table), leaving no
/// moment at which a single pass could see both: <see cref="CaptureAsync"/> runs
/// <em>before</em> <c>Database.Migrate()</c> and reads the legacy column with raw
/// SQL, <see cref="ApplyAsync"/> runs <em>after</em> and writes the hashed rows.
/// Losing the captured list between the two (a crash mid-migration) costs one
/// re-login, never data.
///
/// Both halves are no-ops on any database that never had the column — a fresh
/// install, or one already migrated — so this can be left wired up until every
/// deployment has started once on this version, then deleted.
/// </summary>
public static class UserSessionCarryOverMigrator
{
    /// <summary>A sign-in read out of the legacy column, still in plaintext.</summary>
    public sealed record CarriedSession(Guid UserId, string Token);

    /// <summary>
    /// Reads the legacy plaintext tokens. Returns empty when the column, the
    /// table, or the database itself is not there — every one of which is the
    /// normal case on all but the single boot that performs this migration.
    /// </summary>
    public static async Task<IReadOnlyList<CarriedSession>> CaptureAsync(
        AppDbContext db,
        CancellationToken ct = default)
    {
        var carried = new List<CarriedSession>();
        try
        {
            var connection = db.Database.GetDbConnection();
            if (connection.State != ConnectionState.Open)
                await db.Database.OpenConnectionAsync(ct);

            await using var command = connection.CreateCommand();
            command.CommandText =
                """SELECT "Id", "SessionToken" FROM "Users" WHERE "SessionToken" IS NOT NULL AND "SessionToken" <> ''""";

            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                carried.Add(new CarriedSession(reader.GetGuid(0), reader.GetString(1)));
        }
        catch
        {
            // No legacy column to read: nothing to carry across.
            return Array.Empty<CarriedSession>();
        }

        return carried;
    }

    /// <summary>
    /// Writes each captured token as a hashed <see cref="UserSession"/>.
    /// Returns the number of sessions created.
    /// </summary>
    /// <param name="expiresAt">
    /// Absolute expiry stamped on the carried sessions. They had none before, so
    /// this is where the new cap starts applying to them; null leaves them
    /// bounded only by idle expiry.
    /// </param>
    public static async Task<int> ApplyAsync(
        AppDbContext db,
        IReadOnlyList<CarriedSession> carried,
        DateTime? expiresAt,
        CancellationToken ct = default)
    {
        if (carried.Count == 0) return 0;

        var now = DateTime.UtcNow;
        var hashes = carried.Select(c => UserSession.HashToken(c.Token)).ToList();
        var alreadyCarried = (await db.UserSessions
                .Where(s => hashes.Contains(s.TokenHash))
                .Select(s => s.TokenHash)
                .ToListAsync(ct))
            .ToHashSet(StringComparer.Ordinal);

        var userIds = carried.Select(c => c.UserId).Distinct().ToList();
        var liveUsers = (await db.Users
                .Where(u => userIds.Contains(u.Id))
                .Select(u => u.Id)
                .ToListAsync(ct))
            .ToHashSet();

        var created = 0;
        foreach (var (session, hash) in carried.Zip(hashes))
        {
            if (!alreadyCarried.Add(hash)) continue;
            if (!liveUsers.Contains(session.UserId)) continue;

            db.UserSessions.Add(new UserSession
            {
                Id = Guid.NewGuid(),
                UserId = session.UserId,
                TokenHash = hash,
                // The old column recorded neither when the token was minted nor
                // when it was last used, so the carry-across is its birthday.
                CreatedAt = now,
                LastSeenAt = now,
                ExpiresAt = expiresAt,
            });
            created++;
        }

        if (created > 0)
            await db.SaveChangesAsync(ct);
        return created;
    }
}
