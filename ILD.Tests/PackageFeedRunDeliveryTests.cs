using System.Text;
using ILD.Core.Services.Implementations;
using ILD.Core.Services.Implementations.Executors;
using ILD.Core.Services.Implementations.PackageFeeds;
using ILD.Core.Services.Interfaces;
using ILD.Data;
using ILD.Data.DTOs;
using ILD.Data.Entities;
using ILD.Data.Enums;
using ILD.Data.Stores.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ILD.Tests;

/// <summary>
/// A run's processes get the credentials of the feeds its repository selected:
/// the Start node's install steps, Cmd node commands and AI node agent launches.
/// Each launch's credential file is gone once the launch is, however it ended; a
/// selected feed that no longer exists is skipped with a warning; and a repository
/// that selected nothing (or only feeds that are gone) runs exactly as before.
/// </summary>
public sealed class PackageFeedRunDeliveryTests : IDisposable
{
    private const string UserConfig = "NPM_CONFIG_USERCONFIG";
    private const string NuGetEndpoints = "VSS_NUGET_EXTERNAL_FEED_ENDPOINTS";
    private const string CompanyPat = "companyPAT-11aa";
    private const string CompanyUrl = "https://pkgs.dev.azure.com/example-org/_packaging/company";
    private const string CompanyNuGet = "https://pkgs.dev.azure.com/example-org/_packaging/company/nuget/v3/index.json";

    private readonly TestDb _db = new();
    private readonly string _worktree = Directory.CreateTempSubdirectory("ild-feed-run-wt-").FullName;
    private readonly RecordingLogger _resolverLog = new();

