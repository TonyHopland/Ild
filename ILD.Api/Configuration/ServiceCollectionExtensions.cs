using ILD.Core.Services.Interfaces;
using ILD.Core.Services.Implementations;
using ILD.Core.Services.Implementations.Executors;
using ILD.Core.Services.Implementations.Adapters;
using ILD.Core.Services.Implementations.Network;
using ILD.Core.Services.Implementations.RemoteProviders;
using ILD.Core.Services.Remote;
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
    public static IServiceCollection AddIldServices(this IServiceCollection services)
    {
        services.AddSingleton<AgentAuthTokenProvider>();

        services.AddScoped<IAuthService, AuthService>();
        services.AddScoped<IWorkItemManager, WorkItemManager>();
        services.AddScoped<ILoopTemplateManager, LoopTemplateManager>();
        services.AddSingleton<IRepositoryManager>(sp =>
        {
            var runner = sp.GetRequiredService<IProcessRunner>();
            var logger = sp.GetService<ILogger<RepositoryManager>>();
            var config = sp.GetRequiredService<IConfiguration>();
            var worktreesRoot = config["App:WorktreesPath"];

            return new RepositoryManager(
                runner,
                logger,
                string.IsNullOrWhiteSpace(worktreesRoot) ? null : worktreesRoot);
        });
        services.AddSingleton<IPromptTemplateResolver, PromptTemplateResolver>();
        services.AddScoped<IPromptRenderingService, PromptRenderingService>();
        services.AddSingleton<IProcessRunner, ProcessRunner>();
        services.AddSingleton<IRemoteGitProviderAdapter, ForgejoRemoteGitProviderAdapter>();
        services.AddSingleton<IRemoteGitProviderAdapter, GitHubRemoteGitProviderAdapter>();
        services.AddSingleton<IRemoteGitProviderAdapter, AzureDevOpsRemoteGitProviderAdapter>();
        services.AddSingleton<IRemoteProviderTypeCatalog, RemoteProviderTypeCatalog>();
        services.AddScoped<IRemoteProvider, RemoteProviderService>();
        services.AddHttpClient<IAIProviderService, AIProviderService>();
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
        services.AddSingleton<IWorktreePreviewService, WorktreePreviewService>();
        services.AddSingleton<EventLogOptions>();
        services.AddScoped<IEventLogService, EventLogService>();
        services.AddHostedService<EventLogRetentionSweeper>();
        services.AddHostedService<WorktreeRetentionSweeper>();
        services.AddHostedService<StuckRunWatchdog>();
        services.AddScoped<IRunReclaimer, RunReclaimer>();
        services.AddScoped<IBranchNameOverrideService, BranchNameOverrideService>();
        services.AddScoped<IRecoveryManager, RecoveryManager>();
        services.AddScoped<IPrSyncService, PrSyncService>();
        services.AddScoped<IAdapterSessionSnapshotStore, AdapterSessionSnapshotStore>();

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
        services.AddSingleton<IChatNotifier, SignalRChatNotifier>();
        // The loop scratchpad relays the open Loop Editor's live document from the
        // chat turn to the agent-scoped API (ADR-0011); a singleton so both scopes
        // share the same in-memory store.
        services.AddSingleton<IChatLoopScratchpad, ChatLoopScratchpad>();

        services.AddSingleton<IRunProgressBuffer, RunProgressBuffer>();
        services.AddSingleton<IRunNotifier, SignalRRunNotifier>();
        services.AddSingleton<IWorkItemNotifier, SignalRWorkItemNotifier>();

        // Agent egress filter (ADR-0019): the proxy every agent launch is pointed
        // at, the cached policy it consults, the recorder that persists what it
        // saw, and the enforcement status the entrypoint reported. All keyed off
        // ILD_NETWORK_PROXY_PORT; unset, nothing listens and launches are untouched.
        services.AddSingleton(_ => EgressProxyOptions.FromEnvironment());
        services.AddSingleton(sp => NetworkEnforcementStatus.FromEnvironment(sp.GetRequiredService<EgressProxyOptions>()));
        services.AddSingleton<IEgressPolicy, EgressPolicy>();
        services.AddSingleton<INetworkNotifier, SignalRNetworkNotifier>();
        services.AddSingleton<NetworkLogRecorder>();
        services.AddSingleton<INetworkLogRecorder>(sp => sp.GetRequiredService<NetworkLogRecorder>());
        services.AddHostedService(sp => sp.GetRequiredService<NetworkLogRecorder>());
        services.AddHostedService<EgressProxy>();
        services.AddSingleton<INodeExecutor, StartNodeExecutor>();
        services.AddSingleton<INodeExecutor, CmdNodeExecutor>();
        services.AddSingleton<INodeExecutor, AINodeExecutor>();
        services.AddSingleton<INodeExecutor, HumanNodeExecutor>();
        services.AddSingleton<INodeExecutor, PromptNodeExecutor>();
        services.AddSingleton<INodeExecutor, PRNodeExecutor>();
        services.AddSingleton<INodeExecutor, ConditionNodeExecutor>();
        services.AddSingleton<INodeExecutor, CleanupNodeExecutor>();
        services.AddSingleton<INodeExecutorRegistry, NodeExecutorRegistry>();
        services.AddSingleton<ILoopEngine, LoopEngine>();
        services.AddScoped<IMetricsCollector, MetricsCollector>();
        services.AddScoped<IRunAnalyticsService, RunAnalyticsService>();
        services.AddSingleton<IAgentAdapterRegistry, AgentAdapterRegistry>();
        services.AddSingleton<IAgentAdapter, OpenCodeAdapter>();
        services.AddSingleton<IAgentAdapter, PiAdapter>();
        services.AddSingleton<IAgentAdapter, ClaudeCodeAdapter>();
        services.AddSingleton<IAgentAdapter, CopilotAdapter>();

        services.AddHttpClient();

        // Remote WorkItem server wiring. The poller is created
        // unconditionally but stays disabled until a RemoteProvider with a
        // WorkItemServerUrl exists, at which point its options snapshot
        // refreshes the next time the host starts.
        services.AddHttpClient<IWorkItemServerClient, WorkItemServerClient>();
        services.AddScoped<ILoopTemplateResolver, DbLoopTemplateResolver>();
        services.AddScoped<IWorkItemServerOptionsResolver, DbWorkItemServerOptionsResolver>();
        services.AddScoped<IRemoteWorkItemCoordinator, RemoteWorkItemCoordinator>();
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
        services.AddHostedService<GracefulRunDrainService>();

        return services;
    }
}
