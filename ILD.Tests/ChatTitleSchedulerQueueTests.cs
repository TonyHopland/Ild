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

/// <summary>A reply arriving while its chat's title job runs is not lost: the newest one runs next.</summary>
public sealed class ChatTitleSchedulerQueueTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly Guid _chatId = Guid.NewGuid();
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "ild-chat-title-queue", Guid.NewGuid().ToString("N"));
    private readonly List<string> _prompts = new();
    private readonly TaskCompletionSource _firstCallStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<NodeExecutionResult> _firstCallAnswer = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Dispose()
    {
        _db.Dispose();
        try { if (Directory.Exists(_scratch)) Directory.Delete(_scratch, true); } catch { }
    }

    /// <summary>Holds its first call until the test answers it; titles every later one.</summary>
    private sealed class Adapter(ChatTitleSchedulerQueueTests owner) : IAgentAdapter
    {
        public string Name => "fake";
        public string[] SupportedProviderTypes => ["claude-code"];
        public ConfigFieldDescriptor[] ConfigSchema => [];
        public AdapterModelSupport ModelSupport => AdapterModelSupport.Unsupported;

        public Task<NodeExecutionResult> ExecuteAsync(AgentExecutionContext context)
        {
            int call;
            lock (owner._prompts)
            {
                owner._prompts.Add(context.Prompt);
                call = owner._prompts.Count;
            }
            if (call > 1) return Task.FromResult(NodeExecutionResult.Ok("Login page fix"));
            owner._firstCallStarted.TrySetResult();
            return owner._firstCallAnswer.Task;
        }
    }

    private sealed class Registry(ChatTitleSchedulerQueueTests owner) : IAgentAdapterRegistry
    {
        public Func<IAgentAdapter> ResolveForProvider(AiProvider provider) => () => new Adapter(owner);
        public string[] GetAllSupportedProviderTypes() => ["claude-code"];
        public AdapterModelSupport GetModelSupport(string providerType) => AdapterModelSupport.Unsupported;
    }

    private async Task<ServiceProvider> SeedAsync()
    {
        await _db.Settings.UpsertAsync(AppSettingKeys.ChatSmartTitles, "true");
        await _db.Providers.CreateAiProviderAsync(new AiProvider
        {
            Id = Guid.NewGuid(),
            Name = "Main",
            Type = "claude-code",
            BaseUrl = "http://localhost",
            Model = "m",
            IsDefault = true,
            Parallelism = 1,
            CreatedAt = DateTime.UtcNow,
        }, []);

        Directory.CreateDirectory(_scratch);
        using (var ctx = _db.Fresh())
        {
            ctx.ChatSessions.Add(new ChatSession
            {
                Id = _chatId,
                UserId = "alice",
                Name = "Why does the login page break?",
                AiProviderId = Guid.NewGuid(),
                ProviderType = "claude-code",
                ToolAllowlistCsv = "read",
                ScratchPath = _scratch,
            });
            var contents = new[] { "Why does the login page break?", "REPLY-ONE", "more", "REPLY-TWO", "more", "REPLY-THREE" };
            for (var seq = 0; seq < contents.Length; seq++)
            {
                ctx.ChatMessages.Add(new ChatMessage
                {
                    Id = Guid.NewGuid(),
                    ChatSessionId = _chatId,
                    Role = seq % 2 == 0 ? "user" : "assistant",
                    Content = contents[seq],
                    Sequence = seq,
                    CreatedAt = DateTime.UtcNow,
                });
            }
            await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped(_ => _db.Fresh());
        services.AddScoped<IProviderStore>(sp => new ProviderStore(sp.GetRequiredService<AppDbContext>()));
        services.AddScoped<IAppSettingStore>(sp => new AppSettingStore(sp.GetRequiredService<AppDbContext>()));
        services.AddSingleton<IAgentAdapterRegistry>(new Registry(this));
        services.AddSingleton(Mock.Of<IChatNotifier>());
        services.AddSingleton(Mock.Of<IWorkItemManager>());
        services.AddScoped<ChatTitleGenerator>();
        return services.BuildServiceProvider();
    }

    private ChatSession Read()
    {
        using var ctx = _db.Fresh();
        return ctx.ChatSessions.AsNoTracking().Single(c => c.Id == _chatId);
    }

    [Fact]
    public async Task Replies_arriving_while_a_title_job_runs_wait_and_the_newest_runs_once_it_fails()
    {
        var ct = TestContext.Current.CancellationToken;
        using var services = await SeedAsync();
        using var scheduler = ActivatorUtilities.CreateInstance<ChatTitleScheduler>(services);

        var first = scheduler.Schedule(_chatId, null, replySequence: 1);
        await _firstCallStarted.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
        var second = scheduler.Schedule(_chatId, null, replySequence: 3);
        var third = scheduler.Schedule(_chatId, null, replySequence: 5);

        Assert.Same(second, third);
        Assert.False(third.IsCompleted);
        Assert.Single(_prompts);

        _firstCallAnswer.SetResult(NodeExecutionResult.Fail("rate limited"));
        await Task.WhenAll(first, third).WaitAsync(TimeSpan.FromSeconds(30), ct);

        Assert.Equal(2, _prompts.Count);
        Assert.Contains("REPLY-THREE", _prompts[1]);
        Assert.DoesNotContain("REPLY-TWO", _prompts[1]);
        var session = Read();
        Assert.Equal("Login page fix", session.Name);
        Assert.Equal(ChatTitleSource.Auto, session.TitleSource);
    }

    [Fact]
    public async Task A_waiting_reply_asks_no_model_once_the_running_job_has_titled_the_chat()
    {
        var ct = TestContext.Current.CancellationToken;
        using var services = await SeedAsync();
        using var scheduler = ActivatorUtilities.CreateInstance<ChatTitleScheduler>(services);

        var first = scheduler.Schedule(_chatId, null, replySequence: 1);
        await _firstCallStarted.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
        var waiting = scheduler.Schedule(_chatId, null, replySequence: 3);

        _firstCallAnswer.SetResult(NodeExecutionResult.Ok("Login page redirect"));
        await Task.WhenAll(first, waiting).WaitAsync(TimeSpan.FromSeconds(30), ct);

        Assert.Single(_prompts);
        Assert.Equal("Login page redirect", Read().Name);
    }
}
