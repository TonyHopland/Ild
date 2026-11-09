using ILD.Core.Services.Backup;
using ILD.Core.Services.Interfaces;
using ILD.Core.Services.Implementations;
using ILD.Core.Services.Implementations.Executors;
using ILD.Core.Services.Implementations.Adapters;
using ILD.Core.Services.Implementations.ModelGateway;
using ILD.Core.Services.Implementations.Network;
using ILD.Core.Services.Implementations.PackageFeeds;
using ILD.Core.Services.Implementations.RemoteProviders;
using ILD.Core.Services.Implementations.Storage;
using ILD.Core.Services.Remote;
using ILD.Core.Services.RunHosts;
using ILD.Core.Services.RunHosts.Container;
using ILD.Core.Services.RunHosts.Kubernetes;
using ILD.Core.Services.RunHosts.Local;
using ILD.Api.Middleware;
using ILD.Api.Services;
using ILD.Data.Stores;
using ILD.Data.Stores.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ILD.Api.Configuration;

public static class ServiceCollectionExtensions
{
    private static Func<CancellationToken, Task<IReadOnlySet<Guid>>> RetainedRuns(IServiceProvider sp) => async ct =>
    {
        using var scope = sp.CreateScope();
        return await LiveRunIds.NotEndedAsync(scope.ServiceProvider.GetRequiredService<ILD.Data.Entities.AppDbContext>(), ct);
    };

    public static IServiceCollection AddIldServices(this IServiceCollection services)
    {
        services.AddSingleton<AgentAuthTokenProvider>();
        services.AddSingleton<IModelGatewayRegistry, ModelGatewayRegistry>();
        // One instance for the process: it holds the Codex refresh lock and the "refresh next" flag.
        services.AddHttpClient(nameof(ModelCredentialSource));
        services.AddSingleton<IModelCredentialSource>(sp => new ModelCredentialSource(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(ModelCredentialSource)),
            sp.GetRequiredService<ILogger<ModelCredentialSource>>()));

