using ILD.Core.Services.Implementations;
using ILD.Core.Services.Interfaces;
using ILD.Data.DTOs;
using ILD.Data.Entities;
using ILD.Data.Enums;
using ILD.Data.Stores;
using ILD.Data.Stores.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace ILD.Tests;

/// <summary>A chat is titled from its first successful exchange, even when that is not the first turn.</summary>
public sealed class ChatTitleRetryTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly string _scratchRoot = Path.Combine(Path.GetTempPath(), "ild-chat-title-retry", Guid.NewGuid().ToString("N"));
    private readonly List<string> _titlePrompts = new();
    private int _chatTurns;

    public void Dispose()
    {
        _db.Dispose();
        try { if (Directory.Exists(_scratchRoot)) Directory.Delete(_scratchRoot, true); } catch { }
    }

    /// <summary>Fails the chat's first turn, answers its second, and titles the chat when asked.</summary>
    private sealed class Adapter(ChatTitleRetryTests owner) : IAgentAdapter
    {
        public string Name => "fake";
        public string[] SupportedProviderTypes => ["claude-code"];
        public ConfigFieldDescriptor[] ConfigSchema => [];
        public AdapterModelSupport ModelSupport => AdapterModelSupport.Unsupported;

        public Task<NodeExecutionResult> ExecuteAsync(AgentExecutionContext context)
        {
            if (context.NoTools)
            {
                lock (owner._titlePrompts) owner._titlePrompts.Add(context.Prompt);
                return Task.FromResult(NodeExecutionResult.Ok("Login page fix"));
            }
            return Task.FromResult(++owner._chatTurns == 1
                ? NodeExecutionResult.Fail("provider unavailable")
                : NodeExecutionResult.Ok("The login form posts to the wrong route."));
        }
    }

    private sealed class Registry(ChatTitleRetryTests owner) : IAgentAdapterRegistry
    {
        public Func<IAgentAdapter> ResolveForProvider(AiProvider provider) => () => new Adapter(owner);
        public string[] GetAllSupportedProviderTypes() => ["claude-code"];
        public AdapterModelSupport GetModelSupport(string providerType) => AdapterModelSupport.Unsupported;
    }

    /// <summary>The real scheduler, with each job it hands back kept so the test can await it.</summary>
    private sealed class RecordingScheduler(ChatTitleScheduler inner) : IChatTitleScheduler
    {
        public List<Task> Jobs { get; } = new();

        public Task Schedule(Guid chatSessionId, string? openWorkItemId)
        {
            var job = inner.Schedule(chatSessionId, openWorkItemId);
            Jobs.Add(job);
            return job;
        }
    }

    [Fact]
    public async Task A_chat_whose_first_reply_failed_is_titled_from_its_first_message_and_its_first_successful_reply()
    {
        var ct = TestContext.Current.CancellationToken;
        await _db.Settings.UpsertAsync(AppSettingKeys.ChatSmartTitles, "true");
        var provider = new AiProvider
        {
            Id = Guid.NewGuid(),
            Name = "Main",
            Type = "claude-code",
            BaseUrl = "http://localhost",
            Model = "m",
            IsDefault = true,
            Parallelism = 1,
            CreatedAt = DateTime.UtcNow,
        };
        await _db.Providers.CreateAiProviderAsync(provider, []);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped(_ => _db.Fresh());
        services.AddScoped<IProviderStore>(sp => new ProviderStore(sp.GetRequiredService<AppDbContext>()));
        services.AddScoped<IAppSettingStore>(sp => new AppSettingStore(sp.GetRequiredService<AppDbContext>()));
        services.AddSingleton<IAgentAdapterRegistry>(new Registry(this));
        services.AddSingleton(Mock.Of<IChatNotifier>());
        services.AddSingleton(Mock.Of<IWorkItemManager>());
        services.AddScoped<ChatTitleGenerator>();
        using var serviceProvider = services.BuildServiceProvider();
        using var scheduler = ActivatorUtilities.CreateInstance<ChatTitleScheduler>(serviceProvider);
        var titles = new RecordingScheduler(scheduler);
        var chat = new ChatService(_db.Context, _db.Providers, new Registry(this), Mock.Of<IChatNotifier>(),
            new ChatOptions { ScratchRoot = _scratchRoot }, _db.LoopRuns, new ChatLoopScratchpad(), titles: titles);
        var started = await chat.StartAsync("alice", provider.Id, ["ild"], ct);

        await chat.ExecuteTurnAsync(started.Id, Guid.NewGuid(), "Why does the login page break?", CancellationToken.None);
        Assert.Empty(titles.Jobs);

        await chat.ExecuteTurnAsync(started.Id, Guid.NewGuid(), "Try again please", CancellationToken.None);
        await Assert.Single(titles.Jobs).WaitAsync(TimeSpan.FromSeconds(30), ct);

        var prompt = Assert.Single(_titlePrompts);
        Assert.Contains("Why does the login page break?", prompt);
        Assert.Contains("The login form posts to the wrong route.", prompt);
        Assert.DoesNotContain("[chat-error]", prompt);
        Assert.DoesNotContain("Try again please", prompt);
        using var read = _db.Fresh();
        var session = await read.ChatSessions.AsNoTracking().SingleAsync(c => c.Id == started.Id, ct);
        Assert.Equal("Login page fix", session.Name);
        Assert.Equal(ChatTitleSource.Auto, session.TitleSource);
    }
}
