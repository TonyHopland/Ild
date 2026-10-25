using System.Security.Cryptography;
using System.Text.Json;
using ILD.Core.Services.Interfaces;
using ILD.Core.Services.Remote;
using ILD.Data.Backup;
using ILD.Data.Security;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ILD.Core.Services.Backup;

/// <summary>Why a backup or restore did not happen; the API answers each kind with its own status.</summary>
public enum BackupFailure
{
    /// <summary>The request itself is wrong: a short passphrase, no confirmation, no file.</summary>
    Invalid,

    /// <summary>This database cannot be backed up here.</summary>
    Unavailable,

    /// <summary>The file is not a backup this instance may restore.</summary>
    Refused,

    /// <summary>Something else is going on: another backup or restore, or a run, preview or chat.</summary>
    Conflict,

    /// <summary>It was tried and went wrong; the data was not changed.</summary>
    Failed,
}

public sealed class BackupException(BackupFailure failure, string message, Exception? inner = null) : Exception(message, inner)
{
    public BackupFailure Failure => failure;
}

public sealed record DatabaseState(bool Available, string? Reason, ArchiveInfo? Info);

public sealed record PreRestoreBackup(DateTime CreatedAt, long Bytes);

public sealed record DatabaseBackupStatus(bool Available, string? Reason, PreRestoreBackup? PreRestoreBackup);

public sealed record BackupStatus(
    IReadOnlyDictionary<string, DatabaseBackupStatus> Databases, bool SecretKeyConfigured, int MinPassphraseLength);

/// <summary>Allows one backup or restore at a time.</summary>
public sealed class BackupOperationLock
{
    private readonly SemaphoreSlim _lock = new(1, 1);

    public IDisposable? TryEnter() => _lock.Wait(0) ? new Release(_lock) : null;

    private sealed class Release(SemaphoreSlim gate) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0) gate.Release();
        }
    }
}