        services.AddScoped<IAuthService, AuthService>();
        services.AddScoped<IWorkItemManager, WorkItemManager>();
        services.AddScoped<ILoopTemplateManager, LoopTemplateManager>();
        services.AddScoped<IHumanInputProposalService, HumanInputProposalService>();
        services.AddSingleton<IRepositoryManager>(sp =>
        {
            var runner = sp.GetRequiredService<IProcessRunner>();
            var logger = sp.GetService<ILogger<RepositoryManager>>();
            var config = sp.GetRequiredService<IConfiguration>();
            var worktreesRoot = config["App:WorktreesPath"];

            return new RepositoryManager(
                runner,
                logger,
                string.IsNullOrWhiteSpace(worktreesRoot) ? null : worktreesRoot,
                mirrorsRoot: RepositoryManager.MirrorRoot(config),
                host: sp.GetRequiredService<IRunHost>());
        });
        services.AddSingleton<IRemoteRetryDelay, RemoteRetryDelay>();
        services.AddSingleton<IPromptTemplateResolver, PromptTemplateResolver>();
        services.AddScoped<IPromptRenderingService, PromptRenderingService>();
        services.AddSingleton<IProcessRunner, ProcessRunner>();
        // Not a setting: containers (or pods in a cluster) from ILD's image, its own processes under dotnet run.
        services.AddSingleton<IRunHost>(sp =>
        {
            var config = sp.GetRequiredService<IConfiguration>();
            var kind = RunHostOptions.Detect(name => config[name]);
            var runner = sp.GetRequiredService<IProcessRunner>();
            var local = new LocalRunHost(runner, sp);
            if (kind == RunHostKind.Local) return local;

            var loggers = sp.GetRequiredService<ILoggerFactory>();
            var host = kind == RunHostKind.Kubernetes
                ? KubernetesRunHostFactory.Create(local, name => config[name], runner, loggers, RetainedRuns(sp))
                : ContainerRunHostFactory.Create(local, name => config[name], runner, loggers, RetainedRuns(sp));
            host.Isolated.Probe = sp.GetRequiredService<NetworkEnforcementProbe>();
            // The declared forwards, answered on each run's and chat's own loopback (docs/adr/0020).
            host.Isolated.Forwards = async ct =>
            {
                using var scope = sp.GetRequiredService<IServiceScopeFactory>().CreateScope();
                var rows = await scope.ServiceProvider.GetRequiredService<INetworkForwardStore>().GetForwardsAsync(ct);
                return rows.Select(r => new ILD.Core.Services.RunHosts.Isolated.RunForward(r.LocalPort, r.Host, r.Port)).ToList();
            };
            return host;
        });
        // Before GracefulRunDrainService, so it stops after the drain has parked the runs.
        services.AddHostedService<RunHostShutdownService>();
        services.AddHostedService(sp => new RunHostSweeper(
            sp.GetRequiredService<IRunHost>(),
            RetainedRuns(sp),
            sp.GetRequiredService<IRunHost>() is RoutingRunHost routing ? routing.Isolated.SweepInterval : TimeSpan.FromMinutes(10),
            sp.GetService<ILogger<RunHostSweeper>>()));
        services.AddSingleton<IRemoteGitProviderAdapter, ForgejoRemoteGitProviderAdapter>();
        services.AddSingleton<IRemoteGitProviderAdapter, GitHubRemoteGitProviderAdapter>();
        services.AddSingleton<IRemoteGitProviderAdapter, AzureDevOpsRemoteGitProviderAdapter>();
        services.AddSingleton<IRemoteProviderTypeCatalog, RemoteProviderTypeCatalog>();
        services.AddScoped<IRemoteProvider, RemoteProviderService>();
        services.AddScoped<IConnectionTester, ConnectionTester>();
        services.AddScoped<IPackageFeedResolver, PackageFeedResolver>();
        services.AddHttpClient<IManagedAgentService, ManagedAgentService>();
        services.AddSingleton<ManagedAgentProvisioner>();
        services.AddSingleton<IManagedAgentProvisioner>(sp => sp.GetRequiredService<ManagedAgentProvisioner>());
        services.AddHostedService(sp => sp.GetRequiredService<ManagedAgentProvisioner>());
        // The one place ILD_PREVIEW_PROXY_BASE is read. Both the preview service
        // (which advertises preview URLs) and the proxy middleware (which matches
        // and serves them) take this instance, so there is a single parsed origin
        // and a single startup message about it.
        services.AddSingleton(sp =>
        {
            var proxyBase = PreviewProxyBase.FromConfiguration(sp.GetRequiredService<IConfiguration>());
            var logger = sp.GetRequiredService<ILogger<PreviewProxyBase>>();
            if (proxyBase.ConfigurationError != null)
                logger.LogWarning("Worktree preview proxy disabled: {Reason}", proxyBase.ConfigurationError);
            else if (proxyBase.Enabled)
                logger.LogInformation("Worktree previews served on *.{Host} over {Scheme} (unauthenticated)", proxyBase.Host, proxyBase.Scheme);
            return proxyBase;
        });
        services.AddSingleton<PreviewDataClaims>();
        services.AddSingleton<IWorktreePreviewService, WorktreePreviewService>();
        services.AddSingleton<IPreviewDataService, PreviewDataService>();
        services.AddScoped<IEventLogService, EventLogService>();
        services.AddScoped<IRunConversationService, RunConversationService>();
        services.AddHostedService<WorktreeRetentionSweeper>();
        services.AddHostedService<NetworkLogRetentionSweeper>();
        services.AddHostedService<ChatInactivitySweeper>();
        services.AddSingleton<IManagedAgentUsage, ManagedAgentUsage>();
        services.AddSingleton<AutoInstallForecast>();
        services.AddSingleton(sp => AgentUpdateOptions.FromEnvironment(sp.GetRequiredService<ILogger<AgentUpdateOptions>>()));
        services.AddHostedService<ManagedAgentAutoUpdater>();
        services.AddHostedService<StuckRunWatchdog>();
        services.AddHostedService<ThrottledRunResumeSweeper>();
        services.AddScoped<IRunReclaimer, RunReclaimer>();
        services.AddSingleton<IFinishedRunCleaner, FinishedRunCleaner>();
        services.AddSingleton(sp => ClaudeProjectFolders.ForProcess(sp.GetRequiredService<ILogger<ClaudeProjectFolders>>()));
        services.AddHostedService<ClaudeProjectFolderSweeper>();
        services.AddScoped<IStorageService, StorageService>();
        // Backup and restore (ADR-0029). The core archive is registered by the host that has PostgreSQL.
        services.AddSingleton<MaintenanceState>();
        services.AddSingleton<IMaintenanceState>(sp => sp.GetRequiredService<MaintenanceState>());
        services.AddSingleton<BackupOperationLock>();
        services.AddSingleton<BackupDirectory>();
        services.AddSingleton<DatabaseCatalog>();
        services.AddScoped<BackupService>();
        // Exports and imports run as long as the data is large, so this client has no timeout of its own.
        services.AddHttpClient<IWorkItemServerBackupClient, WorkItemServerBackupClient>(c => c.Timeout = Timeout.InfiniteTimeSpan);
        services.AddScoped<IBranchNameOverrideService, BranchNameOverrideService>();
        services.AddScoped<IRecoveryManager, RecoveryManager>();
        services.AddScoped<IPrSyncService, PrSyncService>();
        services.AddScoped<IAdapterSessionSnapshotStore, AdapterSessionSnapshotStore>();
        // Legacy (remove with migration B): converts rows still in the old SessionJson column.
        services.AddHostedService<SessionSnapshotMigrationService>();

