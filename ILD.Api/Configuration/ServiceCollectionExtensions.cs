using ILD.Core.Services.Interfaces;
using ILD.Core.Services.Implementations;

namespace ILD.Api.Configuration;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddIldServices(this IServiceCollection services)
    {
        services.AddSingleton<IAuthService, AuthService>();
        services.AddSingleton<IWorkItemManager, WorkItemManager>();
        services.AddSingleton<ILoopTemplateManager, LoopTemplateManager>();
        services.AddSingleton<ILoopEngine, LoopEngine>();
        services.AddSingleton<IRepositoryManager, RepositoryManager>();
        services.AddSingleton<IRemoteProvider, RemoteProvider>();
        services.AddSingleton<IAIProviderService, AIProviderService>();
        services.AddSingleton<IEventLogService, EventLogService>();
        services.AddSingleton<IRecoveryManager, RecoveryManager>();
        services.AddSingleton<IPrSyncService, PrSyncService>();

        return services;
    }
}
