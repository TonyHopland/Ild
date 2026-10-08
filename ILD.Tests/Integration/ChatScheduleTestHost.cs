using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using System.Threading.Channels;
using ILD.Core.Services.Implementations;
using ILD.Core.Services.Interfaces;
using ILD.Core.Services.Remote;
using ILD.Data;
using ILD.Data.DTOs;
using ILD.Data.Entities;
using ILD.Data.Enums;
using ILD.Data.Stores.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ILD.Tests.Integration;

/// <summary>
/// The real API with its chat scheduler running, on a clock the test sets and
/// whose timers never fire: a scheduler pass happens only when something pulses
/// it. Chat turns run on a scripted adapter, so a test sees exactly which turns
/// started and with what, and decides how each one ends.
/// </summary>
internal sealed class ChatScheduleTestHost : IAsyncDisposable
{
    public static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    public ManualClock Clock { get; }
    public ScriptedChatAdapter Adapter { get; } = new();
    public ScriptedAdapterRegistry Registry { get; }
    public WorkItemServerFaults Faults { get; } = new();
    public ApiFactory Factory { get; }
    public HttpClient Client { get; private set; } = null!;

    private ChatScheduleTestHost(DateTime startUtc)
    {
        Clock = new ManualClock(startUtc);
        Registry = new ScriptedAdapterRegistry(Adapter);
        Factory = new ApiFactory(configureServices: services =>
        {
            services.ReplaceSingleton<TimeProvider>(Clock);
            services.ReplaceSingleton<IAgentAdapterRegistry>(Registry);
            var inner = (IWorkItemServerClient)services
                .Last(d => d.ServiceType == typeof(IWorkItemServerClient)).ImplementationInstance!;
            services.RemoveAll<IWorkItemServerClient>();
            services.AddSingleton(FaultyWorkItemServerClient.Wrap(inner, Faults));
        });
    }

    public static async Task<ChatScheduleTestHost> StartAsync(string startUtc)
    {
        var host = new ChatScheduleTestHost(ParseUtc(startUtc));
        host.Client = await host.Factory.CreateAuthenticatedClientAsync();
        return host;
    }

    public async ValueTask DisposeAsync()
    {
        Adapter.ReleaseHung();
        Registry.ReleaseAll();
        await Factory.DisposeAsync();
    }

    public static DateTime ParseUtc(string iso)
        => DateTime.Parse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    public static DateTime? Utc(JsonElement value)
        => value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined ? null : ParseUtc(value.GetString()!);

    public void Pulse() => Factory.Services.GetRequiredService<ChatScheduleScheduler>().Pulse();

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
        string repositoryScope = "all",
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

    public async Task<JsonElement> CreateAsync(Dictionary<string, object?> body)
    {
        using var response = await PostScheduleAsync(body);
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(response.StatusCode == HttpStatusCode.Created, $"create answered {(int)response.StatusCode}: {text}");
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    public async Task UpdateAsync(Guid id, Dictionary<string, object?> body)
    {
        using var response = await PutScheduleAsync(id, body);
        Assert.True(response.IsSuccessStatusCode,
            $"update answered {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)}");
    }

    public async Task<JsonElement[]> ListAsync()
        => (await Client.GetFromJsonAsync<JsonElement[]>("/api/v1/chat/schedules", TestContext.Current.CancellationToken))!;

    public async Task<JsonElement> GetAsync(Guid id)
        => (await ListAsync()).Single(s => s.GetProperty("id").GetGuid() == id);

    /// <summary>Run now; the firing it answers with.</summary>
    public async Task<JsonElement> RunNowAsync(Guid id)
    {
        using var response = await Client.PostAsync($"/api/v1/chat/schedules/{id}/run", null, TestContext.Current.CancellationToken);
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(response.IsSuccessStatusCode, $"run now answered {(int)response.StatusCode}: {text}");
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
            last = (await GetAsync(scheduleId)).GetProperty("lastFiring");
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
/// Hands out the scripted adapter for every provider. Looking up a provider that
/// the test has blocked holds that caller until it is released, which is how a
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

/// <summary>How the next work item create reaches the WorkItem server.</summary>
internal sealed class WorkItemServerFaults
{
    public enum Fault { None, RefusedBeforeCreating, LostAfterCreating }

    public volatile Fault Next = Fault.None;
}

/// <summary>
/// The fake WorkItem server's client, except that a create can be refused with a
/// status code (nothing created) or lose its answer after the item was created,
/// as a timeout or dropped connection does.
/// </summary>
public class FaultyWorkItemServerClient : DispatchProxy
{
    private IWorkItemServerClient _inner = null!;
    private WorkItemServerFaults _faults = null!;

    internal static IWorkItemServerClient Wrap(IWorkItemServerClient inner, WorkItemServerFaults faults)
    {
        var proxy = Create<IWorkItemServerClient, FaultyWorkItemServerClient>();
        var self = (FaultyWorkItemServerClient)(object)proxy;
        self._inner = inner;
        self._faults = faults;
        return proxy;
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod!.Name == nameof(IWorkItemServerClient.CreateAsync))
        {
            var fault = _faults.Next;
            _faults.Next = WorkItemServerFaults.Fault.None;
            if (fault == WorkItemServerFaults.Fault.RefusedBeforeCreating)
                return Task.FromException<RemoteWorkItem>(new HttpRequestException("refused", null, HttpStatusCode.BadRequest));
            if (fault == WorkItemServerFaults.Fault.LostAfterCreating)
                return CreateThenLoseAsync(args!);
        }

        try
        {
            return targetMethod.Invoke(_inner, args);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            throw ex.InnerException;
        }
    }

    private async Task<RemoteWorkItem> CreateThenLoseAsync(object?[] args)
    {
        await (Task<RemoteWorkItem>)typeof(IWorkItemServerClient).GetMethod(nameof(IWorkItemServerClient.CreateAsync))!.Invoke(_inner, args)!;
        throw new HttpRequestException("the connection was lost before the answer arrived");
    }
}
