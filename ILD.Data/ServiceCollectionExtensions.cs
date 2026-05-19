using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ILD.Data.Entities;
using ILD.Data.Stores.Interfaces;

namespace ILD.Data;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddDataLayer(this IServiceCollection services, Action<DbContextOptionsBuilder> configure)
    {
        services.AddDbContext<AppDbContext>(configure);
        services.AddDataStores();
        return services;
    }

    public static IServiceCollection AddDataStores(this IServiceCollection services)
    {
        services.AddScoped<ILoopRunStore, Stores.LoopRunStore>();
        services.AddScoped<ILoopTemplateStore, Stores.LoopTemplateStore>();
        services.AddScoped<IEventLogStore, Stores.EventLogStore>();
        services.AddScoped<IAuthStore, Stores.AuthStore>();
        services.AddScoped<IProviderStore, Stores.ProviderStore>();
        services.AddScoped<IAppSettingStore, Stores.AppSettingStore>();
        return services;
    }
}
