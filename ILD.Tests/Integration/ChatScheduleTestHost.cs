using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Channels;
using ILD.Api.Hubs;
using ILD.Core.Services.Implementations;
using ILD.Core.Services.Interfaces;
using ILD.Data;
using ILD.Data.DTOs;
using ILD.Data.Entities;
using ILD.Data.Enums;
using ILD.Data.Stores.Interfaces;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Moq;

namespace ILD.Tests.Integration;

/// <summary>
/// The real API on a clock the test sets, with the chat scheduler's background
/// loop left out: a scheduler pass happens only when the test runs one. Chat
/// turns run on a scripted adapter, so a test sees exactly which turns started
/// and with what, and decides how each one ends. What the chat hub would send is
/// recorded rather than sent.
///
/// <para>Every DbContext gets its own connection to a database file, as Postgres
/// gives each its own session: turns use the database in the background while
/// the test's requests do, and one SqliteConnection cannot be shared across
/// threads. A second host can open the same file, as ILD does after a restart.</para>
/// </summary>
internal sealed class ChatScheduleTestHost : IAsyncDisposable
{
    public static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    public ManualClock Clock { get; }
    public ScriptedChatAdapter Adapter { get; } = new();
    public ScriptedAdapterRegistry Registry { get; }
    public RecordingChatHub Hub { get; } = new();
    public ApiFactory Factory { get; }
    public HttpClient Client { get; private set; } = null!;
    public string DatabaseFile { get; }

    private readonly bool _ownsDatabase;

    private ChatScheduleTestHost(DateTime startUtc, string? databaseFile)
    {
        Clock = new ManualClock(startUtc);
        Registry = new ScriptedAdapterRegistry(Adapter);
        _ownsDatabase = databaseFile is null;
        DatabaseFile = databaseFile ?? CreateDatabaseFile();
        var connectionString = ConnectionString(DatabaseFile);

        Factory = new ApiFactory(configureServices: services =>
        {
            services.RemoveAll<DbContextOptions<AppDbContext>>();
            services.RemoveAll<IDbContextOptionsConfiguration<AppDbContext>>();
            services.AddDbContext<AppDbContext>(options => options.UseSqlite(connectionString,
                sql => sql.MigrationsAssembly(typeof(AppDbContext).Assembly)));

            LeaveOutScheduleLoop(services);
            services.ReplaceSingleton<TimeProvider>(Clock);
            services.ReplaceSingleton<IAgentAdapterRegistry>(Registry);
            services.ReplaceSingleton<IHubContext<ChatHub>>(Hub.Context);
        });
    }

    public static Task<ChatScheduleTestHost> StartAsync(string startUtc) => StartAsync(startUtc, databaseFile: null);

    /// <summary>A second ILD on the database another host has open, as after a restart.</summary>
    public static Task<ChatScheduleTestHost> RestartOnAsync(ChatScheduleTestHost running, string nowUtc)
        => StartAsync(nowUtc, running.DatabaseFile);

    private static async Task<ChatScheduleTestHost> StartAsync(string startUtc, string? databaseFile)
    {
        var host = new ChatScheduleTestHost(ParseUtc(startUtc), databaseFile);
        host.Client = await host.Factory.CreateAuthenticatedClientAsync();
        return host;
    }

