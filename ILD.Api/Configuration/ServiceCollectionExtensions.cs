using ILD.Core.Services.Interfaces;
using ILD.Core.Services.Implementations;
using ILD.Core.Services.Implementations.Executors;
using ILD.Core.Models;
using ILD.Api.Middleware;

namespace ILD.Api.Configuration;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddIldServices(this IServiceCollection services)
    {
        services.AddSingleton<AuthOptions>(_ => new AuthOptions());

        services.AddScoped<IAuthService, AuthService>();
        services.AddScoped<IWorkItemManager, WorkItemManager>();
        services.AddScoped<ILoopTemplateManager, LoopTemplateManager>();
        services.AddSingleton<IRepositoryManager, RepositoryManager>();
        services.AddScoped<IRemoteProvider, ILD.Core.Services.Implementations.RemoteProvider>();
        services.AddScoped<IAIProviderService, AIProviderService>();
        services.AddScoped<IEventLogService, EventLogService>();
        services.AddScoped<IRecoveryManager, RecoveryManager>();
        services.AddScoped<IPrSyncService, PrSyncService>();

        // Engine + node executors are singletons; they receive a Func<AppDbContext>
        // so they can create their own scope per operation.
        services.AddSingleton<Func<AppDbContext>>(sp => () =>
        {
            var scope = sp.CreateScope();
            return scope.ServiceProvider.GetRequiredService<AppDbContext>();
        });

        services.AddSingleton<IRunNotifier, SignalRRunNotifier>();
        services.AddSingleton<INodeExecutor, StartNodeExecutor>();
        services.AddSingleton<INodeExecutor, CmdNodeExecutor>();
        services.AddSingleton<INodeExecutor, AINodeExecutor>();
        services.AddSingleton<INodeExecutor, HumanNodeExecutor>();
        services.AddSingleton<INodeExecutor, PRNodeExecutor>();
        services.AddSingleton<INodeExecutor, CleanupNodeExecutor>();
        services.AddSingleton<INodeExecutorRegistry, NodeExecutorRegistry>();
        services.AddSingleton<ILoopEngine, LoopEngine>();

        services.AddHttpClient();

        return services;
    }
}
