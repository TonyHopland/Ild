using System.Buffers.Binary;
using System.Text.Json;
using ILD.Core.Services.Backup;
using ILD.Core.Services.Interfaces;
using ILD.Core.Services.Remote;
using ILD.Data.Backup;
using ILD.Data.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ILD.Tests;

/// <summary>
/// The core flow of <see cref="BackupService"/> against a stand-in archive (the real one needs
/// PostgreSQL): which backups a key lets back in, and what a refusal leaves behind. The secret
/// key is process-wide state, so these run alone.
/// </summary>
[Collection(ProcessGlobalStateCollection.Name)]
public sealed class BackupServiceCoreTests : IDisposable
{
    private const string Pass = "correct horse battery";

    private readonly string _dataRoot = Directory.CreateTempSubdirectory("ild-backup-core-").FullName;
    private readonly FakeArchive _archive;
    private readonly MaintenanceState _maintenance = new(TimeSpan.Zero);
    private readonly BackupDirectory _directory;

    public BackupServiceCoreTests()
    {
        _archive = new FakeArchive();
        _directory = new BackupDirectory(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["App:DataPath"] = _dataRoot }).Build());
    }

    public void Dispose()
    {
        SecretProtector.Configure(null);
        Directory.Delete(_dataRoot, recursive: true);
    }

    /// <summary>Stands in for the PostgreSQL archive.</summary>
    private sealed class FakeArchive : IDatabaseArchive
    {
        public int Imports;

        public Task<ArchiveInfo> DescribeAsync(CancellationToken cancellationToken)
            => Task.FromResult(new ArchiveInfo("1.0.0", ["M1"], 100L));

        public Task ExportAsync(Stream output, string? keyCheck, CancellationToken cancellationToken)
        {
            var manifest = JsonSerializer.SerializeToUtf8Bytes(new
            {
                format = 1, database = "core", appVersion = "1.0.0", migrations = new[] { "M1" },
                keyCheck, createdAt = DateTime.UtcNow, tables = Array.Empty<object>(),
            }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            var head = new byte[4];
            BinaryPrimitives.WriteInt32BigEndian(head, manifest.Length);
            output.Write(head);
            output.Write(manifest);
            return Task.CompletedTask;
        }

        public Task ImportAsync(Stream input, CancellationToken cancellationToken)
        {
            Imports++;
            return Task.CompletedTask;
        }
    }

    private BackupService Service()
    {
        var engine = new Mock<ILoopEngine>();
        engine.Setup(e => e.GetActiveRunIdsAsync()).ReturnsAsync(Array.Empty<Guid>());
        var client = Mock.Of<IWorkItemServerBackupClient>();
        var resolver = Mock.Of<IWorkItemServerOptionsResolver>();
        var services = new ServiceCollection();
        services.AddSingleton(client);
        services.AddSingleton(resolver);
        services.AddScoped<IDatabaseArchive>(_ => _archive);
        var catalog = new DatabaseCatalog(
            services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(), NullLogger<DatabaseCatalog>.Instance);
        return new BackupService(
            client, resolver, _directory, _maintenance,
            new BackupOperationLock(), catalog, engine.Object, Mock.Of<IWorktreePreviewService>(), Mock.Of<IChatTurnRunner>(),
            NullLogger<BackupService>.Instance, _archive);
    }

    private async Task<byte[]> BackupAsync(string? key)
    {
        SecretProtector.Configure(key);
        var file = new MemoryStream();
        await Service().CreateAsync("core", Pass, "admin", _ => file, TestContext.Current.CancellationToken);
        return file.ToArray();
    }

    private async Task RestoreAsync(byte[] backup, string? key)
    {
        SecretProtector.Configure(key);
        await Service().RestoreAsync("core", new MemoryStream(backup), Pass, BackupService.RestoreConfirmation,
            "admin", TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData("key-one", "key-one")]
    [InlineData(null, "key-one")]
    [InlineData(null, null)]
    public async Task A_backup_restores_with_the_key_it_was_made_with_and_one_made_without_a_key_restores_anywhere(string? madeWith, string? restoredWith)
    {
        var backup = await BackupAsync(madeWith);

        await RestoreAsync(backup, restoredWith);

        Assert.Equal(1, _archive.Imports);
        Assert.NotNull(_directory.PreRestoreInfo("core"));
        // ILD restarts after a core restore, so the instance never reopens for business.
        Assert.True(_maintenance.IsActive);
    }

    [Theory]
    [InlineData("key-one", "key-two")]
    [InlineData("key-one", null)]
    public async Task A_backup_made_with_another_key_is_refused_naming_the_key_before_anything_changes(string madeWith, string? restoredWith)
    {
        var backup = await BackupAsync(madeWith);

        var refused = await Assert.ThrowsAsync<BackupException>(() => RestoreAsync(backup, restoredWith));

        Assert.Equal(BackupFailure.Refused, refused.Failure);
        Assert.Contains("ILD_SECRET_KEY", refused.Message);
        Assert.Equal(0, _archive.Imports);
        Assert.Null(_directory.PreRestoreInfo("core"));
        Assert.False(_maintenance.IsActive);
    }

    [Fact]
    public async Task A_restore_waits_for_work_already_under_way_and_is_refused_when_it_does_not_finish()
    {
        var backup = await BackupAsync(null);
        var inFlight = _maintenance.TryBeginWork();

        // One lease is the request that carries the restore; the second is a pass or another request.
        var extra = _maintenance.TryBeginWork();
        var refused = await Assert.ThrowsAsync<BackupException>(() => RestoreAsync(backup, null));
        Assert.Equal(BackupFailure.Conflict, refused.Failure);
        Assert.Equal(0, _archive.Imports);
        Assert.False(_maintenance.IsActive);

        extra!.Dispose();
        await RestoreAsync(backup, null);
        Assert.Equal(1, _archive.Imports);
        inFlight!.Dispose();
    }

    [Fact]
    public void Work_does_not_start_during_a_restore_and_is_counted_before_one_can_begin()
    {
        var work = _maintenance.TryBeginWork();
        Assert.NotNull(work);
        using (_maintenance.Enter())
            Assert.Null(_maintenance.TryBeginWork());
        work.Dispose();

        using var again = _maintenance.TryBeginWork();
        Assert.NotNull(again);
    }
}
