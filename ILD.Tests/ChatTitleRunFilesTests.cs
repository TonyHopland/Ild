using ILD.Core.Services.Implementations;
using ILD.Core.Services.Interfaces;
using ILD.Data.DTOs;
using ILD.Data.Entities;
using ILD.Data.Stores;
using ILD.Data.Stores.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace ILD.Tests;

/// <summary>A title call runs under its own run id, and its run files go with the job.</summary>
public sealed class ChatTitleRunFilesTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly Guid _chatId = Guid.NewGuid();
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "ild-chat-title-run-files", Guid.NewGuid().ToString("N"));
    private readonly List<Guid> _runIds = new();
    private bool _modelSucceeds;

    public void Dispose()
    {
        _db.Dispose();
        foreach (var dir in _runIds.Append(_chatId).Select(PiAgentDirectory))
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
        try { if (Directory.Exists(_scratch)) Directory.Delete(_scratch, true); } catch { }
    }

    private static string PiAgentDirectory(Guid runId)
        => Path.Combine(AgentIsolation.ScratchRoot, "ild-pi-agent", runId.ToString("N"));

    /// <summary>Leaves a provider config under its run id, as pi does for a provider with an absolute base URL.</summary>
    private sealed class FilesLeavingAdapter(ChatTitleRunFilesTests owner) : IAgentAdapter
    {
        public string Name => "fake";
        public string[] SupportedProviderTypes => ["claude-code"];
        public ConfigFieldDescriptor[] ConfigSchema => [];
        public AdapterModelSupport ModelSupport => AdapterModelSupport.Unsupported;

        public Task<NodeExecutionResult> ExecuteAsync(AgentExecutionContext context)
        {
            var runId = context.RunContext.LoopRunId;
            owner._runIds.Add(runId);
            Directory.CreateDirectory(PiAgentDirectory(runId));
            File.WriteAllText(Path.Combine(PiAgentDirectory(runId), "models.json"), "{}");
            return Task.FromResult(owner._modelSucceeds
                ? NodeExecutionResult.Ok("Login page fix")
                : NodeExecutionResult.Fail("rate limited"));
        }
    }

    private sealed class Registry(ChatTitleRunFilesTests owner) : IAgentAdapterRegistry
    {
        public Func<IAgentAdapter> ResolveForProvider(AiProvider provider) => () => new FilesLeavingAdapter(owner);
        public string[] GetAllSupportedProviderTypes() => ["claude-code"];
        public AdapterModelSupport GetModelSupport(string providerType) => AdapterModelSupport.Unsupported;
    }

    private async Task SeedAsync()
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
        using var ctx = _db.Fresh();
        ctx.ChatSessions.Add(new ChatSession
        {
            Id = _chatId,
            UserId = "alice",
            Name = "Can you look at why the login page breaks?",
            AiProviderId = Guid.NewGuid(),
            ProviderType = "claude-code",
            ToolAllowlistCsv = "read",
            ScratchPath = _scratch,
        });
        foreach (var (seq, role) in new[] { (0, "user"), (1, "assistant") })
        {
            ctx.ChatMessages.Add(new ChatMessage
            {
                Id = Guid.NewGuid(),
                ChatSessionId = _chatId,
                Role = role,
                Content = $"{role} message",
                Sequence = seq,
                CreatedAt = DateTime.UtcNow,
            });
        }
        await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task The_title_call_runs_under_its_own_id_and_its_files_go_with_the_job(bool modelSucceeds)
    {
        _modelSucceeds = modelSucceeds;
        await SeedAsync();
        // What the chat's own turns keep under the chat's id.
        Directory.CreateDirectory(PiAgentDirectory(_chatId));
        File.WriteAllText(Path.Combine(PiAgentDirectory(_chatId), "models.json"), "{\"chat\":true}");

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped(_ => _db.Fresh());
        services.AddScoped<IProviderStore>(sp => new ProviderStore(sp.GetRequiredService<AppDbContext>()));
        services.AddScoped<IAppSettingStore>(sp => new AppSettingStore(sp.GetRequiredService<AppDbContext>()));
        services.AddSingleton<IAgentAdapterRegistry>(new Registry(this));
        services.AddSingleton(Mock.Of<IChatNotifier>());
        services.AddSingleton(Mock.Of<IWorkItemManager>());
        services.AddScoped<ChatTitleGenerator>();
        using var provider = services.BuildServiceProvider();
        using var scheduler = ActivatorUtilities.CreateInstance<ChatTitleScheduler>(provider);

        await scheduler.Schedule(_chatId, null).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        var runId = Assert.Single(_runIds);
        Assert.NotEqual(_chatId, runId);
        Assert.False(Directory.Exists(PiAgentDirectory(runId)));
        Assert.Equal("{\"chat\":true}", File.ReadAllText(Path.Combine(PiAgentDirectory(_chatId), "models.json")));
    }
}