/// <summary>
/// Backup and restore of ILD's two databases, each its own flow with its own file and passphrase
/// (ADR-0029): "core", ILD's own data, through the archive in this process; "workitems", the
/// built-in WorkItem Server's, through that server's backup endpoints.
///
/// <para>What a restore guarantees: nothing is changed until the file has been read end to end and
/// every check has passed, until nothing is running that the data belongs to, and until the
/// current data is safely stored. The import itself is one transaction.</para>
/// </summary>
public sealed class BackupService(
    IWorkItemServerBackupClient workItemServer,
    IWorkItemServerOptionsResolver serverOptions,
    BackupDirectory directory,
    MaintenanceState maintenance,
    BackupOperationLock operations,
    ILoopEngine engine,
    IWorktreePreviewService previews,
    IChatTurnRunner chats,
    ILogger<BackupService> log,
    IDatabaseArchive? core = null)
{
    public const string Core = "core";
    public const string WorkItems = "workitems";
    public static readonly IReadOnlyList<string> Databases = [Core, WorkItems];

    /// <summary>What the key check in a core backup decrypts to.</summary>
    public const string KeyCheckValue = "ild-backup-key-check-v1";

    public const string RestoreConfirmation = "RESTORE";

    public async Task<IReadOnlyDictionary<string, DatabaseState>> DescribeDatabasesAsync(CancellationToken ct)
        => new Dictionary<string, DatabaseState>
        {
            [Core] = await DescribeCoreAsync(ct),
            [WorkItems] = await DescribeWorkItemsAsync(ct),
        };

    public async Task<BackupStatus> GetStatusAsync(CancellationToken ct)
        => new(
            (await DescribeDatabasesAsync(ct)).ToDictionary(
                database => database.Key,
                database => new DatabaseBackupStatus(
                    database.Value.Available,
                    database.Value.Reason,
                    directory.PreRestoreInfo(database.Key) is var (createdAt, bytes) ? new PreRestoreBackup(createdAt, bytes) : null)),
            SecretProtector.IsEnabled,
            PassphrasePolicy.MinLength);

    public static string FileName(string database)
        => $"ild-{database}-{DateTime.UtcNow:yyyyMMdd-HHmmss}.ildbackup";

    /// <summary>
    /// Writes the encrypted backup to the stream <paramref name="openOutput"/> returns, which it asks for
    /// by file name only once it has what to write: a failure before that is still an error the caller can report.
    /// </summary>
    public Task CreateAsync(string database, string? passphrase, string username, Func<string, Stream> openOutput, CancellationToken ct)
        => Audited(database, "create", username, async () =>
        {
            if (passphrase is null || passphrase.Length < PassphrasePolicy.MinLength)
                throw new BackupException(BackupFailure.Invalid, $"The passphrase must be at least {PassphrasePolicy.MinLength} characters.");

            using var operation = operations.TryEnter() ?? throw Busy();
            var state = await DescribeAsync(database, ct);
            if (!state.Available) throw new BackupException(BackupFailure.Unavailable, state.Reason!);

            await WriteEncryptedAsync(database, passphrase, username, () => openOutput(FileName(database)), ct);
        });

    public Task RestoreAsync(
        string database, Stream? upload, string? passphrase, string? confirmation, string username, CancellationToken ct)
        => Audited(database, "restore", username, async () =>
        {
            if (confirmation != RestoreConfirmation)
                throw new BackupException(BackupFailure.Invalid, $"Type {RestoreConfirmation} to confirm that the restore replaces all current data.");
            if (upload is null)
                throw new BackupException(BackupFailure.Invalid, "Choose the backup file to restore.");
            if (string.IsNullOrEmpty(passphrase))
                throw new BackupException(BackupFailure.Invalid, "Enter the passphrase the backup was made with.");

            using var operation = operations.TryEnter() ?? throw Busy();
            var state = await DescribeAsync(database, ct);
            if (!state.Available) throw new BackupException(BackupFailure.Unavailable, state.Reason!);

            // Read the whole file before anything is touched: a file that fails here fails with
            // the data exactly as it was, and no restore is worth starting on one that will not finish.
            upload.Position = 0;
            await using (var verify = await OpenAsync(upload, passphrase, database, ct))
            {
                var manifest = await ArchiveManifest.ReadAsync(verify, ct);
                manifest.RequireRestorableInto(database, state.Info!);
                if (database == Core) RequireSameSecretKey(manifest.KeyCheck);
                await verify.CopyToAsync(Stream.Null, ct);
            }

            // From here the instance is its own maintenance window. A core restore keeps it for good
            // (the process restarts); every other way out of this method lets it go.
            var lease = maintenance.TryEnter() ?? throw Busy();
            var restarting = false;
            try
            {
                await RequireNothingRunningAsync();

                // The data about to be replaced, kept for download; it replaces the previous one only once it is complete.
                await using (var partial = directory.CreatePartial("pre-restore-" + database))
                {
                    await WriteEncryptedAsync(database, passphrase, username, () => partial.Stream, ct);
                    await partial.MoveToAsync(directory.PreRestorePath(database));
                }

                // From here the restore is not given up when the caller hangs up: the import is atomic, but
                // a commit that races a cancellation would leave nobody knowing which way it went.
                upload.Position = 0;
                await using var archive = await OpenAsync(upload, passphrase, database, CancellationToken.None);
                if (database == Core)
                {
                    await core!.ImportAsync(archive, CancellationToken.None);
                    restarting = true;
                }
                else
                {
                    await workItemServer.ImportAsync(await ServerOptionsAsync(ct), WorkItemActor.Human(username), archive, CancellationToken.None);
                }
            }
            finally
            {
                if (!restarting) lease.Dispose();
            }
        });

    /// <summary>The automatic backup a restore of <paramref name="database"/> took of the data it replaced, or null.</summary>
    public Stream? OpenPreRestore(string database, string username)
    {
        var file = directory.OpenPreRestore(database);
        if (file is not null) log.LogInformation("Backup {Database} {Action} {Outcome} by {Username}", database, "pre-restore download", "succeeded", username);
        return file;
    }

    /// <summary>
    /// The database's archive, encrypted into the stream <paramref name="openOutput"/> returns. For the WorkItem Server
    /// that is asked for only once the server has answered, so a failure before then leaves the caller free to report it.
    /// </summary>
    private async Task WriteEncryptedAsync(string database, string passphrase, string username, Func<Stream> openOutput, CancellationToken ct)
    {
        async Task EncryptAsync(Func<Stream, Task> write)
        {
            await using var encrypted = await BackupCipher.OpenWriteAsync(openOutput(), passphrase, database, ct);
            await write(encrypted);
            await encrypted.CompleteAsync(ct);
        }

        if (database == Core)
            await EncryptAsync(encrypted => core!.ExportAsync(encrypted, SecretProtector.IsEnabled ? SecretProtector.Protect(KeyCheckValue) : null, ct));
        else
            await workItemServer.ExportAsync(await ServerOptionsAsync(ct), WorkItemActor.Human(username),
                source => EncryptAsync(encrypted => source.CopyToAsync(encrypted, ct)), ct);
    }

    private async Task RequireNothingRunningAsync()
    {
        if ((await engine.GetActiveRunIdsAsync()).Any())
            throw new BackupException(BackupFailure.Conflict, "A loop run is executing. Let it finish, or stop it, and restore again.");
        if (previews.AnyPreviewRunning())
            throw new BackupException(BackupFailure.Conflict, "A worktree preview is running. Stop it and restore again.");
        if (chats.AnyTurnActive)
            throw new BackupException(BackupFailure.Conflict, "A chat is answering. Wait for it, or stop it, and restore again.");
    }

    private static async Task<Stream> OpenAsync(Stream upload, string passphrase, string database, CancellationToken ct)
    {
        try
        {
            return await BackupCipher.OpenReadAsync(upload, passphrase, database, ct);
        }
        catch (BackupRefusedException ex)
        {
            throw new BackupException(BackupFailure.Refused, ex.Message);
        }
    }

    private static void RequireSameSecretKey(string? keyCheck)
    {
        if (keyCheck is null || KeyMatches(keyCheck)) return;
        throw new BackupException(BackupFailure.Refused, SecretProtector.IsEnabled
            ? "This backup was made with a different ILD_SECRET_KEY than this instance has: every stored credential would become unreadable. Start ILD with the key the backup was made with."
            : "This backup was made with an ILD_SECRET_KEY and this instance has none: every stored credential would become unreadable. Start ILD with the key the backup was made with.");
    }

    private static bool KeyMatches(string keyCheck)
    {
        if (!SecretProtector.IsEnabled) return false;
        try
        {
            return SecretProtector.Unprotect(keyCheck) == KeyCheckValue;
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return false;
        }
    }

    private async Task<DatabaseState> DescribeAsync(string database, CancellationToken ct)
        => database == Core ? await DescribeCoreAsync(ct) : await DescribeWorkItemsAsync(ct);

    private async Task<DatabaseState> DescribeCoreAsync(CancellationToken ct)
    {
        if (core is null)
            return new DatabaseState(false, "Backing up ILD's own data needs PostgreSQL, and this instance uses another database.", null);
        try
        {
            return new DatabaseState(true, null, await core.DescribeAsync(ct));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "Could not read the ILD database for a backup");
            return new DatabaseState(false, $"The ILD database could not be read: {ex.Message}", null);
        }
    }

    private async Task<DatabaseState> DescribeWorkItemsAsync(CancellationToken ct)
    {
        WorkItemServerOptions options;
        try
        {
            options = await ServerOptionsAsync(ct);
        }
        catch (InvalidOperationException ex)
        {
            return new DatabaseState(false, ex.Message, null);
        }

        try
        {
            return await workItemServer.GetInfoAsync(options, ct) is { } info
                ? new DatabaseState(true, null, info)
                : new DatabaseState(false, "This tracker does not support backup.", null);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or NotSupportedException)
        {
            // NotSupported and Json: it answered, with something that is not backup info.
            return new DatabaseState(false, $"The WorkItem Server could not be asked about backup: {ex.Message}", null);
        }
    }

    private Task<WorkItemServerOptions> ServerOptionsAsync(CancellationToken ct)
        => serverOptions.ResolveForRepositoryAsync(null, ct);

    private static BackupException Busy()
        => new(BackupFailure.Conflict, "Another backup or restore is in progress. Try again when it has finished.");

    /// <summary>Runs one operation and logs how it ended with the acting username. The passphrase is never in a message.</summary>
    private async Task Audited(string database, string action, string username, Func<Task> operation)
    {
        try
        {
            await operation();
            log.LogInformation("Backup {Database} {Action} {Outcome} by {Username}", database, action, "succeeded", username);
        }
        catch (BackupException ex)
        {
            log.LogInformation("Backup {Database} {Action} {Outcome} by {Username}", database, action, "refused: " + ex.Message, username);
            throw;
        }
        catch (BackupRefusedException ex)
        {
            log.LogInformation("Backup {Database} {Action} {Outcome} by {Username}", database, action, "refused: " + ex.Message, username);
            throw new BackupException(BackupFailure.Refused, ex.Message);
        }
        catch (OperationCanceledException)
        {
            log.LogInformation("Backup {Database} {Action} {Outcome} by {Username}", database, action, "cancelled", username);
            throw;
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Backup {Database} {Action} {Outcome} by {Username}", database, action, "failed", username);
            throw new BackupException(BackupFailure.Failed, $"The {action} failed: {ex.Message}", ex);
        }
    }
}
