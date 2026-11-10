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
    public async Task A_failing_pod_list_keeps_the_last_ranges_and_is_not_retried_by_every_connection()
    {
        var clock = new ILD.Tests.Fakes.ManualTimeProvider();
        var calls = 0;
        var fail = false;
        var source = new ProtectedDestinationSource(
            _services.GetRequiredService<IServiceScopeFactory>(), name => name == "ILD_API_URL" ? "http://ild.example:8080" : null, RunHostKind.Kubernetes, clock,
            (_, _) => Task.FromResult(new[] { IPAddress.Parse("10.0.0.7") }), () => [],
            isolatedRanges: _ =>
            {
                calls++;
                return fail ? throw new InvalidOperationException("the API is throttling") : Task.FromResult<IReadOnlyList<IpRange>>([IpRange.Single(IPAddress.Parse("10.244.1.5"))]);
            });
        var ct = TestContext.Current.CancellationToken;

        Assert.Equal([IpRange.Single(IPAddress.Parse("10.244.1.5"))], await source.GetIsolatedRangesAsync(ct));

        // The cluster fails when the cache has expired: the destinations still load and the last ranges stay in force.
        fail = true;
        clock.Advance(ProtectedDestinationSource.CacheTtl + TimeSpan.FromSeconds(1));
        Assert.Equal([IpRange.Single(IPAddress.Parse("10.244.1.5"))], await source.GetIsolatedRangesAsync(ct));
        Assert.NotEmpty(await source.GetAsync(ct));

        // The failure is cached for the TTL, so more connections do not ask the cluster again.
        var before = calls;
        for (var i = 0; i < 5; i++) await source.GetIsolatedRangesAsync(ct);
        Assert.Equal(before, calls);

        // Once it answers again, the new ranges are used.
        fail = false;
        clock.Advance(ProtectedDestinationSource.CacheTtl + TimeSpan.FromSeconds(1));
        Assert.Single(await source.GetIsolatedRangesAsync(ct));
    }

    [Fact]
    public async Task A_resolver_that_fails_keeps_what_the_host_last_resolved_to()
    {
        var clock = new ILD.Tests.Fakes.ManualTimeProvider();
        var fail = false;
        var source = new ProtectedDestinationSource(
            _services.GetRequiredService<IServiceScopeFactory>(), name => name == "ILD_DB_CONNECTION_STRING" ? "Host=db.example" : null, RunHostKind.Container, clock,
            (_, _) => fail ? throw new InvalidOperationException("resolver down") : Task.FromResult(new[] { IPAddress.Parse("10.0.0.7") }), () => []);
        var ct = TestContext.Current.CancellationToken;
        Assert.Equal([IPAddress.Parse("10.0.0.7")], (await source.GetAsync(ct)).Single(e => e.Id == ProtectedDestination.Database).Addresses);

        fail = true;
        clock.Advance(ProtectedDestinationSource.CacheTtl + TimeSpan.FromSeconds(1));

        Assert.Equal([IPAddress.Parse("10.0.0.7")], (await source.GetAsync(ct)).Single(e => e.Id == ProtectedDestination.Database).Addresses);
    }

    [Fact]
    public async Task The_kubernetes_api_entry_covers_the_endpoints_of_the_kubernetes_service_and_keeps_them_when_they_cannot_be_read()
    {
        var clock = new ILD.Tests.Fakes.ManualTimeProvider();
        var readable = true;
        var source = new ProtectedDestinationSource(
            _services.GetRequiredService<IServiceScopeFactory>(), name => name == "KUBERNETES_SERVICE_HOST" ? "10.96.0.1" : null, RunHostKind.Kubernetes, clock,
            (_, _) => Task.FromResult(new[] { IPAddress.Parse("10.96.0.1") }), () => [],
            controlPlane: _ => readable ? Task.FromResult<IReadOnlyList<IPAddress>>([IPAddress.Parse("192.168.1.10")]) : throw new InvalidOperationException("forbidden"));
        var ct = TestContext.Current.CancellationToken;

        var api = (await source.GetAsync(ct)).Single(e => e.Id == ProtectedDestination.KubernetesApi);
        Assert.True(api.Covers(IPAddress.Parse("10.96.0.1")));
        Assert.True(api.Covers(IPAddress.Parse("192.168.1.10")));

        readable = false;
        clock.Advance(ProtectedDestinationSource.CacheTtl + TimeSpan.FromSeconds(1));

        api = (await source.GetAsync(ct)).Single(e => e.Id == ProtectedDestination.KubernetesApi);
        Assert.True(api.Covers(IPAddress.Parse("10.96.0.1")));
        Assert.True(api.Covers(IPAddress.Parse("192.168.1.10")));
    }

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

        Assert.Equal([ProtectedDestination.Ild, ProtectedDestination.LinkLocal, ProtectedDestination.Private], entries.Select(e => e.Id));
    }

    [Theory]
    [InlineData(RunHostKind.Container, true)]
    [InlineData(RunHostKind.Kubernetes, true)]
    [InlineData(RunHostKind.Local, false)]
    public async Task The_private_ranges_are_protected_on_isolated_run_hosts_only_and_listed_last(RunHostKind kind, bool present)
    {
        var entries = await Source(kind, new() { ["ILD_API_URL"] = "http://ild.example:8080" }).GetAsync(TestContext.Current.CancellationToken);

        Assert.Equal(present, entries.Any(e => e.Id == ProtectedDestination.Private));
        if (present) Assert.Equal(ProtectedDestination.Private, entries[^1].Id);
        Assert.All(entries.Where(e => e.Id == ProtectedDestination.Private), e => Assert.True(e.Covers(IPAddress.Parse("::ffff:10.1.2.3").MapToIPv4()) && e.Covers(IPAddress.Parse("fd12::1"))));
    }

    [Fact]
    public async Task With_containers_the_machine_ilds_container_runs_on_is_protected_at_the_addresses_it_is_reached_by()
    {
        var source = new ProtectedDestinationSource(
            _services.GetRequiredService<IServiceScopeFactory>(), _ => null, RunHostKind.Container, TimeProvider.System,
            resolve: (_, _) => Task.FromResult<IPAddress[]>([]), ownAddresses: () => [IPAddress.Parse("172.20.0.4")],
            hostAddresses: () => [IPAddress.Parse("172.20.0.1"), IPAddress.Parse("192.168.65.254"), IPAddress.Parse("172.20.0.4")]);

        var host = (await source.GetAsync(TestContext.Current.CancellationToken)).Single(e => e.Id == ProtectedDestination.Host);

        Assert.Equal([IPAddress.Parse("172.20.0.1"), IPAddress.Parse("192.168.65.254")], host.Addresses);
        Assert.True(host.Enabled);
    }

    [Theory]
    [InlineData("172.20.0.4", 16, "172.20.0.1")]
    [InlineData("10.213.0.18", 28, "10.213.0.17")]
    [InlineData("10.0.0.5", 32, null)]
    [InlineData("10.0.0.5", 31, null)]
    public void A_bridges_gateway_is_the_first_address_of_the_subnet(string address, int prefix, string? expected)
    {
        Assert.Equal(expected is null ? null : IPAddress.Parse(expected), ProtectedDestinationSource.FirstOfSubnet(IPAddress.Parse(address), prefix));
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