        // Standalone chat (ADR-0010): the turn runner is a singleton (owns
        // interrupt/concurrency), the service is scoped (DbContext + adapter), and
        // the notifier streams to the chat hub. Chats are retained as history until
        // the user deletes them (ADR-0013) — no idle sweeper.
        services.AddSingleton(sp =>
        {
            var config = sp.GetRequiredService<IConfiguration>();
            var dataRoot = config["App:DataPath"]
                ?? Environment.GetEnvironmentVariable("ILD_DATA_PATH")
                ?? config["Storage:DataRoot"]
                ?? "data";
            return new ChatOptions
            {
                ScratchRoot = System.IO.Path.Combine(dataRoot, "chat-sessions"),
            };
        });
        services.AddScoped<IChatService, ChatService>();
        services.AddSingleton<IChatTurnRunner, ChatTurnRunner>();
        services.AddScoped<ChatTitleGenerator>();
        services.AddSingleton<IChatTitleScheduler, ChatTitleScheduler>();
        services.AddSingleton<SignalRChatNotifier>();
        services.AddSingleton<IChatNotifier>(sp => sp.GetRequiredService<SignalRChatNotifier>());
        services.AddSingleton<IChatScheduleNotifier>(sp => sp.GetRequiredService<SignalRChatNotifier>());
        // The loop scratchpad relays the open Loop Editor's live document from the
        // chat turn to the agent-scoped API (ADR-0011); a singleton so both scopes
        // share the same in-memory store.
        services.AddSingleton<IChatLoopScratchpad, ChatLoopScratchpad>();
        // Chat Schedules (ADR-0026): a firing is a chat turn, started by the scheduler
        // or by Run now; one lock per schedule, shared by both.
        services.AddSingleton<ChatScheduleLocks>();
        services.AddScoped<ChatScheduleService>();
        services.AddSingleton<ChatCloneCoordinator>();
        services.AddScoped<IChatRepositoryCloner, ChatRepositoryCloner>();
        services.AddSingleton<ChatScheduleScheduler>();
        services.AddHostedService(sp => sp.GetRequiredService<ChatScheduleScheduler>());

        services.AddSingleton<IRunProgressBuffer, RunProgressBuffer>();
        services.AddSingleton<IRunNotifier, SignalRRunNotifier>();
        services.AddSingleton<IWorkItemNotifier, SignalRWorkItemNotifier>();
        services.AddSingleton<SignalRLogNotifier>();

