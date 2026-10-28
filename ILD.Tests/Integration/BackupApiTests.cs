using System.Buffers.Binary;
using System.Linq.Expressions;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reflection;
using System.Text;
using System.Text.Json;
using ILD.Core.Services.Implementations;
using ILD.Core.Services.Interfaces;
using ILD.Core.Services.Remote;
using ILD.Data;
using ILD.Data.Entities;
using ILD.Data.Enums;
using ILD.Data.Stores.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Http;
using Moq;

namespace ILD.Tests.Integration;

/// <summary>
/// Backup and restore of ILD's databases through the real API. Only the workitems
/// database can be backed up on this host (the test database is SQLite, and the
/// core backup needs PostgreSQL), so the flow is driven end to end against a
/// stand-in for the WorkItem Server's backup endpoints that speaks the wire
/// protocol the plan defines: GET /maintenance/backup (info), POST
/// /maintenance/backup/export, POST /maintenance/backup/import (raw
/// application/octet-stream body). The archive it serves begins with a 4-byte
/// big-endian length, then that many bytes of manifest JSON, then opaque bytes.
/// </summary>
// A host per test: backups read and write the host's data root and the settings it holds.
public class BackupApiTests
{
    private const string Url = "/api/v1/storage/backups";
    internal const string Pass = "correct horse battery";
    private const string PlainMarker = "PLAINTEXT-WORKITEM-BODY-5e1c7a";
    private const string CipherMarker = "enc.v1.QUJDREVGR0hJSktMTU5PUA";

    internal static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---- archive and stand-in server -------------------------------------------------

    internal static byte[] Archive(
        string database = "workitems",
        string appVersion = "1.0.0",
        string[]? migrations = null,
        string payload = PlainMarker + "|" + CipherMarker)
    {
        var manifest = JsonSerializer.SerializeToUtf8Bytes(new
        {
            format = 1,
            database,
            appVersion,
            migrations = migrations ?? new[] { "M1", "M2" },
            keyCheck = (string?)null,
            createdAt = DateTime.UtcNow,
            tables = Array.Empty<object>(),
        });
        var head = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(head, manifest.Length);
        return [.. head, .. manifest, .. Encoding.UTF8.GetBytes(payload)];
    }

    internal sealed class FakeWorkItemBackupServer : HttpMessageHandler
    {
        private int _export, _import, _info;
        private readonly object _lock = new();

        public byte[] Archive = BackupApiTests.Archive();
        public bool Offered = true;
        public bool InfoThrows;
        public string Version = "1.0.0";
        public string[] Migrations = ["M1", "M2"];
        public long? DatabaseBytes = 4242;
        public byte[]? Imported;
        public Task? HoldImport;
        public HttpStatusCode ImportStatus = HttpStatusCode.OK;
        public TaskCompletionSource ImportEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<string> Actors { get; } = [];
        public List<string?> Authorizations { get; } = [];

        public int ExportCalls => Volatile.Read(ref _export);
        public int ImportCalls => Volatile.Read(ref _import);
        public int Calls => ExportCalls + ImportCalls + Volatile.Read(ref _info);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (!path.Contains("/maintenance/backup", StringComparison.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.NotFound);

            lock (_lock)
            {
                Authorizations.Add(request.Headers.Authorization?.ToString());
                if (request.Headers.TryGetValues(WorkItemActor.HeaderName, out var actor))
                    Actors.Add(actor.Single());
            }
            if (!Offered) return new HttpResponseMessage(HttpStatusCode.NotFound);

            if (request.Method == HttpMethod.Post && path.EndsWith("/export", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _export);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(Archive)
                    {
                        Headers = { ContentType = new MediaTypeHeaderValue("application/octet-stream") },
                    },
                };
            }
            if (request.Method == HttpMethod.Post && path.EndsWith("/import", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _import);
                Imported = await request.Content!.ReadAsByteArrayAsync(ct);
                ImportEntered.TrySetResult();
                if (HoldImport is not null) await HoldImport;
                return ImportStatus == HttpStatusCode.OK
                    ? new HttpResponseMessage(HttpStatusCode.OK)
                    : new HttpResponseMessage(ImportStatus)
                    {
                        Content = JsonContent.Create(new { error = "the import failed" }),
                    };
            }

