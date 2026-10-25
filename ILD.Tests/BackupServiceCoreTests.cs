using System.Buffers.Binary;
using System.Reflection;
using System.Text.Json;
using ILD.Core.Services.Backup;
using ILD.Core.Services.Interfaces;
using ILD.Core.Services.Remote;
using ILD.Data.Security;
using Microsoft.Extensions.Configuration;
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
    private readonly object _archiveObject;
    private readonly MaintenanceState _maintenance = new();
    private readonly BackupDirectory _directory;

    public BackupServiceCoreTests()
    {
        var archiveType = typeof(BackupService).GetConstructors().Single().GetParameters().Last().ParameterType;
        _archiveObject = typeof(DispatchProxy).GetMethods()
            .Single(m => m.Name == nameof(DispatchProxy.Create) && m.GetGenericArguments().Length == 2)
            .MakeGenericMethod(archiveType, typeof(FakeArchive)).Invoke(null, null)!;
        _archive = (FakeArchive)_archiveObject;
        _directory = new BackupDirectory(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["App:DataPath"] = _dataRoot }).Build());
    }

    public void Dispose()
    {
        SecretProtector.Configure(null);
        Directory.Delete(_dataRoot, recursive: true);
    }

    /// <summary>
    /// Stands in for the PostgreSQL archive. Built by reflection: the archive types are also compiled into the
    /// WorkItem Server's assembly, which this project references too, so their names are ambiguous here.
    /// </summary>
    public class FakeArchive : DispatchProxy
    {
        public int Imports;

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            switch (method!.Name)
            {
                case "DescribeAsync":
                    var info = Activator.CreateInstance(method.ReturnType.GetGenericArguments()[0], "1.0.0", new[] { "M1" }, 100L);
                    return typeof(Task).GetMethod(nameof(Task.FromResult))!
                        .MakeGenericMethod(method.ReturnType.GetGenericArguments()[0]).Invoke(null, [info]);
                case "ExportAsync":
                    var manifest = JsonSerializer.SerializeToUtf8Bytes(new
                    {
                        format = 1, database = "core", appVersion = "1.0.0", migrations = new[] { "M1" },
                        keyCheck = (string?)args![1], createdAt = DateTime.UtcNow, tables = Array.Empty<object>(),
                    }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
                    var head = new byte[4];
                    BinaryPrimitives.WriteInt32BigEndian(head, manifest.Length);
                    var output = (Stream)args[0]!;
                    output.Write(head);
                    output.Write(manifest);
                    return Task.CompletedTask;
                case "ImportAsync":
                    Imports++;
                    return Task.CompletedTask;
                default:
                    throw new NotSupportedException(method.Name);
            }
        }
    }

    private BackupService Service()
    {
        var engine = new Mock<ILoopEngine>();
        engine.Setup(e => e.GetActiveRunIdsAsync()).ReturnsAsync(Array.Empty<Guid>());
        return (BackupService)typeof(BackupService).GetConstructors().Single().Invoke([
            Mock.Of<IWorkItemServerBackupClient>(), Mock.Of<IWorkItemServerOptionsResolver>(), _directory, _maintenance,
            new BackupOperationLock(), engine.Object, Mock.Of<IWorktreePreviewService>(), Mock.Of<IChatTurnRunner>(),
            NullLogger<BackupService>.Instance, _archiveObject]);
    }

    private async Task<byte[]> BackupAsync(string? key)
    {
        SecretProtector.Configure(key);
        var file = new MemoryStream();
        await Service().CreateAsync(BackupService.Core, Pass, "admin", _ => file, TestContext.Current.CancellationToken);
        return file.ToArray();
    }

    private async Task RestoreAsync(byte[] backup, string? key)
    {
        SecretProtector.Configure(key);
        await Service().RestoreAsync(BackupService.Core, new MemoryStream(backup), Pass, BackupService.RestoreConfirmation,
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
        Assert.NotNull(_directory.PreRestoreInfo(BackupService.Core));
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
        Assert.Null(_directory.PreRestoreInfo(BackupService.Core));
        Assert.False(_maintenance.IsActive);
    }
}