    public async ValueTask DisposeAsync()
    {
        Adapter.ReleaseHung();
        Registry.ReleaseAll();
        await Factory.DisposeAsync();
        if (!_ownsDatabase) return;
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(Path.GetDirectoryName(DatabaseFile)!, recursive: true); } catch { }
    }

    private static string ConnectionString(string file) => $"Data Source={file}";

    private static string CreateDatabaseFile()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ild-schedules-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, "ild.db");
        using var schema = SqliteSchemaTemplate<AppDbContext>.OpenCopy(options => new AppDbContext(options));
        using var target = new SqliteConnection(ConnectionString(file));
        target.Open();
        schema.BackupDatabase(target);
        // Readers never wait on a writer, and a writer waits its turn rather than failing.
        using var wal = target.CreateCommand();
        wal.CommandText = "PRAGMA journal_mode=WAL;";
        wal.ExecuteNonQuery();
        return file;
    }

    // The test runs every scheduler pass itself. Registered by type or through a
    // factory, the scheduler's hosted registration is replaced by one that does
    // nothing, while the scheduler itself stays resolvable.
    private static void LeaveOutScheduleLoop(IServiceCollection services)
    {
        for (var i = services.Count - 1; i >= 0; i--)
        {
            var descriptor = services[i];
            if (descriptor.ServiceType != typeof(IHostedService) || descriptor.IsKeyedService) continue;
            if (descriptor.ImplementationType == typeof(ChatScheduleScheduler)
                || descriptor.ImplementationInstance is ChatScheduleScheduler)
            {
                services.RemoveAt(i);
            }
            else if (descriptor.ImplementationFactory is { } factory)
            {
                services[i] = new ServiceDescriptor(typeof(IHostedService), sp =>
                {
                    var made = factory(sp);
                    return made is ChatScheduleScheduler ? new IdleHostedService() : made;
                }, descriptor.Lifetime);
            }
        }
    }

    private sealed class IdleHostedService : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    public static DateTime ParseUtc(string iso)
        => DateTime.Parse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    public static DateTime? Utc(JsonElement value)
        => value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined ? null : ParseUtc(value.GetString()!);

    /// <summary>One scheduler pass, as the background loop runs it.</summary>
    public Task RunDueAsync()
        => Factory.Services.GetRequiredService<ChatScheduleScheduler>().RunDueAsync(TestContext.Current.CancellationToken);

    /// <summary>The startup recovery of firings a restart left running, run on its own.</summary>
    public async Task RecoverInterruptedAsync()
    {
        using var scope = Factory.Services.CreateScope();
        var schedules = ActivatorUtilities.GetServiceOrCreateInstance<ChatScheduleService>(scope.ServiceProvider);
        await schedules.RecoverInterruptedAsync(TestContext.Current.CancellationToken);
    }

    public async Task<T> WithDbAsync<T>(Func<AppDbContext, Task<T>> read)
    {
        using var scope = Factory.Services.CreateScope();
        return await read(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    public async Task<Guid> SeedProviderAsync(string name, bool isDefault, params string[] tags)
    {
        using var scope = Factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IProviderStore>();
        var provider = new AiProvider
        {
            Id = Guid.NewGuid(),
            Name = name,
            Type = "claude-code",
            BaseUrl = string.Empty,
            Model = string.Empty,
            IsDefault = isDefault,
            Parallelism = 1,
            CreatedAt = DateTime.UtcNow,
        };
        await store.CreateAiProviderAsync(provider, tags);
        return provider.Id;
    }

    public async Task DeleteProviderAsync(Guid id)
    {
        using var scope = Factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IProviderStore>();
        await store.DeleteAiProviderAsync((await store.GetAiProviderByIdAsync(id))!);
    }

    public async Task<Guid> SeedRepositoryAsync(string name)
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var remote = new RemoteProvider
        {
            Id = Guid.NewGuid(),
            Name = "remote-" + name,
            Type = "forgejo",
            Url = "https://example.com",
            CreatedAt = DateTime.UtcNow,
        };
        db.RemoteProviders.Add(remote);
        var repo = new Repository
        {
            Id = Guid.NewGuid(),
            Name = name,
            CloneUrl = $"https://example.com/{name}.git",
            RemoteProviderId = remote.Id,
            DefaultIntakeStatus = WorkItemStatus.Backlog,
            CreatedAt = DateTime.UtcNow,
        };
        db.Repositories.Add(repo);
        await db.SaveChangesAsync();
        return repo.Id;
    }

    /// <summary>A valid schedule body; a test overrides what it is about.</summary>
    public static Dictionary<string, object?> Body(
        string name = "Weekly retro",
        string prompt = "Review how the loops did last week.",
        string? aiTag = "",
        string cron = "0 8 * * 1",
        string timeZone = "UTC",
        bool enabled = true,
        string repositoryScope = "All",
        IEnumerable<Guid>? repositoryIds = null,
        bool continueSession = true)
        => new()
        {
            ["name"] = name,
            ["prompt"] = prompt,
            ["aiTag"] = aiTag,
            ["cronExpression"] = cron,
            ["timeZone"] = timeZone,
            ["enabled"] = enabled,
            ["repositoryScope"] = repositoryScope,
            ["repositoryIds"] = (repositoryIds ?? []).ToArray(),
            ["continueSession"] = continueSession,
        };

    public Task<HttpResponseMessage> PostScheduleAsync(object body)
        => Client.PostAsJsonAsync("/api/v1/chat/schedules", body, TestContext.Current.CancellationToken);

    public Task<HttpResponseMessage> PutScheduleAsync(Guid id, object body)
        => Client.PutAsJsonAsync($"/api/v1/chat/schedules/{id}", body, TestContext.Current.CancellationToken);

    public Task<HttpResponseMessage> PostRunAsync(Guid id)
        => Client.PostAsync($"/api/v1/chat/schedules/{id}/run", null, TestContext.Current.CancellationToken);

    public async Task<Guid> CreateAsync(Dictionary<string, object?> body)
    {
        using var response = await PostScheduleAsync(body);
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(response.StatusCode == HttpStatusCode.Created, $"create answered {(int)response.StatusCode}: {text}");
        return JsonDocument.Parse(text).RootElement.GetProperty("id").GetGuid();
    }

    public async Task UpdateAsync(Guid id, Dictionary<string, object?> body)
    {
        using var response = await PutScheduleAsync(id, body);
        Assert.True(response.IsSuccessStatusCode,
            $"update answered {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)}");
    }

    public async Task DeleteAsync(Guid id)
    {
        using var response = await Client.DeleteAsync($"/api/v1/chat/schedules/{id}", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    /// <summary>The list as the Schedules page reads it: the paused flag and the caller's schedules.</summary>
    public async Task<JsonElement> ListPageAsync()
        => await Client.GetFromJsonAsync<JsonElement>("/api/v1/chat/schedules", TestContext.Current.CancellationToken);

    public async Task<JsonElement[]> ListAsync()
        => (await ListPageAsync()).GetProperty("schedules").EnumerateArray().ToArray();

    public async Task<JsonElement> GetAsync(Guid id)
        => (await ListAsync()).Single(s => s.GetProperty("id").GetGuid() == id);

    public async Task<JsonElement> LastFiringAsync(Guid id)
        => (await GetAsync(id)).GetProperty("lastFiring");

    /// <summary>Run now; the firing it answers with.</summary>
    public async Task<JsonElement> RunNowAsync(Guid id)
    {
        using var response = await PostRunAsync(id);
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"run now answered {(int)response.StatusCode}: {text}");
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    public async Task SetPausedAsync(bool paused)
    {
        using var response = await Client.PutAsJsonAsync($"/api/v1/settings/{AppSettingKeys.SchedulerIsPaused}",
            new { value = paused ? "true" : "false" }, TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>The schedule's last firing, once it satisfies <paramref name="done"/>.</summary>
    public async Task<JsonElement> LastFiringWhenAsync(Guid scheduleId, Func<JsonElement, bool> done, string waitingFor)
    {
        JsonElement last = default;
        await EventuallyAsync(async () =>
        {
            last = await LastFiringAsync(scheduleId);
            return last.ValueKind == JsonValueKind.Object && done(last);
        }, waitingFor);
        return last;
    }

    public Task<JsonElement> LastFiringEndedAsync(Guid scheduleId, string outcome)
        => LastFiringWhenAsync(scheduleId, f => IsOutcome(f, outcome), $"the last firing to end {outcome}");

    public static bool IsOutcome(JsonElement firing, string outcome)
        => string.Equals(firing.GetProperty("outcome").GetString(), outcome, StringComparison.OrdinalIgnoreCase);

    public static string Reason(JsonElement firing)
        => firing.TryGetProperty("reason", out var reason) && reason.ValueKind == JsonValueKind.String ? reason.GetString()! : "";

    public static Guid FiringId(JsonElement firing) => firing.GetProperty("id").GetGuid();

    /// <summary>The outcome of one firing, which need not be the schedule's last.</summary>
    public Task<string> FiringOutcomeAsync(Guid firingId)
        => WithDbAsync(async db => (await db.Set<ChatScheduleFiring>().AsNoTracking()
            .SingleAsync(f => f.Id == firingId, TestContext.Current.CancellationToken)).Outcome.ToString());

    public async Task<JsonElement[]> ChatHistoryAsync()
        => (await Client.GetFromJsonAsync<JsonElement[]>("/api/v1/chat/history", TestContext.Current.CancellationToken))!;

    public async Task<JsonElement> ChatAsync(Guid chatId)
        => await Client.GetFromJsonAsync<JsonElement>($"/api/v1/chat/{chatId}", TestContext.Current.CancellationToken);

    /// <summary>Once the chat has no turn in flight, so a firing will not find it busy.</summary>
    public Task ChatIdleAsync(Guid chatId)
        => EventuallyAsync(async () => (await ChatAsync(chatId)).GetProperty("activeTurnId").ValueKind == JsonValueKind.Null,
            $"chat {chatId} to fall idle");

    public static async Task EventuallyAsync(Func<Task<bool>> condition, string waitingFor)
    {
        var deadline = DateTime.UtcNow + Patience;
        while (!await condition())
        {
            Assert.True(DateTime.UtcNow < deadline, $"timed out waiting for {waitingFor}");
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }
    }
}

/// <summary>A clock that stands still until the test moves it, and whose timers never fire.</summary>
internal sealed class ManualClock(DateTime startUtc) : TimeProvider
{
    private long _ticks = startUtc.Ticks;

    public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);

    public void Set(string iso) => Interlocked.Exchange(ref _ticks, ChatScheduleTestHost.ParseUtc(iso).Ticks);

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        => new NeverTimer();

    private sealed class NeverTimer : ITimer
    {
        public bool Change(TimeSpan dueTime, TimeSpan period) => true;
        public void Dispose() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

/// <summary>
/// Stands in for every agent CLI. Each chat turn is recorded as it starts and
/// then succeeds, fails or hangs as <see cref="Behaviour"/> said when it started;
/// a hung turn ends when it is stopped or released.
/// </summary>
internal sealed class ScriptedChatAdapter : IAgentAdapter
{
    public enum Mode { Succeed, Fail, Hang }

    private readonly Channel<AgentExecutionContext> _started = Channel.CreateUnbounded<AgentExecutionContext>();
    private TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _chatTurns;

    public volatile Mode Behaviour = Mode.Succeed;

    public int ChatTurns => Volatile.Read(ref _chatTurns);

    public string Name => "scripted";
    public string[] SupportedProviderTypes => ["claude-code", "opencode", "pi", "copilot"];
    public ConfigFieldDescriptor[] ConfigSchema => [];
    public AdapterModelSupport ModelSupport => AdapterModelSupport.Unsupported;

    public async Task<NodeExecutionResult> ExecuteAsync(AgentExecutionContext context)
    {
        if (context.ChatSessionId is null) return NodeExecutionResult.Ok("not a chat turn");
        var mode = Behaviour;
        Interlocked.Increment(ref _chatTurns);
        _started.Writer.TryWrite(context);
        switch (mode)
        {
            case Mode.Fail:
                return NodeExecutionResult.Fail("the scripted agent failed");
            case Mode.Hang:
                var release = Volatile.Read(ref _release).Task;
                await Task.WhenAny(release, Task.Delay(Timeout.Infinite, context.Cancel));
                context.Cancel.ThrowIfCancellationRequested();
                return NodeExecutionResult.Ok("released");
            default:
                return NodeExecutionResult.Ok("done");
        }
    }

    /// <summary>The next chat turn to start.</summary>
    public async Task<AgentExecutionContext> NextTurnAsync()
        => await _started.Reader.ReadAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(ChatScheduleTestHost.Patience);

    public void ReleaseHung()
        => Interlocked.Exchange(ref _release, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();
}

/// <summary>
/// Hands out the scripted adapter for every provider. Looking up a provider the
/// test has blocked holds that caller until it is released, which is how a
/// firing is held at the step that creates its chat.
/// </summary>
internal sealed class ScriptedAdapterRegistry(ScriptedChatAdapter adapter) : IAgentAdapterRegistry
{
    private readonly Dictionary<Guid, (ManualResetEventSlim Release, TaskCompletionSource Entered)> _blocked = new();

    public void Block(Guid providerId)
    {
        lock (_blocked) _blocked[providerId] = (new ManualResetEventSlim(false), new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
    }

    public Task EnteredAsync(Guid providerId)
    {
        lock (_blocked) return _blocked[providerId].Entered.Task.WaitAsync(ChatScheduleTestHost.Patience);
    }

    public void Release(Guid providerId)
    {
        lock (_blocked)
        {
            if (_blocked.Remove(providerId, out var gate)) gate.Release.Set();
        }
    }

    public void ReleaseAll()
    {
        lock (_blocked)
        {
            foreach (var gate in _blocked.Values) gate.Release.Set();
            _blocked.Clear();
        }
    }

    public Func<IAgentAdapter> ResolveForProvider(AiProvider provider)
    {
        (ManualResetEventSlim Release, TaskCompletionSource Entered) gate;
        bool blocked;
        lock (_blocked) blocked = _blocked.TryGetValue(provider.Id, out gate);
        if (blocked)
        {
            gate.Entered.TrySetResult();
            gate.Release.Wait(ChatScheduleTestHost.Patience);
        }
        return () => adapter;
    }

    public string[] GetAllSupportedProviderTypes() => ["opencode", "pi", "claude-code", "copilot"];

    public AdapterModelSupport GetModelSupport(string providerType) => DeclaredModelSupport.For(providerType);
}

/// <summary>
/// The chat hub as the notifier reaches it: every send to a group is recorded
/// with its event name and payload, and nothing goes on the wire.
/// </summary>
internal sealed class RecordingChatHub
{
    public sealed record Sent(string Group, string Event, JsonElement Payload);

    private readonly ConcurrentQueue<Sent> _sent = new();
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    public IHubContext<ChatHub> Context { get; }

    public RecordingChatHub()
    {
        var clients = new Mock<IHubClients> { DefaultValue = DefaultValue.Mock };
        clients.Setup(c => c.Group(It.IsAny<string>())).Returns((string group) =>
        {
            var proxy = new Mock<IClientProxy>();
            proxy.Setup(p => p.SendCoreAsync(It.IsAny<string>(), It.IsAny<object?[]>(), It.IsAny<CancellationToken>()))
                .Callback((string method, object?[] args, CancellationToken _) =>
                    _sent.Enqueue(new Sent(group, method, JsonSerializer.SerializeToElement(args.Length == 1 ? args[0] : args, Web))))
                .Returns(Task.CompletedTask);
            return proxy.Object;
        });
        var context = new Mock<IHubContext<ChatHub>> { DefaultValue = DefaultValue.Mock };
        context.SetupGet(c => c.Clients).Returns(clients.Object);
        Context = context.Object;
    }

    public IReadOnlyList<Sent> SentTo(string group, string eventName)
        => _sent.Where(s => s.Group == group && s.Event == eventName).ToArray();
}