            Interlocked.Increment(ref _info);
            if (InfoThrows) throw new HttpRequestException("connection refused");
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new { version = Version, migrations = Migrations, databaseBytes = DatabaseBytes }),
            };
        }
    }

    // ---- host ------------------------------------------------------------------------

    internal sealed class Host : IAsyncDisposable
    {
        public FakeWorkItemBackupServer Wi { get; } = new();
        public Mock<ILoopEngine> Engine { get; } = new();
        public Mock<IWorktreePreviewService> Previews { get; } = new();
        public Mock<IChatTurnRunner> Chats { get; } = new();
        public Mock<IRecoveryManager> Recovery { get; } = new();
        public Mock<IRemoteWorkItemCoordinator> Coordinator { get; } = new();
        public Mock<IPrStatusPollService> PrPoll { get; } = new();
        public Mock<IWorkItemManager> WorkItems { get; } = new();
        public ApiFactory Factory { get; private set; } = null!;
        public HttpClient User { get; private set; } = null!;
        public HttpClient Agent { get; private set; } = null!;

        public static async Task<Host> StartAsync()
        {
            var host = new Host();
            host.Engine.Setup(e => e.GetActiveRunIdsAsync()).ReturnsAsync(Array.Empty<Guid>());
            host.Recovery.Setup(r => r.RecoverRunAsync(It.IsAny<Guid>())).ReturnsAsync(true);
            host.Coordinator.Setup(c => c.RunPollCycleAsync(
                    It.IsAny<WorkItemServerOptions>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new PollCycleResult());
            host.WorkItems.Setup(w => w.GetWorkItemAsync(It.IsAny<string>()))
                .ReturnsAsync((string id) => new WorkItemView { Id = id, Status = RemoteWorkItemStatus.Running });

            host.Factory = new ApiFactory(configureServices: services =>
            {
                services.ConfigureAll<HttpClientFactoryOptions>(o =>
                    o.HttpMessageHandlerBuilderActions.Add(b => b.PrimaryHandler = host.Wi));
                services.ReplaceSingleton(host.Engine.Object);
                services.ReplaceSingleton(host.Previews.Object);
                services.ReplaceSingleton(host.Chats.Object);
                services.RemoveAll<IRecoveryManager>();
                services.AddSingleton(host.Recovery.Object);
                services.RemoveAll<IRemoteWorkItemCoordinator>();
                services.AddSingleton(host.Coordinator.Object);
                services.RemoveAll<IPrStatusPollService>();
                services.AddSingleton(host.PrPoll.Object);
                services.RemoveAll<IWorkItemManager>();
                services.AddSingleton(host.WorkItems.Object);
                services.RemoveForwardedHostedService<PrStatusPoller>();
                services.RemoveHostedService<StuckRunWatchdog>();
                services.RemoveHostedService<ThrottledRunResumeSweeper>();
            });
            host.User = await host.Factory.CreateAuthenticatedClientAsync();
            host.Agent = host.Factory.CreateClient();
            var token = host.Factory.Services.GetRequiredService<ILD.Api.Configuration.AgentAuthTokenProvider>().Token;
            host.Agent.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return host;
        }

        public async ValueTask DisposeAsync() => await Factory.DisposeAsync();

        public Task<HttpResponseMessage> CreateAsync(string db, string? passphrase, HttpClient? client = null)
            => (client ?? User).PostAsJsonAsync($"{Url}/{db}", new { passphrase }, Ct);

        public async Task<byte[]> CreateBackupAsync(string db, string passphrase)
        {
            using var response = await CreateAsync(db, passphrase);
            Assert.True(response.IsSuccessStatusCode,
                $"create {db}: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync(Ct)}");
            return await response.Content.ReadAsByteArrayAsync(Ct);
        }

        public Task<HttpResponseMessage> RestoreAsync(
            string db, byte[] file, string? passphrase, string? confirmation, HttpClient? client = null)
        {
            var form = new MultipartFormDataContent();
            form.Add(new ByteArrayContent(file) { Headers = { ContentType = new MediaTypeHeaderValue("application/octet-stream") } },
                "file", "backup.ildbackup");
            if (passphrase is not null) form.Add(new StringContent(passphrase), "passphrase");
            if (confirmation is not null) form.Add(new StringContent(confirmation), "confirmation");
            return (client ?? User).PostAsync($"{Url}/{db}/restore", form, Ct);
        }

        public async Task<JsonElement> StatusAsync()
        {
            using var response = await User.GetAsync(Url, Ct);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        }

        /// <summary>Any ordinary API call; 200 whenever the API is not in maintenance.</summary>
        public Task<HttpResponseMessage> OrdinaryRequestAsync() => User.GetAsync("/api/v1/global-variables", Ct);

        public IEnumerable<string> FilesUnderDataRoot()
            => Directory.EnumerateFiles(Factory.DataRoot, "*", SearchOption.AllDirectories);

        public List<string> StoredBackups()
            => FilesUnderDataRoot().Where(f => HasMagic(f)).ToList();

        public bool AnyFileContains(string text)
        {
            var needle = Encoding.UTF8.GetBytes(text);
            return FilesUnderDataRoot().Any(f =>
            {
                try { return File.ReadAllBytes(f).AsSpan().IndexOf(needle) >= 0; }
                catch (IOException) { return false; }
            });
        }

        private static bool HasMagic(string path)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                var buffer = new byte[7];
                return stream.Read(buffer, 0, 7) == 7 && Encoding.ASCII.GetString(buffer) == "ILDBAK1";
            }
            catch (IOException) { return false; }
        }
    }

    private sealed class StaticOptionsMonitor<T>(T value) : Microsoft.Extensions.Options.IOptionsMonitor<T>
    {
        public T CurrentValue => value;
        public T Get(string? name) => value;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    private static JsonElement DbStatus(JsonElement status, string db)
    {
        var dbs = status.GetProperty("databases");
        if (dbs.ValueKind == JsonValueKind.Object) return dbs.GetProperty(db);
        return dbs.EnumerateArray().Single(e =>
            (e.TryGetProperty("key", out var k) ? k : e.TryGetProperty("database", out var d) ? d : e.GetProperty("name"))
                .GetString() == db);
    }

    private static bool HasPreRestore(JsonElement status, string db)
        => DbStatus(status, db).TryGetProperty("preRestoreBackup", out var p) && p.ValueKind == JsonValueKind.Object;

    private static async Task<string> ErrorOf(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync(Ct);
        using var doc = JsonDocument.Parse(text);
        Assert.True(doc.RootElement.TryGetProperty("error", out var error) && !string.IsNullOrWhiteSpace(error.GetString()),
            $"no reason in the {(int)response.StatusCode} body: {text}");
        return error.GetString()!;
    }

    private static async Task<string> AssertRefusedAsync(HttpResponseMessage response)
    {
        var code = (int)response.StatusCode;
        Assert.True(code is >= 400 and < 500 && code is not (401 or 403 or 404 or 409),
            $"expected a refusal of the request itself, got {code} {await response.Content.ReadAsStringAsync(Ct)}");
        return await ErrorOf(response);
    }

    // ---- criterion 1, 2 --------------------------------------------------------------

    [Fact]
    public async Task Every_endpoint_answers_401_without_auth_and_403_for_the_agent_and_produces_nothing()
    {
        await using var host = await Host.StartAsync();
        using var anonymous = host.Factory.CreateClient();
        var file = new byte[] { 1, 2, 3 };

        Task<HttpResponseMessage>[] Calls(HttpClient c) =>
        [
            c.GetAsync(Url, Ct),
            c.PostAsJsonAsync($"{Url}/core", new { passphrase = Pass }, Ct),
            c.PostAsJsonAsync($"{Url}/workitems", new { passphrase = Pass }, Ct),
            c.GetAsync($"{Url}/core/pre-restore", Ct),
            c.GetAsync($"{Url}/workitems/pre-restore", Ct),
            host.RestoreAsync("core", file, Pass, "RESTORE", c),
            host.RestoreAsync("workitems", file, Pass, "RESTORE", c),
        ];

        foreach (var (client, expected) in new[] { (anonymous, HttpStatusCode.Unauthorized), (host.Agent, HttpStatusCode.Forbidden) })
        {
            var tasks = Calls(client);
            var responses = await Task.WhenAll(tasks);
            for (var i = 0; i < responses.Length; i++)
            {
                Assert.True(responses[i].StatusCode == expected, $"call #{i} as {expected}: got {(int)responses[i].StatusCode}");
                responses[i].Dispose();
            }
        }

        Assert.Equal(0, host.Wi.Calls);
        Assert.Empty(host.StoredBackups());

        var status = await host.StatusAsync();
        Assert.Equal(12, status.GetProperty("minPassphraseLength").GetInt32());
        Assert.False(HasPreRestore(status, "workitems"));
    }

    [Theory]
    [InlineData("core", "")]
    [InlineData("core", "eleven-char")]
    [InlineData("workitems", "")]
    [InlineData("workitems", "eleven-char")]
    public async Task A_passphrase_shorter_than_12_characters_is_refused_and_nothing_is_produced(string db, string passphrase)
    {
        await using var host = await Host.StartAsync();

        using var response = await host.CreateAsync(db, passphrase);

        var error = await AssertRefusedAsync(response);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("12", error);
        Assert.Equal(0, host.Wi.ExportCalls);
    }

    [Fact]
    public async Task A_missing_passphrase_is_refused_and_an_unknown_database_is_404()
    {
        await using var host = await Host.StartAsync();

        using var missing = await host.User.PostAsJsonAsync($"{Url}/workitems", new { }, Ct);
        using var unknown = await host.CreateAsync("nonsense", Pass);
        using var unknownPreRestore = await host.User.GetAsync($"{Url}/nonsense/pre-restore", Ct);
        using var noneYet = await host.User.GetAsync($"{Url}/workitems/pre-restore", Ct);

        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unknownPreRestore.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, noneYet.StatusCode);
        Assert.Equal(0, host.Wi.ExportCalls);
    }

    // ---- criterion 3, 4, 5 (workitems side) ------------------------------------------

    [Fact]
    public async Task A_backup_is_an_opaque_file_that_carries_the_raw_archive_and_the_acting_user_to_the_server()
    {
        await using var host = await Host.StartAsync();

        using var response = await host.CreateAsync("workitems", Pass);
        var file = await response.Content.ReadAsByteArrayAsync(Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/octet-stream", response.Content.Headers.ContentType?.MediaType);
        var name = response.Content.Headers.ContentDisposition?.FileName?.Trim('"');
        Assert.Matches(@"^ild-workitems-\d{8}-\d{6}\.ildbackup$", name);
        Assert.Equal("ILDBAK1", Encoding.ASCII.GetString(file, 0, 7));
        foreach (var leaked in new[] { PlainMarker, CipherMarker, "enc.v1.", "appVersion", "migrations", "M1" })
            Assert.True(file.AsSpan().IndexOf(Encoding.UTF8.GetBytes(leaked)) < 0, $"'{leaked}' is readable in the file");

        Assert.Equal(1, host.Wi.ExportCalls);
        Assert.All(host.Wi.Authorizations, a => Assert.Equal("Bearer test-key", a));
        var actor = JsonDocument.Parse(Convert.FromBase64String(Assert.Single(host.Wi.Actors))).RootElement;
        Assert.Equal("admin", actor.GetProperty("username").GetString());
        Assert.Empty(host.StoredBackups());
    }

    // ---- criterion 8 (round trip), 11, 15 --------------------------------------------

    [Fact]
    public async Task A_restore_imports_the_stored_bytes_first_keeps_the_current_data_for_download_and_that_file_restores_too()
    {
        await using var host = await Host.StartAsync();
        var original = Archive(payload: "ORIGINAL|" + CipherMarker);
        var current = Archive(payload: "CURRENT-LIVE-STATE|enc.v1.Q1VSUkVOVA");
        host.Wi.Archive = original;
        var backup = await host.CreateBackupAsync("workitems", Pass);
        host.Wi.Archive = current;

        using var restored = await host.RestoreAsync("workitems", backup, Pass, "RESTORE");

        Assert.Equal(HttpStatusCode.OK, restored.StatusCode);
        Assert.Equal(original, host.Wi.Imported);
        Assert.Equal(1, host.Wi.ImportCalls);
        Assert.Equal("admin", JsonDocument.Parse(Convert.FromBase64String(host.Wi.Actors.Last())).RootElement
            .GetProperty("username").GetString());

        var status = await host.StatusAsync();
        Assert.True(HasPreRestore(status, "workitems"));
        var pre = DbStatus(status, "workitems").GetProperty("preRestoreBackup");
        Assert.True(pre.GetProperty("bytes").GetInt64() > 0);
        Assert.NotEqual(JsonValueKind.Null, pre.GetProperty("createdAt").ValueKind);

        // Kept where only ILD's own user can read it, and found by the storage overview.
        var stored = Assert.Single(host.StoredBackups());
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(stored));
        var directory = Path.GetDirectoryName(stored)!;
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(directory));
        Assert.DoesNotContain("wwwroot", stored, StringComparison.Ordinal);
        Assert.False(host.AnyFileContains("CURRENT-LIVE-STATE"));

        using var overview = await host.User.GetAsync("/api/v1/storage", Ct);
        var categories = (await overview.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("categories");
        var backups = categories.EnumerateArray().Single(c => c.GetProperty("key").GetString() == "backups");
        Assert.True(backups.GetProperty("bytes").GetInt64() >= new FileInfo(stored).Length);

        using var download = await host.User.GetAsync($"{Url}/workitems/pre-restore", Ct);
        var preFile = await download.Content.ReadAsByteArrayAsync(Ct);
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal(File.ReadAllBytes(stored), preFile);

        // The download is a backup of the data that was live: restoring it brings that data back.
        host.Wi.Archive = Archive(payload: "SOMETHING-ELSE");
        using var back = await host.RestoreAsync("workitems", preFile, Pass, "RESTORE");
        Assert.Equal(HttpStatusCode.OK, back.StatusCode);
        Assert.Equal(current, host.Wi.Imported);
        Assert.Single(host.StoredBackups());
    }

    [Theory]
    [InlineData("older-version", "0.9.0", new[] { "M1", "M2" })]
    [InlineData("older-migrations", "1.0.0", new[] { "M1" })]
    [InlineData("unparsable-version", "dev-build", new[] { "M1" })]
    public async Task A_backup_from_an_older_or_unversioned_build_restores(string _, string appVersion, string[] migrations)
    {
        await using var host = await Host.StartAsync();
        var archive = Archive(appVersion: appVersion, migrations: migrations);
        host.Wi.Archive = archive;
        var backup = await host.CreateBackupAsync("workitems", Pass);

        using var response = await host.RestoreAsync("workitems", backup, Pass, "RESTORE");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(archive, host.Wi.Imported);
    }

    [Fact]
    public async Task A_backup_spanning_many_cipher_chunks_restores_byte_for_byte_and_a_change_in_a_late_chunk_is_refused()
    {
        await using var host = await Host.StartAsync();
        var payload = new byte[400_000];
        new Random(7).NextBytes(payload);
        var archive = Archive(payload: "").Concat(payload).ToArray();
        host.Wi.Archive = archive;
        var backup = await host.CreateBackupAsync("workitems", Pass);
        var lateChange = (byte[])backup.Clone();
        lateChange[(int)(lateChange.Length * 0.9)] ^= 0x80;

        using var refused = await host.RestoreAsync("workitems", lateChange, Pass, "RESTORE");
        Assert.Equal(0, host.Wi.ImportCalls);
        await AssertRefusedAsync(refused);

        using var restored = await host.RestoreAsync("workitems", backup, Pass, "RESTORE");
        Assert.Equal(HttpStatusCode.OK, restored.StatusCode);
        Assert.Equal(archive, host.Wi.Imported);
    }

    // ---- criteria 3, 7, 8: refused before any data changes ---------------------------

    [Theory]
    [InlineData("wrong-passphrase")]
    [InlineData("tampered")]
    [InlineData("truncated")]
    [InlineData("not-a-backup")]
    [InlineData("empty-file")]
    [InlineData("no-confirmation")]
    [InlineData("wrong-confirmation")]
    [InlineData("other-database")]
    [InlineData("newer-version")]
    [InlineData("unknown-migration")]
    public async Task A_refused_restore_changes_nothing_and_leaves_no_files_behind(string scenario)
    {
        await using var host = await Host.StartAsync();
        host.Wi.Archive = scenario switch
        {
            "other-database" => Archive(database: "core"),
            "newer-version" => Archive(appVersion: "99.0.0"),
            "unknown-migration" => Archive(migrations: ["M1", "M9"]),
            _ => Archive(),
        };
        var backup = await host.CreateBackupAsync("workitems", Pass);
        var exportsBefore = host.Wi.ExportCalls;

        var file = backup;
        var passphrase = Pass;
        string? confirmation = "RESTORE";
        switch (scenario)
        {
            case "wrong-passphrase": passphrase = "another passphrase!"; break;
            case "tampered": file = (byte[])backup.Clone(); file[file.Length / 2] ^= 0x01; break;
            case "truncated": file = backup[..(backup.Length - 12)]; break;
            case "not-a-backup": file = Encoding.UTF8.GetBytes("this is not an ILD backup, just text"); break;
            case "empty-file": file = []; break;
            case "no-confirmation": confirmation = null; break;
            case "wrong-confirmation": confirmation = "restore"; break;
        }

        using var response = await host.RestoreAsync("workitems", file, passphrase, confirmation);

        var error = await AssertRefusedAsync(response);
        if (scenario == "wrong-passphrase") Assert.Contains("passphrase", error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, host.Wi.ImportCalls);
        Assert.Equal(exportsBefore, host.Wi.ExportCalls);
        Assert.False(HasPreRestore(await host.StatusAsync(), "workitems"));
        Assert.Empty(host.StoredBackups());
        Assert.False(host.AnyFileContains(PlainMarker));
        using var after = await host.OrdinaryRequestAsync();
        Assert.Equal(HttpStatusCode.OK, after.StatusCode);
    }

    // ---- criterion 8, 9: quiescing ----------------------------------------------------

    [Theory]
    [InlineData("run")]
    [InlineData("preview")]
    [InlineData("chat")]
    public async Task A_restore_while_a_run_a_preview_or_a_chat_turn_is_active_is_refused_with_409_and_changes_nothing(string busy)
    {
        await using var host = await Host.StartAsync();
        var backup = await host.CreateBackupAsync("workitems", Pass);
        var exportsBefore = host.Wi.ExportCalls;
        switch (busy)
        {
            case "run": host.Engine.Setup(e => e.GetActiveRunIdsAsync()).ReturnsAsync(new[] { Guid.NewGuid() }); break;
            case "preview": SetupTrue(host.Previews, "AnyPreviewRunning"); break;
            case "chat": SetupTrue(host.Chats, "AnyTurnActive"); break;
        }

        using var response = await host.RestoreAsync("workitems", backup, Pass, "RESTORE");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        await ErrorOf(response);
        Assert.Equal(0, host.Wi.ImportCalls);
        Assert.Equal(exportsBefore, host.Wi.ExportCalls);
        Assert.False(HasPreRestore(await host.StatusAsync(), "workitems"));
        Assert.Empty(host.StoredBackups());
        using var after = await host.OrdinaryRequestAsync();
        Assert.Equal(HttpStatusCode.OK, after.StatusCode);
    }

    private static void SetupTrue<T>(Mock<T> mock, string member) where T : class
    {
        var parameter = Expression.Parameter(typeof(T), "x");
        var found = typeof(T).GetMember(member).Single();
        Expression body = found is PropertyInfo property
            ? Expression.Property(parameter, property)
            : Expression.Call(parameter, (MethodInfo)found);
        mock.Setup(Expression.Lambda<Func<T, bool>>(body, parameter)).Returns(true);
    }

    [Fact]
    public async Task During_a_restore_other_API_requests_get_503_and_everything_works_again_afterwards()
    {
        await using var host = await Host.StartAsync();
        var backup = await host.CreateBackupAsync("workitems", Pass);
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.Wi.HoldImport = hold.Task;

        var restore = host.RestoreAsync("workitems", backup, Pass, "RESTORE");
        await host.Wi.ImportEntered.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        using var during = await host.OrdinaryRequestAsync();
        hold.SetResult();
        using var finished = await restore;
        using var after = await host.OrdinaryRequestAsync();

        Assert.Equal(HttpStatusCode.ServiceUnavailable, during.StatusCode);
        Assert.Equal(HttpStatusCode.OK, finished.StatusCode);
        Assert.Equal(HttpStatusCode.OK, after.StatusCode);
    }

    [Fact]
    public async Task A_failed_import_reports_the_failure_leaves_no_spool_and_ends_the_maintenance()
    {
        await using var host = await Host.StartAsync();
        var backup = await host.CreateBackupAsync("workitems", Pass);
        host.Wi.ImportStatus = HttpStatusCode.InternalServerError;

        using var response = await host.RestoreAsync("workitems", backup, Pass, "RESTORE");

        Assert.False(response.IsSuccessStatusCode);
        await ErrorOf(response);
        using var after = await host.OrdinaryRequestAsync();
        Assert.Equal(HttpStatusCode.OK, after.StatusCode);
        // Only the automatic backup of the current data may remain; the upload's spool is gone.
        Assert.True(host.StoredBackups().Count <= 1);
        Assert.False(host.AnyFileContains(PlainMarker));
    }

    // ---- criterion 12: unavailable ---------------------------------------------------

    [Theory]
    [InlineData("tracker-without-backup")]
    [InlineData("server-unreachable")]
    public async Task When_the_tracker_offers_no_backup_the_workitems_backup_is_unavailable_with_a_reason(string how)
    {
        await using var host = await Host.StartAsync();
        if (how == "tracker-without-backup") host.Wi.Offered = false;
        else host.Wi.InfoThrows = true;

        var status = await host.StatusAsync();
        using var create = await host.CreateAsync("workitems", Pass);
        using var restore = await host.RestoreAsync("workitems", Archive(), Pass, "RESTORE");

        var workitems = DbStatus(status, "workitems");
        Assert.False(workitems.GetProperty("available").GetBoolean());
        Assert.False(string.IsNullOrWhiteSpace(workitems.GetProperty("reason").GetString()));
        Assert.False(create.IsSuccessStatusCode);
        Assert.False(restore.IsSuccessStatusCode);
        Assert.Equal(0, host.Wi.ExportCalls);
        Assert.Equal(0, host.Wi.ImportCalls);
    }

    [Fact]
    public async Task The_core_backup_is_unavailable_with_a_reason_when_the_database_is_not_PostgreSQL()
    {
        await using var host = await Host.StartAsync();

        var core = DbStatus(await host.StatusAsync(), "core");
        using var create = await host.CreateAsync("core", Pass);

        Assert.False(core.GetProperty("available").GetBoolean());
        Assert.False(string.IsNullOrWhiteSpace(core.GetProperty("reason").GetString()));
        Assert.False(create.IsSuccessStatusCode);
    }

    [Fact]
    public async Task The_workitems_backup_is_available_and_the_status_names_whether_a_secret_key_is_configured()
    {
        await using var host = await Host.StartAsync();

        var status = await host.StatusAsync();

        Assert.True(DbStatus(status, "workitems").GetProperty("available").GetBoolean());
        Assert.True(status.TryGetProperty("secretKeyConfigured", out var key)
            && key.ValueKind is JsonValueKind.True or JsonValueKind.False);
    }

    // ---- criterion 15 ----------------------------------------------------------------

    [Fact]
    public async Task The_storage_overview_shows_the_database_sizes_it_knows_and_nothing_for_the_ones_it_does_not()
    {
        await using var host = await Host.StartAsync();
        host.Wi.DatabaseBytes = 7_654_321;

        var known = await Overview(host);
        host.Wi.Offered = false;
        var unknown = await Overview(host);

        Assert.Equal(7_654_321, Bytes(known, "workitems")!.Value);
        Assert.Null(Bytes(known, "core"));
        Assert.Null(Bytes(unknown, "workitems"));
        Assert.All(known.GetProperty("databases").EnumerateArray(),
            d => Assert.False(string.IsNullOrWhiteSpace(d.GetProperty("label").GetString())));

        static async Task<JsonElement> Overview(Host h)
        {
            using var response = await h.User.GetAsync("/api/v1/storage", Ct);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        }

        static long? Bytes(JsonElement overview, string key)
        {
            var bytes = overview.GetProperty("databases").EnumerateArray()
                .Single(d => d.GetProperty("key").GetString() == key).GetProperty("bytes");
            return bytes.ValueKind == JsonValueKind.Null ? null : bytes.GetInt64();
        }
    }

    // ---- uploads of any size ---------------------------------------------------------

    [Fact]
    public async Task The_restore_upload_has_no_request_size_limit_and_no_multipart_limit()
    {
        await using var host = await Host.StartAsync();

        var endpoint = host.Factory.Services.GetServices<Microsoft.AspNetCore.Routing.EndpointDataSource>()
            .SelectMany(s => s.Endpoints)
            .OfType<Microsoft.AspNetCore.Routing.RouteEndpoint>()
            .Single(e => e.RoutePattern.RawText!.EndsWith("/restore", StringComparison.Ordinal)
                && e.RoutePattern.RawText.Contains("storage/backups", StringComparison.Ordinal));

        var size = endpoint.Metadata.GetMetadata<Microsoft.AspNetCore.Http.Metadata.IRequestSizeLimitMetadata>();
        Assert.NotNull(size);
        Assert.Null(size!.MaxRequestBodySize);
        var form = endpoint.Metadata.GetMetadata<Microsoft.AspNetCore.Http.Metadata.IFormOptionsMetadata>();
        Assert.Equal(long.MaxValue, form?.MultipartBodyLengthLimit);
    }

    // ---- criterion 9: each background service skips its pass during a restore --------

    private sealed class SignalingTime : TimeProvider
    {
        public TaskCompletionSource Waiting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Waiting.TrySetResult();
            return base.CreateTimer(callback, state, dueTime, period);
        }
    }

    private static async Task OnePassAsync(Microsoft.Extensions.Hosting.BackgroundService service, SignalingTime time)
    {
        await service.StartAsync(Ct);
        await time.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        await service.StopAsync(CancellationToken.None);
    }

    private static Task SweepOnceAsync(object service)
        => (Task)service.GetType().GetMethod("SweepOnceAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(service, [CancellationToken.None])!;

    [Theory]
    [InlineData("work-item scheduler")]
    [InlineData("throttled-resume sweeper")]
    [InlineData("stuck-run watchdog")]
    [InlineData("pr status poller")]
    [InlineData("chat schedule scheduler")]
    public async Task A_background_service_skips_its_pass_during_a_restore_and_works_again_after(string service)
    {
        await using var host = await Host.StartAsync();
        var sp = host.Factory.Services;
        var firingId = Guid.Empty;

        using (var scope = sp.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            switch (service)
            {
                case "throttled-resume sweeper":
                    await scope.ServiceProvider.GetRequiredService<IAppSettingStore>()
                        .UpsertAsync(AppSettingKeys.ThrottleAutoResume, "true", Ct);
                    SeedRun(db, LoopRunStatus.WaitingHuman, DateTime.UtcNow.AddMinutes(-61), throttled: true);
                    break;
                case "stuck-run watchdog":
                    SeedRun(db, LoopRunStatus.Running, DateTime.UtcNow.AddMinutes(-10), throttled: false);
                    break;
                case "chat schedule scheduler":
                    firingId = SeedRunningFiring(db);
                    break;
            }
        }

        async Task PassAsync()
        {
            var time = new SignalingTime();
            switch (service)
            {
                case "work-item scheduler":
                    await OnePassAsync(ActivatorUtilities.CreateInstance<WorkItemScheduler>(sp,
                        new StaticOptionsMonitor<WorkItemSchedulerOptions>(new WorkItemSchedulerOptions
                        {
                            Enabled = true,
                            BaseUrl = "http://localhost",
                            ApiKey = "k",
                            PollInterval = TimeSpan.FromHours(1),
                        }), (TimeProvider)time), time);
                    break;
                case "pr status poller":
                    await OnePassAsync(ActivatorUtilities.CreateInstance<PrStatusPoller>(sp, (TimeProvider)time), time);
                    break;
                case "chat schedule scheduler":
                    await OnePassAsync(ActivatorUtilities.CreateInstance<ChatScheduleScheduler>(sp, (TimeProvider)time), time);
                    break;
                case "throttled-resume sweeper":
                    await SweepOnceAsync(ActivatorUtilities.CreateInstance<ThrottledRunResumeSweeper>(sp));
                    break;
                case "stuck-run watchdog":
                    await SweepOnceAsync(ActivatorUtilities.CreateInstance<StuckRunWatchdog>(sp));
                    break;
            }
        }

        bool Acted() => service switch
        {
            "work-item scheduler" => host.Coordinator.Invocations.Any(i => i.Method.Name == "RunPollCycleAsync"),
            "pr status poller" => host.PrPoll.Invocations.Any(i => i.Method.Name == "PollOnceAsync"),
            "throttled-resume sweeper" => host.Engine.Invocations.Any(i => i.Method.Name == "ResumeFromHaltAsync"),
            "stuck-run watchdog" => host.Recovery.Invocations.Any(i => i.Method.Name == "RecoverRunAsync"),
            _ => FiringOutcome(sp, firingId) != ChatScheduleFiringOutcome.Running,
        };

        var backup = await host.CreateBackupAsync("workitems", Pass);
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.Wi.HoldImport = hold.Task;
        var restore = host.RestoreAsync("workitems", backup, Pass, "RESTORE");
        await host.Wi.ImportEntered.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        host.Coordinator.Invocations.Clear();
        host.PrPoll.Invocations.Clear();
        host.Engine.Invocations.Clear();
        host.Recovery.Invocations.Clear();
        host.WorkItems.Invocations.Clear();

        await PassAsync();
        var actedDuringRestore = Acted();
        var calledTheWorkItemServer = host.WorkItems.Invocations.Count > 0;

        hold.SetResult();
        using var finished = await restore;
        Assert.Equal(HttpStatusCode.OK, finished.StatusCode);
        await PassAsync();

        Assert.False(actedDuringRestore, $"the {service} acted during the restore");
        Assert.False(calledTheWorkItemServer, $"the {service} called the WorkItem Server during the restore");
        Assert.True(Acted(), $"the {service} did nothing after the restore either, so the test proves nothing");
    }

    private static Guid SeedRun(AppDbContext db, LoopRunStatus status, DateTime touched, bool throttled)
    {
        var template = new LoopTemplate { Id = Guid.NewGuid(), Name = "t", RecoveryPolicy = RecoveryPolicy.AutoResume };
        var version = new LoopTemplateVersion { Id = Guid.NewGuid(), LoopTemplateId = template.Id, VersionNumber = 1 };
        var run = new LoopRun
        {
            Id = Guid.NewGuid(),
            WorkItemId = Guid.NewGuid().ToString(),
            LoopTemplateVersionId = version.Id,
            RecoveryPolicy = RecoveryPolicy.AutoResume,
            Status = status,
            StartedAt = touched,
            CreatedAt = touched,
            UpdatedAt = touched,
        };
        if (throttled)
        {
            run.IsHalted = true;
            run.HaltReason = HaltReason.Throttled;
            run.HumanFeedbackReason = HumanFeedbackReasons.AiProviderThrottled;
        }
        db.LoopTemplates.Add(template);
        db.LoopTemplateVersions.Add(version);
        db.LoopRuns.Add(run);
        db.SaveChanges();
        return run.Id;
    }

    private static Guid SeedRunningFiring(AppDbContext db)
    {
        var schedule = new ChatSchedule
        {
            Id = Guid.NewGuid(), UserId = "alice", Name = "Nightly check", Prompt = "Check.",
            CronExpression = "0 3 * * *", TimeZone = "UTC", Enabled = true, CreatedAt = DateTime.UtcNow,
        };
        var firing = new ChatScheduleFiring
        {
            Id = Guid.NewGuid(), ScheduleId = schedule.Id, Sequence = 1, Trigger = ChatScheduleTrigger.RunNow,
            FiredAt = DateTime.UtcNow, Outcome = ChatScheduleFiringOutcome.Running,
        };
        db.ChatSchedules.Add(schedule);
        db.ChatScheduleFirings.Add(firing);
        db.SaveChanges();
        return firing.Id;
    }

    private static ChatScheduleFiringOutcome FiringOutcome(IServiceProvider sp, Guid id)
    {
        using var scope = sp.CreateScope();
        return scope.ServiceProvider.GetRequiredService<AppDbContext>().ChatScheduleFirings
            .Where(f => f.Id == id).Select(f => f.Outcome).Single();
    }
}