        // Agent egress filter (ADR-0019): the proxy every agent launch is pointed
        // at, the cached policy it consults, the recorder that persists what it
        // saw, and the enforcement status the entrypoint reported. All keyed off
        // ILD_NETWORK_PROXY_PORT; unset, nothing listens and launches are untouched.
        // A run's pod or container reaches the egress proxy over the network, not on ILD's loopback.
        services.AddSingleton(sp => EgressProxyOptions.FromEnvironment() with
        {
            BindAddress = EgressProxyOptions.BindAddressFor(RunHostOptions.Detect(name => sp.GetRequiredService<IConfiguration>()[name])),
        });
        services.AddSingleton(sp => new NetworkEnforcementProbe(name => Environment.GetEnvironmentVariable(name) ?? sp.GetRequiredService<IConfiguration>()[name]));
        services.AddSingleton(sp => NetworkEnforcementStatus.FromEnvironment(sp.GetRequiredService<EgressProxyOptions>()) with { Probe = sp.GetRequiredService<NetworkEnforcementProbe>() });
        services.AddSingleton<IProtectedDestinationSource>(sp =>
        {
            var config = sp.GetRequiredService<IConfiguration>();
            string? Read(string name) => Environment.GetEnvironmentVariable(name) ?? config[name];
            return new ProtectedDestinationSource(sp.GetRequiredService<IServiceScopeFactory>(), Read, RunHostOptions.Detect(Read), sp.GetRequiredService<TimeProvider>());
        });
        services.AddSingleton<IEgressPolicy, EgressPolicy>();
        services.AddSingleton<INetworkNotifier, SignalRNetworkNotifier>();
        services.AddSingleton<NetworkLogRecorder>();
        services.AddSingleton<INetworkLogRecorder>(sp => sp.GetRequiredService<NetworkLogRecorder>());
        services.AddHostedService(sp => sp.GetRequiredService<NetworkLogRecorder>());
        services.AddHostedService<EgressProxy>();
        // The declared forwards (ADR-0020) need none of that: they are loopback
        // listeners the orchestrator runs whether or not the proxy is enabled, for
        // clients that cannot name a destination in band.
        services.AddSingleton<EgressForwarder>();
        services.AddSingleton<IEgressForwarderState>(sp => sp.GetRequiredService<EgressForwarder>());
        services.AddHostedService(sp => sp.GetRequiredService<EgressForwarder>());
        services.AddSingleton<INodeExecutor, StartNodeExecutor>();
        services.AddSingleton<INodeExecutor, CmdNodeExecutor>();
        services.AddSingleton<INodeExecutor, AINodeExecutor>();
        services.AddSingleton<INodeExecutor, HumanNodeExecutor>();
        services.AddSingleton<INodeExecutor, PromptNodeExecutor>();
        services.AddSingleton<INodeExecutor, SetVariableNodeExecutor>();
        services.AddSingleton<INodeExecutor, PRNodeExecutor>();
        services.AddSingleton<INodeExecutor, ConditionNodeExecutor>();
        services.AddSingleton<INodeExecutor, CleanupNodeExecutor>();
        services.AddSingleton<INodeExecutorRegistry, NodeExecutorRegistry>();
        services.AddSingleton<ILoopEngine, LoopEngine>();
        services.AddScoped<IMetricsCollector, MetricsCollector>();
        services.AddScoped<IRunAnalyticsService, RunAnalyticsService>();
        services.AddScoped<IAnalyticsCleanupService, AnalyticsCleanupService>();
        services.AddScoped<ILoopRunDigestService, LoopRunDigestService>();
        services.AddSingleton<IAgentAdapterRegistry, AgentAdapterRegistry>();
        services.AddSingleton<IAgentAdapter, OpenCodeAdapter>();
        services.AddSingleton<IAgentAdapter, PiAdapter>();
        services.AddSingleton<IAgentAdapter, ClaudeCodeAdapter>();
        services.AddSingleton<IAgentAdapter, CopilotAdapter>();
        services.AddSingleton<IAgentAdapter, CodexAdapter>();

