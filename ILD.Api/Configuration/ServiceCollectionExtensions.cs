using ILD.Core.Services.Interfaces;
using ILD.Core.Services.Implementations;
using ILD.Core.Services.Implementations.Executors;
using ILD.Core.Services.Implementations.Adapters;
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
        services.AddSingleton<AuthOptions>(_ => new AuthOptions());
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
        services.AddSingleton<IRemoteProviderTypeCatalog, RemoteProviderTypeCatalog>();
        services.AddScoped<IRemoteProvider, RemoteProviderService>();
        services.AddHttpClient<IAIProviderService, AIProviderService>();
        services.AddSingleton<IWorktreePreviewService, WorktreePreviewService>();
        services.AddSingleton<EventLogOptions>();
        services.AddScoped<IEventLogService, EventLogService>();
        services.AddHostedService<EventLogRetentionSweeper>();
        services.AddHostedService<WorktreeRetentionSweeper>();
        services.AddScoped<IRunReclaimer, RunReclaimer>();
        services.AddScoped<IRecoveryManager, RecoveryManager>();
        services.AddScoped<IPrSyncService, PrSyncService>();
        services.AddScoped<IAdapterSessionSnapshotStore, AdapterSessionSnapshotStore>();

        services.AddSingleton<IRunNotifier, SignalRRunNotifier>();
        services.AddSingleton<IWorkItemNotifier, SignalRWorkItemNotifier>();
        services.AddSingleton<INodeExecutor, StartNodeExecutor>();
        services.AddSingleton<INodeExecutor, CmdNodeExecutor>();
        services.AddSingleton<INodeExecutor, AINodeExecutor>();
        services.AddSingleton<INodeExecutor, HumanNodeExecutor>();
        services.AddSingleton<INodeExecutor, PromptNodeExecutor>();
        services.AddSingleton<INodeExecutor, PRNodeExecutor>();
        services.AddSingleton<INodeExecutor, CleanupNodeExecutor>();
        services.AddSingleton<INodeExecutorRegistry, NodeExecutorRegistry>();
        services.AddSingleton<ILoopEngine, LoopEngine>();
        services.AddScoped<IMetricsCollector, MetricsCollector>();
        services.AddSingleton<IAgentAdapterRegistry, AgentAdapterRegistry>();
        services.AddSingleton<IAgentAdapter, OpenCodeAdapter>();
        services.AddSingleton<IAgentAdapter, PiAdapter>();
        services.AddSingleton<IAgentAdapter, ClaudeCodeAdapter>();

        services.AddHttpClient();

        // Remote WorkItem server wiring. The poller is created
        // unconditionally but stays disabled until a RemoteProvider with a
        // WorkItemServerUrl exists, at which point its options snapshot
        // refreshes the next time the host starts.
        services.AddHttpClient<IWorkItemServerClient, WorkItemServerClient>();
        services.AddSingleton<IActiveWorkItemTracker, InMemoryActiveWorkItemTracker>();
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
        services.AddHostedService(sp => sp.GetRequiredService<WorkItemScheduler>());
        services.AddHostedService<RemoteWorkItemStartupReconciler>();

        return services;
    }
}