    public void Dispose()
    {
        _db.Dispose();
        try { Directory.Delete(_worktree, recursive: true); } catch { /* best effort */ }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string CompanyPasswordLine
        => $"//pkgs.dev.azure.com/example-org/_packaging/company/npm/registry/:_password={Convert.ToBase64String(Encoding.UTF8.GetBytes(CompanyPat))}";

    // ── Seeding ──────────────────────────────────────────────────────────

    private async Task SeedFeedAsync(string name, string feedUrl, string pat)
    {
        _db.Context.Set<PackageFeed>().Add(new PackageFeed
        {
            Id = Guid.NewGuid(),
            Name = name,
            NormalizedName = name.Trim().ToUpperInvariant(),
            FeedUrl = feedUrl,
            Pat = pat,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await _db.Context.SaveChangesAsync(Ct);
    }

    private async Task<Guid> SeedRepositoryAsync(params string[] selectedFeeds)
    {
        var remote = new RemoteProvider { Id = Guid.NewGuid(), Name = "r", Type = "Forgejo", Url = "https://git.example.com" };
        var repo = new Repository
        {
            Id = Guid.NewGuid(),
            Name = "repo",
            CloneUrl = "https://git.example.com/o/r.git",
            DefaultBranch = "main",
            RemoteProviderId = remote.Id,
        };
        _db.Context.RemoteProviders.Add(remote);
        _db.Context.Repositories.Add(repo);
        foreach (var name in selectedFeeds)
        {
            _db.Context.Set<RepositoryPackageFeed>().Add(new RepositoryPackageFeed
            {
                RepositoryId = repo.Id,
                Name = name,
                NormalizedName = name.Trim().ToUpperInvariant(),
            });
        }
        await _db.Context.SaveChangesAsync(Ct);
        return repo.Id;
    }

    /// <summary>The company feed exists; "gone" was selected but has since been deleted.</summary>
    private async Task<Guid> RepositoryWithCompanyAndAMissingFeedAsync()
    {
        await SeedFeedAsync("company", CompanyUrl, CompanyPat);
        return await SeedRepositoryAsync("company", "gone");
    }

    private IPackageFeedResolver Resolver()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(_db.Context);
        services.AddDataStores();
        services.AddSingleton(_db.Providers);
        services.AddSingleton<ILogger<PackageFeedResolver>>(_resolverLog);
        return ActivatorUtilities.CreateInstance<PackageFeedResolver>(services.BuildServiceProvider());
    }

    private sealed class RecordingLogger : ILogger<PackageFeedResolver>
    {
        public List<string> Warnings { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning) Warnings.Add(formatter(state, exception));
        }
    }

    // ── Resolution ───────────────────────────────────────────────────────

    [Fact]
    public async Task A_selected_feed_that_no_longer_exists_is_skipped_with_a_warning_naming_it()
    {
        var repoId = await RepositoryWithCompanyAndAMissingFeedAsync();

        var resolved = await Resolver().ResolveAsync(repoId);

        var feed = Assert.Single(resolved.Feeds);
        Assert.Equal("company", feed.Name);
        Assert.Equal(CompanyPat, feed.Pat);
        Assert.Equal(new[] { "gone" }, resolved.Missing);
        Assert.Contains(_resolverLog.Warnings, w => w.Contains("gone"));
    }

    [Fact]
    public async Task A_repository_without_a_selection_or_no_repository_resolves_to_nothing()
    {
        await SeedFeedAsync("company", CompanyUrl, CompanyPat);
        var repoId = await SeedRepositoryAsync();

        foreach (var id in new Guid?[] { repoId, null })
        {
            var resolved = await Resolver().ResolveAsync(id);
            Assert.Empty(resolved.Feeds);
            Assert.Empty(resolved.Missing);
        }
        Assert.Empty(_resolverLog.Warnings);
    }

    // ── Start node install ───────────────────────────────────────────────

    [Fact]
    public async Task Start_node_install_steps_get_the_feeds_and_the_missing_one_is_a_warning_on_success()
    {
        var repoId = await RepositoryWithCompanyAndAMissingFeedAsync();
        File.WriteAllText(Path.Combine(_worktree, "ild.config.json"), """
        {
          "preview": {
            "defaultProfile": "app",
            "profiles": {
              "app": {
                "install": [
                  { "cwd": ".", "command": "printf '%s' \"$NPM_CONFIG_USERCONFIG\" > feed.path; cat \"$NPM_CONFIG_USERCONFIG\" > feed.npmrc; printf '%s' \"$VSS_NUGET_EXTERNAL_FEED_ENDPOINTS\" > feed.nuget" }
                ],
                "services": []
              }
            }
          }
        }
        """);
        var repoManager = new Mock<IRepositoryManager>();
        repoManager.Setup(m => m.ValidateWorktreeHealthAsync(_worktree)).ReturnsAsync(true);
        var workItems = new Mock<IWorkItemManager>();
        workItems.Setup(m => m.GetWorkItemAsync(It.IsAny<string>()))
            .ReturnsAsync(new WorkItemView { Id = "WI-1", Title = "T", RepositoryId = repoId });

        var services = new ServiceCollection();
        services.AddSingleton(workItems.Object);
        services.AddSingleton(_db.Providers);
        services.AddSingleton(repoManager.Object);
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddSingleton<IWorktreePreviewService>(new WorktreePreviewService(
            new Mock<IHttpClientFactory>().Object, new ConfigurationBuilder().Build(), PreviewProxyBase.Disabled,
            NullLogger<WorktreePreviewService>.Instance));
        services.AddSingleton(Resolver());
        var run = new LoopRun { Id = Guid.NewGuid(), WorkItemId = "WI-1", RepositoryId = repoId, WorktreePath = _worktree, BranchName = "b" };
        var node = new LoopNode { Id = Guid.NewGuid(), NodeType = NodeType.Start, Config = "{\"runInstall\":true}" };

        var outcomes = new List<NodeOutcome>();
        await foreach (var o in new StartNodeExecutor().ExecuteAsync(new NodeExecutionContext(run, node, services.BuildServiceProvider(), Ct)))
            outcomes.Add(o);

        var success = Assert.IsType<NodeOutcome.Success>(Assert.Single(outcomes, o => o is NodeOutcome.Success or NodeOutcome.Fail));
        Assert.Contains("gone", success.Output);
        var path = File.ReadAllText(Path.Combine(_worktree, "feed.path"));
        Assert.False(string.IsNullOrEmpty(path), "the install step got no npm user config");
        Assert.False(path.StartsWith(_worktree + Path.DirectorySeparatorChar, StringComparison.Ordinal), "the PAT file is inside the worktree");
        Assert.Contains(CompanyPasswordLine, File.ReadAllLines(Path.Combine(_worktree, "feed.npmrc")).Select(l => l.Trim()));
        Assert.Contains(CompanyNuGet, File.ReadAllText(Path.Combine(_worktree, "feed.nuget")));
        Assert.False(File.Exists(path), "the install's PAT file outlived the install");
    }

    // ── Cmd node ─────────────────────────────────────────────────────────

    private const string ReportCommand =
        "printf '%s\\n' \"$NPM_CONFIG_USERCONFIG\"; "
        + "[ -n \"$NPM_CONFIG_USERCONFIG\" ] && cat \"$NPM_CONFIG_USERCONFIG\"; "
        + "printf 'NUGET=%s\\n' \"$VSS_NUGET_EXTERNAL_FEED_ENDPOINTS\"; "
        + "printf 'REGISTRY=%s%s\\n' \"$npm_config_registry\" \"$NPM_CONFIG_REGISTRY\"";

    private NodeExecutionContext CmdContext(
        string command, Guid? runRepositoryId, Guid? workItemRepositoryId = null,
        CancellationToken cancel = default, Func<string, Task>? progress = null)
    {
        var workItems = new Mock<IWorkItemManager>();
        workItems.Setup(m => m.GetWorkItemAsync(It.IsAny<string>()))
            .ReturnsAsync(new WorkItemView { Id = "WI-1", Title = "T", RepositoryId = workItemRepositoryId });
        var services = new ServiceCollection();
        services.AddSingleton(workItems.Object);
        services.AddSingleton(Resolver());
        return new NodeExecutionContext(
            new LoopRun { Id = Guid.NewGuid(), WorkItemId = "WI-1", RepositoryId = runRepositoryId, WorktreePath = _worktree },
            new LoopNode
            {
                Id = Guid.NewGuid(),
                NodeType = NodeType.Cmd,
                Label = "cmd",
                Config = System.Text.Json.JsonSerializer.Serialize(new { command }),
            },
            services.BuildServiceProvider(),
            cancel == default ? Ct : cancel,
            progress);
    }

    private static async Task<List<NodeOutcome>> RunCmdAsync(NodeExecutionContext ctx)
    {
        var outcomes = new List<NodeOutcome>();
        await foreach (var outcome in new CmdNodeExecutor().ExecuteAsync(ctx)) outcomes.Add(outcome);
        return outcomes;
    }

    private void AssertDelivered(string output)
    {
        var lines = output.Split('\n').Select(l => l.Trim()).ToArray();
        var path = lines[0];
        Assert.False(string.IsNullOrEmpty(path), "the command got no npm user config");
        Assert.False(path.StartsWith(_worktree + Path.DirectorySeparatorChar, StringComparison.Ordinal), "the PAT file is inside the worktree");
        Assert.Contains(CompanyPasswordLine, lines);
        Assert.Contains(lines, l => l.StartsWith("NUGET=") && l.Contains(CompanyNuGet));
        Assert.Contains("REGISTRY=", lines);
        Assert.False(File.Exists(path), "the command's PAT file outlived the command");
    }

    [Fact]
    public async Task A_cmd_node_command_gets_the_feeds_and_its_file_is_gone_when_it_succeeds()
    {
        var repoId = await RepositoryWithCompanyAndAMissingFeedAsync();

        var outcomes = await RunCmdAsync(CmdContext(ReportCommand, repoId));

        var success = Assert.IsType<NodeOutcome.Success>(Assert.Single(outcomes, o => o is NodeOutcome.Success or NodeOutcome.Fail));
        AssertDelivered(success.Output!);
    }

    [Fact]
    public async Task A_cmd_node_command_of_a_run_without_its_own_repository_gets_its_work_items_feeds()
    {
        var repoId = await RepositoryWithCompanyAndAMissingFeedAsync();

        var outcomes = await RunCmdAsync(CmdContext(ReportCommand, runRepositoryId: null, workItemRepositoryId: repoId));

        var success = Assert.IsType<NodeOutcome.Success>(Assert.Single(outcomes, o => o is NodeOutcome.Success or NodeOutcome.Fail));
        AssertDelivered(success.Output!);
    }

    [Fact]
    public async Task A_failing_cmd_node_command_leaves_no_file_behind()
    {
        var repoId = await RepositoryWithCompanyAndAMissingFeedAsync();

        var outcomes = await RunCmdAsync(CmdContext(ReportCommand + "; exit 7", repoId));

        var fail = Assert.IsType<NodeOutcome.Fail>(Assert.Single(outcomes, o => o is NodeOutcome.Success or NodeOutcome.Fail));
        Assert.Equal("exit code 7", fail.Reason);
        AssertDelivered(fail.Output!);
    }

    [Fact]
    public async Task A_cancelled_cmd_node_command_leaves_no_file_behind()
    {
        var repoId = await RepositoryWithCompanyAndAMissingFeedAsync();
        using var cancel = new CancellationTokenSource();
        string? path = null;
        var ctx = CmdContext(
            "printf '%s\\n' \"$NPM_CONFIG_USERCONFIG\"; echo started; sleep 30",
            repoId,
            cancel: cancel.Token,
            progress: line =>
            {
                path ??= line.Trim();
                if (line.Contains("started", StringComparison.Ordinal)) cancel.Cancel();
                return Task.CompletedTask;
            });

        var outcomes = await RunCmdAsync(ctx).WaitAsync(TimeSpan.FromSeconds(30), Ct);

        Assert.Single(outcomes, o => o is NodeOutcome.Fail);
        Assert.False(string.IsNullOrEmpty(path), "the command got no npm user config");
        Assert.False(File.Exists(path), "a cancelled command's PAT file was left");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_cmd_node_command_of_a_repository_with_no_usable_feed_sees_no_feed_variable(bool selectsOnlyAMissingFeed)
    {
        await SeedFeedAsync("company", CompanyUrl, CompanyPat);
        var repoId = selectsOnlyAMissingFeed ? await SeedRepositoryAsync("gone") : await SeedRepositoryAsync();

        var outcomes = await RunCmdAsync(CmdContext("printf '[%s][%s]' \"${NPM_CONFIG_USERCONFIG-unset}\" \"${VSS_NUGET_EXTERNAL_FEED_ENDPOINTS-unset}\"", repoId));

        var success = Assert.IsType<NodeOutcome.Success>(Assert.Single(outcomes, o => o is NodeOutcome.Success or NodeOutcome.Fail));
        Assert.Equal("[unset][unset]", success.Output!.Trim());
    }

    // ── AI node ──────────────────────────────────────────────────────────

    public enum AdapterEnding { Succeeds, Fails, Throws, Cancelled }

    /// <summary>Sees the launch environment the executor hands the adapter, and what its file holds right then.</summary>
    private sealed class ObservingAdapter(AdapterEnding ending, CancellationTokenSource? cancelDuringLaunch = null) : IAgentAdapter
    {
        public IReadOnlyDictionary<string, string>? Environment { get; private set; }
        public bool FileExistedDuringLaunch { get; private set; }
        public string? FileContent { get; private set; }

        public string Name => "observing";
        public string[] SupportedProviderTypes => ["stub"];
        public ConfigFieldDescriptor[] ConfigSchema => [];
        public AdapterModelSupport ModelSupport => AdapterModelSupport.Unsupported;

        public Task<NodeExecutionResult> ExecuteAsync(AgentExecutionContext context)
        {
            Environment = context.Environment;
            if (Environment is not null && Environment.TryGetValue(UserConfig, out var path))
            {
                FileExistedDuringLaunch = File.Exists(path);
                FileContent = FileExistedDuringLaunch ? File.ReadAllText(path) : null;
            }
            return ending switch
            {
                AdapterEnding.Succeeds => Task.FromResult(NodeExecutionResult.Ok("done")),
                AdapterEnding.Fails => Task.FromResult(NodeExecutionResult.Fail("agent failed")),
                AdapterEnding.Throws => throw new InvalidOperationException("adapter blew up"),
                _ => CancelAndThrow(context.Cancel),
            };
        }

        private Task<NodeExecutionResult> CancelAndThrow(CancellationToken token)
        {
            cancelDuringLaunch?.Cancel();
            throw new OperationCanceledException(token);
        }
    }

    private async Task<List<NodeOutcome>> RunAiAsync(
        ObservingAdapter adapter, Guid? runRepositoryId, Guid? workItemRepositoryId = null, CancellationToken cancel = default)
    {
        var provider = new AiProvider
        {
            Id = Guid.NewGuid(),
            Name = "default",
            Type = "stub",
            IsDefault = true,
            Parallelism = 1,
            CreatedAt = DateTime.UtcNow,
        };
        var providerStore = new Mock<IProviderStore>();
        providerStore.Setup(s => s.GetDefaultAiProviderAsync()).ReturnsAsync(provider);
        var workItems = new Mock<IWorkItemManager>();
        workItems.Setup(m => m.GetWorkItemAsync(It.IsAny<string>()))
            .ReturnsAsync(new WorkItemView { Id = "WI-1", Title = "T", RepositoryId = workItemRepositoryId });
        var runs = Mock.Of<ILoopRunStore>(m =>
            m.GetByIdAsync(It.IsAny<Guid>()) == Task.FromResult<LoopRun?>(null) &&
            m.GetRunNodesAsync(It.IsAny<Guid>()) == Task.FromResult<IReadOnlyList<LoopRunNode>>(Array.Empty<LoopRunNode>()));
        var registry = Mock.Of<IAgentAdapterRegistry>(r =>
            r.ResolveForProvider(It.IsAny<AiProvider>()) == (Func<IAgentAdapter>)(() => adapter));

        var services = new ServiceCollection();
        services.AddSingleton(providerStore.Object);
        services.AddSingleton(runs);
        services.AddSingleton(workItems.Object);
        services.AddSingleton(registry);
        services.AddSingleton(Resolver());

        var ctx = new NodeExecutionContext(
            new LoopRun { Id = Guid.NewGuid(), WorkItemId = "WI-1", RepositoryId = runRepositoryId, WorktreePath = _worktree },
            new LoopNode { Id = Guid.NewGuid(), NodeType = NodeType.AI, Config = "{\"prompt\":\"go\"}" },
            services.BuildServiceProvider(),
            cancel == default ? Ct : cancel);

        var outcomes = new List<NodeOutcome>();
        try
        {
            await foreach (var o in new AINodeExecutor().ExecuteAsync(ctx))
                outcomes.Add(o);
        }
        catch (Exception ex) when (ex is InvalidOperationException or OperationCanceledException)
        {
            // Whether the executor reports these as a failure or lets them out is
            // not the question here; what the launch left behind is.
        }
        return outcomes;
    }

    [Theory]
    [InlineData(AdapterEnding.Succeeds)]
    [InlineData(AdapterEnding.Fails)]
    [InlineData(AdapterEnding.Throws)]
    [InlineData(AdapterEnding.Cancelled)]
    public async Task An_ai_node_launch_gets_the_feeds_and_its_file_is_gone_however_it_ends(AdapterEnding ending)
    {
        var repoId = await RepositoryWithCompanyAndAMissingFeedAsync();
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var adapter = new ObservingAdapter(ending, cancel);

        await RunAiAsync(adapter, repoId, cancel: cancel.Token);

        Assert.NotNull(adapter.Environment);
        var path = adapter.Environment![UserConfig];
        Assert.True(adapter.FileExistedDuringLaunch, "the npm user config was not there while the agent ran");
        Assert.Contains(CompanyPasswordLine, adapter.FileContent!.Split('\n').Select(l => l.Trim()));
        Assert.Contains(CompanyNuGet, adapter.Environment[NuGetEndpoints]);
        Assert.DoesNotContain(adapter.Environment.Keys, k => k.Equals("npm_config_registry", StringComparison.OrdinalIgnoreCase));
        Assert.False(path.StartsWith(_worktree + Path.DirectorySeparatorChar, StringComparison.Ordinal), "the PAT file is inside the worktree");
        Assert.False(File.Exists(path), $"the agent launch's PAT file outlived a launch that {ending}");
    }

    [Fact]
    public async Task An_ai_node_of_a_run_without_its_own_repository_gets_its_work_items_feeds()
    {
        var repoId = await RepositoryWithCompanyAndAMissingFeedAsync();
        var adapter = new ObservingAdapter(AdapterEnding.Succeeds);

        await RunAiAsync(adapter, runRepositoryId: null, workItemRepositoryId: repoId);

        Assert.True(adapter.FileExistedDuringLaunch);
        Assert.Contains(CompanyNuGet, adapter.Environment![NuGetEndpoints]);
    }

    [Fact]
    public async Task An_ai_node_of_a_repository_with_no_usable_feed_is_launched_without_feed_variables()
    {
        await SeedFeedAsync("company", CompanyUrl, CompanyPat);
        var repoId = await SeedRepositoryAsync("gone");
        var adapter = new ObservingAdapter(AdapterEnding.Succeeds);

        await RunAiAsync(adapter, repoId);

        Assert.False(adapter.Environment?.ContainsKey(UserConfig) ?? false);
        Assert.False(adapter.Environment?.ContainsKey(NuGetEndpoints) ?? false);
    }
}
