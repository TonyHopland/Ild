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

/// <summary>An untitled chat is offered for a title after each successful reply, up to its title attempts.</summary>
public sealed class ChatTitleRetryTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly string _scratchRoot = Path.Combine(Path.GetTempPath(), "ild-chat-title-retry", Guid.NewGuid().ToString("N"));
    private readonly List<string> _titlePrompts = new();
    private Func<int, NodeExecutionResult> _turn = _ => NodeExecutionResult.Ok("The login form posts to the wrong route.");
    private int _chatTurns;

    public void Dispose()
    {
        _db.Dispose();
        try { if (Directory.Exists(_scratchRoot)) Directory.Delete(_scratchRoot, true); } catch { }
    }

    /// <summary>Answers chat turns as <see cref="_turn"/> says, and titles the chat when asked.</summary>
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
            return Task.FromResult(owner._turn(++owner._chatTurns));
        }
    }

    private sealed class Registry(ChatTitleRetryTests owner) : IAgentAdapterRegistry
    {
        public Func<IAgentAdapter> ResolveForProvider(AiProvider provider) => () => new Adapter(owner);
        public string[] GetAllSupportedProviderTypes() => ["claude-code"];
        public AdapterModelSupport GetModelSupport(string providerType) => AdapterModelSupport.Unsupported;
    }

    /// <summary>Records each job handed off, and runs it on <paramref name="inner"/> when there is one.</summary>
    private sealed class RecordingScheduler(IChatTitleScheduler? inner) : IChatTitleScheduler
    {
        public List<(int ReplySequence, Task Job)> Jobs { get; } = new();

        public Task Schedule(Guid chatSessionId, string? openWorkItemId, int replySequence)
        {
            var job = inner?.Schedule(chatSessionId, openWorkItemId, replySequence) ?? Task.CompletedTask;
            Jobs.Add((replySequence, job));
            return job;
        }
    }

    private async Task<AiProvider> SeedProviderAsync()
    {
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
        return provider;
    }

    private ChatService NewChatService(IChatTitleScheduler titles)
        => new(_db.Context, _db.Providers, new Registry(this), Mock.Of<IChatNotifier>(),
            new ChatOptions { ScratchRoot = _scratchRoot }, _db.LoopRuns, new ChatLoopScratchpad(), titles: titles);

    private Task TurnAsync(ChatService chat, Guid id, string message)
        => chat.ExecuteTurnAsync(id, Guid.NewGuid(), message, CancellationToken.None);

    private ChatSession Read(Guid id)
    {
        using var ctx = _db.Fresh();
        return ctx.ChatSessions.AsNoTracking().Single(c => c.Id == id);
    }

    [Fact]
    public async Task A_chat_whose_first_turn_failed_with_output_is_titled_from_its_first_message_and_the_next_successful_reply()
    {
        var ct = TestContext.Current.CancellationToken;
        var provider = await SeedProviderAsync();
        // A failed turn the CLI still printed something for is stored as that output.
        _turn = turn => turn == 1
            ? NodeExecutionResult.Fail("exit=1", "Claude is overloaded, please try again later.")
            : NodeExecutionResult.Ok("The login form posts to the wrong route.");

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
        var chat = NewChatService(titles);
        var started = await chat.StartAsync("alice", provider.Id, ["ild"], ct);

        await TurnAsync(chat, started.Id, "Why does the login page break?");
        Assert.Empty(titles.Jobs);

        await TurnAsync(chat, started.Id, "Try again please");
        var (replySequence, job) = Assert.Single(titles.Jobs);
        Assert.Equal(3, replySequence);
        await job.WaitAsync(TimeSpan.FromSeconds(30), ct);

        var prompt = Assert.Single(_titlePrompts);
        Assert.Contains("Why does the login page break?", prompt);
        Assert.Contains("The login form posts to the wrong route.", prompt);
        Assert.DoesNotContain("overloaded", prompt);
        Assert.DoesNotContain("Try again please", prompt);
        var session = Read(started.Id);
        Assert.Equal("Login page fix", session.Name);
        Assert.Equal(ChatTitleSource.Auto, session.TitleSource);
    }

    [Theory]
    [InlineData(null, new[] { 1, 3, 5 })]
    [InlineData("1", new[] { 1 })]
    [InlineData("4", new[] { 1, 3, 5, 7 })]
    public async Task An_untitled_chat_is_offered_for_a_title_after_each_reply_up_to_its_title_attempts(string? maxAttempts, int[] offered)
    {
        var ct = TestContext.Current.CancellationToken;
        var provider = await SeedProviderAsync();
        if (maxAttempts is not null) await _db.Settings.UpsertAsync(AppSettingKeys.ChatTitleMaxAttempts, maxAttempts);
        // Jobs that never title the chat, so it stays on its fallback.
        var titles = new RecordingScheduler(inner: null);
        var chat = NewChatService(titles);
        var started = await chat.StartAsync("alice", provider.Id, ["ild"], ct);

        for (var message = 1; message <= 5; message++)
            await TurnAsync(chat, started.Id, $"message {message}");

        Assert.Equal(offered, titles.Jobs.Select(j => j.ReplySequence));
    }

    [Theory]
    [InlineData(ChatTitleSource.Manual)]
    [InlineData(ChatTitleSource.Auto)]
    public async Task A_chat_already_titled_is_never_offered_for_a_title(ChatTitleSource source)
    {
        var ct = TestContext.Current.CancellationToken;
        var provider = await SeedProviderAsync();
        var titles = new RecordingScheduler(inner: null);
        var chat = NewChatService(titles);
        var started = await chat.StartAsync("alice", provider.Id, ["ild"], ct);
        using (var ctx = _db.Fresh())
        {
            await ctx.ChatSessions.Where(c => c.Id == started.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.Name, "Named already").SetProperty(c => c.TitleSource, source), ct);
        }

        await TurnAsync(chat, started.Id, "first");
        await TurnAsync(chat, started.Id, "second");

        Assert.Empty(titles.Jobs);
    }
}
