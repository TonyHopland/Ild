using System.Net;
using ILD.Core.Services.Implementations.Network;
using ILD.Core.Services.Interfaces;
using ILD.Core.Services.RunHosts;
using ILD.Data.Stores;
using ILD.Data.Stores.Interfaces;
using Microsoft.Extensions.DependencyInjection;

namespace ILD.Tests;

public sealed class ProtectedDestinationSourceTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly ServiceProvider _services;

    public ProtectedDestinationSourceTests()
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => _db.OnOwnConnection());
        services.AddScoped<IAppSettingStore, AppSettingStore>();
        _services = services.BuildServiceProvider();
    }

    public void Dispose()
    {
        _services.Dispose();
        _db.Dispose();
    }

    private ProtectedDestinationSource Source(RunHostKind kind, Dictionary<string, string> environment)
        => new(_services.GetRequiredService<IServiceScopeFactory>(), name => environment.GetValueOrDefault(name), kind, TimeProvider.System,
            (host, _) => Task.FromResult(new[] { IPAddress.Parse(host == "kubernetes.default.svc" ? "10.96.0.1" : "10.0.0.7") }),
            () => [IPAddress.Parse("10.0.0.99")]);

    [Fact]
    public async Task Each_entry_is_derived_from_the_configuration_ILD_already_has()
    {
        await _db.Settings.UpsertAsync(AppSettingKeys.WorkItemServerUrl, "http://workitems.example:5000/", TestContext.Current.CancellationToken);
        var source = Source(RunHostKind.Kubernetes, new()
        {
            ["ILD_API_URL"] = "http://ild.example:8080",
            ["ILD_DB_CONNECTION_STRING"] = "Host=db.example;Port=5432;Database=IldCore;Username=ild",
            ["KUBERNETES_SERVICE_HOST"] = "10.96.0.1",
        });

        var entries = (await source.GetAsync(TestContext.Current.CancellationToken)).ToDictionary(e => e.Id);

        Assert.Equal(["ild.example"], entries[ProtectedDestination.Ild].Hosts);
        Assert.Contains(IPAddress.Parse("10.0.0.99"), entries[ProtectedDestination.Ild].Addresses);
        Assert.Equal(["db.example"], entries[ProtectedDestination.Database].Hosts);
        Assert.Equal(["workitems.example"], entries[ProtectedDestination.WorkItemServer].Hosts);
        Assert.Equal(["10.96.0.1", "kubernetes.default.svc"], entries[ProtectedDestination.KubernetesApi].Hosts);
        Assert.All(entries.Values, e => Assert.True(e.Enabled));
    }

    [Fact]
    public async Task Outside_kubernetes_there_is_no_api_entry_and_unset_configuration_adds_none()
    {
        var source = Source(RunHostKind.Container, new() { ["ILD_API_URL"] = "http://ild.example:8080", ["KUBERNETES_SERVICE_HOST"] = "10.96.0.1" });

        var entries = await source.GetAsync(TestContext.Current.CancellationToken);

        Assert.Equal([ProtectedDestination.Ild, ProtectedDestination.LinkLocal], entries.Select(e => e.Id));
    }

    [Fact]
    public void The_guard_matches_an_ipv4_mapped_address_against_the_protected_one()
    {
        var destination = new ProtectedDestination(ProtectedDestination.Database, "ILD database", ["db"], [IPAddress.Parse("10.0.0.7")]);

        var refusal = EgressDestinationGuard.Check([IPAddress.Parse("::ffff:10.0.0.7")], [destination], exemptFromProtections: false);

        Assert.Equal("protected: ILD database", refusal?.Reason);
        Assert.Null(EgressDestinationGuard.Check([IPAddress.Parse("::ffff:10.0.0.7")], [destination], exemptFromProtections: true));
    }
}