        services.AddHttpClient();

        // Remote WorkItem server wiring. The poller is created
        // unconditionally but stays disabled until a RemoteProvider with a
        // WorkItemServerUrl exists, at which point its options snapshot
        // refreshes the next time the host starts.
        services.AddHttpClient<IWorkItemServerClient, WorkItemServerClient>();
        services.AddScoped<ILoopTemplateResolver, DbLoopTemplateResolver>();
        services.AddScoped<IWorkItemServerOptionsResolver, DbWorkItemServerOptionsResolver>();
        services.AddScoped<IRemoteWorkItemCoordinator, RemoteWorkItemCoordinator>();
        services.AddSingleton<RepositoryParallelismHolds>();
        services.AddSingleton<ReadyClaimGate>();
        services.AddSingleton<IConfigureOptions<WorkItemSchedulerOptions>, WorkItemSchedulerOptionsConfigurator>();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IAiProviderConcurrencyTracker, AiProviderConcurrencyTracker>();
        services.AddSingleton<InteractiveProviderSessionService>();
        services.AddSingleton<InteractiveShellSessionService>();
        services.AddScoped<ISchedulerSettingsService, SchedulerSettingsService>();
        services.AddSingleton<WorkItemScheduler>();
        services.AddSingleton<IWorkItemScheduler>(sp => sp.GetRequiredService<WorkItemScheduler>());
        // Order matters: hosted services start sequentially, and the reconciler
        // does its whole job inside StartAsync. Registering it first means the
        // scheduler's first pass derives its active set from runs the reconciler
        // has already settled. The other way round, a pass can heartbeat orphan
        // runs that are about to be cancelled and re-claim an item the server
        // has already reset, leaving it Running on the server behind a cancelled
        // local run until the stale reclaimer catches it ~15 minutes later.
        services.AddHostedService<RemoteWorkItemStartupReconciler>();
        services.AddHostedService(sp => sp.GetRequiredService<WorkItemScheduler>());

        // PR heartbeat poller: refreshes the persisted PR snapshot and fires
        // PR-node custom edges on state transitions while a run is parked at a
        // PR node awaiting merge.
        services.AddScoped<IPrStatusPollService, PrStatusPollService>();

        // The read side of a CI failure: the agent tool that pulls a failing
        // check's log with the forge credentials it does not itself hold.
        services.AddScoped<IPrCiLogService, PrCiLogService>();
        services.AddScoped<PrReviewService>();
        services.AddScoped<IPrReviewService>(sp => sp.GetRequiredService<PrReviewService>());
        services.AddScoped<IPrWriteQueue>(sp => sp.GetRequiredService<PrReviewService>());
        services.AddSingleton<PrStatusPoller>();
        services.AddSingleton<IPrStatusPoller>(sp => sp.GetRequiredService<PrStatusPoller>());
        services.AddHostedService(sp => sp.GetRequiredService<PrStatusPoller>());

        // Graceful shutdown drain. Registered LAST, and the position is
        // load-bearing: hosted services stop in reverse registration order, so
        // last-registered drains first — while the notifier it publishes
        // through, the scopes it opens and the scheduler whose heartbeats hold
        // the work-item claims are all still standing. Moved earlier, the drain
        // would park runs into a half-torn-down process (GracefulShutdownDrainTests
        // holds this line, since a comment cannot).
        services.AddSingleton<IShutdownState, ShutdownState>();
        services.AddSingleton(_ => ShutdownOptions.FromEnvironment());
        // The WorkItem server reads the same two variables and enforces the same
        // numbers; both boundaries check independently.
        services.AddSingleton(_ => ILD.Core.Services.Attachments.AttachmentLimits.FromEnvironment());
        services.AddSingleton(_ => BootstrapCredentials.FromEnvironment());
        services.AddHostedService<GracefulRunDrainService>();

        return services;
    }
}
