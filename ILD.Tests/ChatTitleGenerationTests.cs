using System.Collections.Concurrent;
using ILD.Core.Services.Implementations;
using ILD.Core.Services.Interfaces;
using ILD.Data;
using ILD.Data.DTOs;
using ILD.Data.Entities;
using ILD.Data.Enums;
using ILD.Data.Stores;
using ILD.Data.Stores.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;

namespace ILD.Tests;

/// <summary>
/// Smart session titles: once a chat's first exchange is stored, a background job
/// asks the provider the title tag resolves to for a short title and saves it, but
/// only while the chat still carries its fallback title. Driven through the
/// scheduler the chat turn hands the job to, with every scope it opens getting its
/// own context on the test database, as a request scope would.
/// </summary>
public sealed class ChatTitleGenerationTests : IDisposable
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    private readonly TestDb _db = new();
    private readonly RecordingNotifier _notifier = new();
    private readonly RecordingLoggerProvider _logs = new();
    private readonly Mock<IWorkItemManager> _workItems = new();
    private readonly string _scratchRoot = Path.Combine(Path.GetTempPath(), "ild-chat-title-tests", Guid.NewGuid().ToString("N"));

    // What the title model does when asked; replaced per test.
    private Func<AgentExecutionContext, Task<NodeExecutionResult>> _titleModel =
        _ => Task.FromResult(NodeExecutionResult.Ok("Login page fix"));
    private readonly List<AgentExecutionContext> _modelCalls = new();
    private bool _noAdapter;

    public ChatTitleGenerationTests()
    {
        _workItems.Setup(w => w.GetWorkItemAsync("WI-7"))
            .ReturnsAsync(new WorkItemView { Id = "WI-7", Title = "Chat session sidebar with unread markers" });
    }

    public void Dispose()
    {
        _db.Dispose();
        try { if (Directory.Exists(_scratchRoot)) Directory.Delete(_scratchRoot, true); } catch { }
    }

    private sealed class ScriptedAdapter(ChatTitleGenerationTests owner) : IAgentAdapter
    {
        public string Name => "fake";
        public string[] SupportedProviderTypes => ["claude-code", "copilot"];
        public ConfigFieldDescriptor[] ConfigSchema => [];
        public AdapterModelSupport ModelSupport => AdapterModelSupport.Unsupported;

        public Task<NodeExecutionResult> ExecuteAsync(AgentExecutionContext context)
        {
            lock (owner._modelCalls) owner._modelCalls.Add(context);
            return owner._titleModel(context);
        }
    }

    private sealed class ScriptedRegistry(ChatTitleGenerationTests owner) : IAgentAdapterRegistry
    {
        public Func<IAgentAdapter> ResolveForProvider(AiProvider provider)
            => owner._noAdapter
                ? throw new InvalidOperationException($"No adapter for provider type '{provider.Type}'")
                : () => new ScriptedAdapter(owner);

        public string[] GetAllSupportedProviderTypes() => ["claude-code", "copilot"];
        public AdapterModelSupport GetModelSupport(string providerType) => AdapterModelSupport.Unsupported;
    }

    private sealed class RecordingNotifier : IChatNotifier
    {
        private readonly ConcurrentQueue<(string UserId, Guid ChatSessionId)> _titleChanged = new();
        public IReadOnlyList<(string UserId, Guid ChatSessionId)> TitleChanged => _titleChanged.ToList();

        public Task TitleChangedAsync(string userId, Guid chatSessionId)
        {
            _titleChanged.Enqueue((userId, chatSessionId));
            return Task.CompletedTask;
        }

        public Task MessageAppendedAsync(Guid chatSessionId, Guid turnId, ChatMessageView message) => Task.CompletedTask;
        public Task TurnProgressAsync(Guid chatSessionId, Guid turnId, string delta) => Task.CompletedTask;
        public Task TurnStartedAsync(Guid chatSessionId, Guid turnId) => Task.CompletedTask;
        public Task TurnCompletedAsync(Guid chatSessionId, Guid turnId, bool interrupted) => Task.CompletedTask;
        public Task LoopUpdateRequestedAsync(Guid chatSessionId, string document) => Task.CompletedTask;
        public Task EditProposalsChangedAsync(Guid chatSessionId) => Task.CompletedTask;
        public Task UnreadChangedAsync(string userId, Guid chatSessionId) => Task.CompletedTask;
    }

    private sealed class RecordingLoggerProvider : ILoggerProvider
    {
        private readonly ConcurrentQueue<LogLevel> _levels = new();
        public IReadOnlyList<LogLevel> Levels => _levels.ToList();

        public ILogger CreateLogger(string categoryName) => new Logger(this);
        public void Dispose() { }

        private sealed class Logger(RecordingLoggerProvider owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
                => owner._levels.Enqueue(logLevel);
        }
    }

    private ServiceProvider BuildServices()
    {
        var services = new ServiceCollection();
        services.AddLogging(b => b.AddProvider(_logs));
        services.AddScoped(_ => _db.Fresh());
        services.AddScoped<IProviderStore>(sp => new ProviderStore(sp.GetRequiredService<AppDbContext>()));
        services.AddScoped<IAppSettingStore>(sp => new AppSettingStore(sp.GetRequiredService<AppDbContext>()));
        services.AddSingleton<IAgentAdapterRegistry>(new ScriptedRegistry(this));
        services.AddSingleton<IChatNotifier>(_notifier);
        services.AddSingleton(_workItems.Object);
        services.AddScoped<ChatTitleGenerator>();
        return services.BuildServiceProvider();
    }

    // Built through the container so the test pins neither the constructor's
    // parameter order nor how the logger is passed; only the timeout is given.
    private static ChatTitleScheduler NewScheduler(IServiceProvider services, TimeSpan? timeout = null)
        => timeout is { } t
            ? ActivatorUtilities.CreateInstance<ChatTitleScheduler>(services, t)
            : ActivatorUtilities.CreateInstance<ChatTitleScheduler>(services);

    private async Task SmartTitlesAsync(bool on, string? tag = null)
    {
        await _db.Settings.UpsertAsync(AppSettingKeys.ChatSmartTitles, on ? "true" : "false");
        if (tag is not null) await _db.Settings.UpsertAsync(AppSettingKeys.ChatTitleProviderTag, tag);
    }

    private async Task<AiProvider> SeedProviderAsync(string name, bool isDefault, string type = "claude-code", params string[] tags)
    {
        var provider = new AiProvider
        {
            Id = Guid.NewGuid(),
            Name = name,
            Type = type,
            BaseUrl = "http://localhost",
            Model = "m",
            IsDefault = isDefault,
            Parallelism = 1,
            CreatedAt = DateTime.UtcNow,
        };
        await _db.Providers.CreateAiProviderAsync(provider, tags);
        return provider;
    }

    /// <summary>A chat whose first exchange is stored, plus any later messages.</summary>
    private async Task<Guid> SeedChatAsync(
        string firstMessage = "Can you look at why the login page breaks?",
        string firstReply = "The login form posts to the wrong route.",
        ChatTitleSource titleSource = ChatTitleSource.Fallback,
        string name = "Can you look at why the login page breaks?",
        params string[] laterMessages)
    {
        var id = Guid.NewGuid();
        var scratch = Path.Combine(_scratchRoot, id.ToString("N"));
        Directory.CreateDirectory(scratch);
        using var ctx = _db.Fresh();
        ctx.ChatSessions.Add(new ChatSession
        {
            Id = id,
            UserId = "alice",
            Name = name,
            TitleSource = titleSource,
            AiProviderId = Guid.NewGuid(),
            ProviderType = "claude-code",
            ToolAllowlistCsv = "ild,read,write",
            ScratchPath = scratch,
            CurrentSessionId = "agent-session-1",
        });
        var contents = new[] { firstMessage, firstReply }.Concat(laterMessages).ToArray();
        for (var seq = 0; seq < contents.Length; seq++)
        {
            ctx.ChatMessages.Add(new ChatMessage
            {
                Id = Guid.NewGuid(),
                ChatSessionId = id,
                Role = seq % 2 == 0 ? "user" : "assistant",
                Content = contents[seq],
                Sequence = seq,
                CreatedAt = DateTime.UtcNow,
            });
        }
        await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);
        return id;
    }

    private ChatSession Read(Guid id)
    {
        using var ctx = _db.Fresh();
        return ctx.ChatSessions.AsNoTracking().Single(c => c.Id == id);
    }

    private async Task RunAsync(ChatTitleScheduler scheduler, Guid id, string? openWorkItemId = null)
        => await scheduler.Schedule(id, openWorkItemId).WaitAsync(Patience, TestContext.Current.CancellationToken);

    private void AssertUntouched(Guid id, string name = "Can you look at why the login page breaks?")
    {
        var session = Read(id);
        Assert.Equal(name, session.Name);
        Assert.Equal(ChatTitleSource.Fallback, session.TitleSource);
        Assert.Empty(_notifier.TitleChanged);
    }

    private ChatService NewChatService(AppDbContext ctx)
        => new(ctx, new ProviderStore(ctx), new ScriptedRegistry(this), _notifier,
            new ChatOptions { ScratchRoot = _scratchRoot }, new LoopRunStore(ctx), new ChatLoopScratchpad());

    [Fact]
    public async Task The_first_exchange_is_summarised_on_the_tagged_provider_and_saved_as_an_auto_title()
    {
        await SeedProviderAsync("Main", isDefault: true);
        await SeedProviderAsync("Fast", isDefault: false, tags: "Fast");
        await SmartTitlesAsync(on: true, tag: "Fast");
        var id = await SeedChatAsync(laterMessages: ["SECRET-LATER-QUESTION", "SECRET-LATER-ANSWER"]);
        _titleModel = _ => Task.FromResult(NodeExecutionResult.Ok("\"Login page fix.\""));
        using var services = BuildServices();
        using var scheduler = NewScheduler(services);

        await RunAsync(scheduler, id, openWorkItemId: "WI-7");

        var call = Assert.Single(_modelCalls);
        Assert.Equal("Fast", call.Provider.Name);
        Assert.Contains("Can you look at why the login page breaks?", call.Prompt);
        Assert.Contains("The login form posts to the wrong route.", call.Prompt);
        Assert.Contains("Chat session sidebar with unread markers", call.Prompt);
        Assert.DoesNotContain("SECRET-LATER", call.Prompt);
        // A side call: the chat's own agent session is neither resumed nor managed.
        Assert.False(call.ManageSession);
        Assert.NotEqual("agent-session-1", call.SessionId);
        Assert.NotEqual("agent-session-1", call.IncomingSessionId);
        Assert.Null(call.ChatSessionId);
        // A plain model call: no MCP server, and no tool the agent can be kept from.
        Assert.True(call.NoTools);

        var session = Read(id);
        Assert.Equal("Login page fix", session.Name);
        Assert.Equal(ChatTitleSource.Auto, session.TitleSource);
        Assert.Equal(new[] { ("alice", id) }, _notifier.TitleChanged);
    }

    [Theory]
    [InlineData("", "Main")]
    [InlineData("Nobody", "Main")]
    [InlineData("Fast", "Fast")]
    public async Task The_title_runs_on_the_provider_the_tag_resolves_to_like_an_AI_node(string tag, string expected)
    {
        await SeedProviderAsync("Main", isDefault: true);
        await SeedProviderAsync("Fast", isDefault: false, tags: "Fast");
        await SmartTitlesAsync(on: true, tag: tag);
        var id = await SeedChatAsync();
        using var services = BuildServices();
        using var scheduler = NewScheduler(services);

        await RunAsync(scheduler, id);

        Assert.Equal(expected, Assert.Single(_modelCalls).Provider.Name);
        Assert.Equal("Login page fix", Read(id).Name);
    }

    [Theory]
    [InlineData("claude-code")]
    [InlineData("opencode")]
    [InlineData("pi")]
    public async Task The_title_call_gets_no_tools_beyond_read_and_no_mcp_servers(string providerType)
    {
        await SeedProviderAsync("Main", isDefault: true, type: providerType);
        await SmartTitlesAsync(on: true);
        var id = await SeedChatAsync();
        using var services = BuildServices();
        using var scheduler = NewScheduler(services);

        await RunAsync(scheduler, id);

        // NoTools: no MCP server anywhere and no tools on Claude Code; OpenCode and
        // pi enforce the allowlist, which must be explicit, as null would hand the
        // agent its default tools.
        var call = Assert.Single(_modelCalls);
        Assert.True(call.NoTools);
        Assert.NotNull(call.ToolAllowlist);
        Assert.Equal(new[] { "read" }, call.ToolAllowlist);
    }

    [Fact]
    public async Task A_copilot_provider_whose_tools_cannot_be_turned_off_is_not_asked_and_the_fallback_stays()
    {
        await SeedProviderAsync("Main", isDefault: true, type: "copilot");
        await SmartTitlesAsync(on: true);
        var id = await SeedChatAsync();
        using var services = BuildServices();
        using var scheduler = NewScheduler(services);

        await RunAsync(scheduler, id);

        Assert.Empty(_modelCalls);
        AssertUntouched(id);
        Assert.Contains(LogLevel.Warning, _logs.Levels);
    }

    [Fact]
    public async Task Only_the_start_of_a_long_first_message_and_of_a_long_reply_is_sent()
    {
        await SeedProviderAsync("Main", isDefault: true);
        await SmartTitlesAsync(on: true);
        var id = await SeedChatAsync(firstMessage: new string('u', 5000), firstReply: new string('r', 3000));
        using var services = BuildServices();
        using var scheduler = NewScheduler(services);

        await RunAsync(scheduler, id);

        var prompt = Assert.Single(_modelCalls).Prompt;
        Assert.Contains(new string('u', 4000), prompt);
        Assert.DoesNotContain(new string('u', 4001), prompt);
        Assert.Contains(new string('r', 1500), prompt);
        Assert.DoesNotContain(new string('r', 1501), prompt);
    }

    [Fact]
    public async Task A_work_item_that_cannot_be_read_still_leaves_the_chat_titled()
    {
        await SeedProviderAsync("Main", isDefault: true);
        await SmartTitlesAsync(on: true);
        _workItems.Setup(w => w.GetWorkItemAsync("WI-9"))
            .ThrowsAsync(new InvalidOperationException("WorkItem server is not configured"));
        var id = await SeedChatAsync();
        using var services = BuildServices();
        using var scheduler = NewScheduler(services);

        await RunAsync(scheduler, id, openWorkItemId: "WI-9");

        Assert.Equal("Login page fix", Read(id).Name);
    }

    [Fact]
    public async Task With_the_switch_off_no_model_is_asked_and_turning_it_on_applies_to_the_next_first_exchange()
    {
        await SeedProviderAsync("Main", isDefault: true);
        var before = await SeedChatAsync();
        using var services = BuildServices();
        using var scheduler = NewScheduler(services);

        // Nothing stored: the switch is off on a fresh install.
        await RunAsync(scheduler, before);
        await SmartTitlesAsync(on: false);
        await RunAsync(scheduler, before);

        Assert.Empty(_modelCalls);
        AssertUntouched(before);

        await SmartTitlesAsync(on: true);
        var after = await SeedChatAsync();
        await RunAsync(scheduler, after);

        Assert.Equal("Login page fix", Read(after).Name);
        Assert.Equal(ChatTitleSource.Fallback, Read(before).TitleSource);
    }

    [Theory]
    [InlineData(ChatTitleSource.Manual)]
    [InlineData(ChatTitleSource.Auto)]
    public async Task A_chat_whose_title_is_no_longer_the_fallback_is_left_alone(ChatTitleSource source)
    {
        await SeedProviderAsync("Main", isDefault: true);
        await SmartTitlesAsync(on: true);
        var id = await SeedChatAsync(titleSource: source, name: "Named already");
        using var services = BuildServices();
        using var scheduler = NewScheduler(services);

        await RunAsync(scheduler, id);

        var session = Read(id);
        Assert.Equal("Named already", session.Name);
        Assert.Equal(source, session.TitleSource);
        Assert.Empty(_notifier.TitleChanged);
    }

    [Fact]
    public async Task A_rename_landing_while_the_title_is_generated_wins()
    {
        await SeedProviderAsync("Main", isDefault: true);
        await SmartTitlesAsync(on: true);
        var id = await SeedChatAsync();
        using var renameContext = _db.Fresh();
        var renameRequest = NewChatService(renameContext);
        _titleModel = async _ =>
        {
            Assert.True(await renameRequest.RenameAsync("alice", id, "My own name", CancellationToken.None));
            return NodeExecutionResult.Ok("Login page fix");
        };
        using var services = BuildServices();
        using var scheduler = NewScheduler(services);

        await RunAsync(scheduler, id);

        var session = Read(id);
        Assert.Equal("My own name", session.Name);
        Assert.Equal(ChatTitleSource.Manual, session.TitleSource);
        // Only the rename announced a title.
        Assert.Single(_notifier.TitleChanged);
    }

    public enum Failure { ModelThrows, ModelFails, NothingLeftAfterCleaning, NoAdapter, NoProvider }

    [Theory]
    [InlineData(Failure.ModelThrows)]
    [InlineData(Failure.ModelFails)]
    [InlineData(Failure.NothingLeftAfterCleaning)]
    [InlineData(Failure.NoAdapter)]
    [InlineData(Failure.NoProvider)]
    public async Task A_failed_generation_logs_a_warning_and_keeps_the_fallback(Failure failure)
    {
        // No default provider, and the tag names nobody: nothing to run the title on.
        await SeedProviderAsync("Main", isDefault: failure != Failure.NoProvider);
        await SmartTitlesAsync(on: true, tag: failure == Failure.NoProvider ? "Nobody" : "");
        _noAdapter = failure == Failure.NoAdapter;
        _titleModel = failure switch
        {
            Failure.ModelThrows => _ => throw new InvalidOperationException("model crashed"),
            Failure.ModelFails => _ => Task.FromResult(NodeExecutionResult.Fail("rate limited")),
            _ => _ => Task.FromResult(NodeExecutionResult.Ok("  \"\"  ")),
        };
        var id = await SeedChatAsync();
        using var services = BuildServices();
        using var scheduler = NewScheduler(services);

        await RunAsync(scheduler, id);

        AssertUntouched(id);
        Assert.Contains(LogLevel.Warning, _logs.Levels);
    }

    [Fact]
    public async Task A_title_model_that_never_answers_is_given_up_on_at_the_timeout()
    {
        await SeedProviderAsync("Main", isDefault: true);
        await SmartTitlesAsync(on: true);
        _titleModel = async ctx =>
        {
            await Task.Delay(Timeout.Infinite, ctx.Cancel);
            return NodeExecutionResult.Ok("too late");
        };
        var id = await SeedChatAsync();
        using var services = BuildServices();
        using var scheduler = NewScheduler(services, timeout: TimeSpan.FromMilliseconds(1));

        await RunAsync(scheduler, id);

        AssertUntouched(id);
        Assert.Contains(LogLevel.Warning, _logs.Levels);
    }

    [Fact]
    public async Task A_chat_deleted_while_its_title_is_generated_ends_the_job_quietly()
    {
        await SeedProviderAsync("Main", isDefault: true);
        await SmartTitlesAsync(on: true);
        var id = await SeedChatAsync();
        using var deleteContext = _db.Fresh();
        var deleteRequest = NewChatService(deleteContext);
        _titleModel = async _ =>
        {
            Assert.True(await deleteRequest.DeleteAsync("alice", id, CancellationToken.None));
            return NodeExecutionResult.Ok("Login page fix");
        };
        using var services = BuildServices();
        using var scheduler = NewScheduler(services);

        await RunAsync(scheduler, id);

        using var ctx = _db.Fresh();
        Assert.False(await ctx.ChatSessions.AnyAsync(c => c.Id == id, TestContext.Current.CancellationToken));
        Assert.Empty(_notifier.TitleChanged);
    }
}
