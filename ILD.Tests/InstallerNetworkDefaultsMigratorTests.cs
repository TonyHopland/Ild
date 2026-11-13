using ILD.Core.Services.Implementations.Network;
using ILD.Data.Entities;
using ILD.Data.Enums;
using ILD.Data.Migrations;
using Microsoft.EntityFrameworkCore;

namespace ILD.Tests;

public class InstallerNetworkDefaultsMigratorTests
{
    [Fact]
    public async Task The_defaults_are_installers_whitelist_entries_and_every_host_is_a_valid_pattern()
    {
        using var db = new TestDb();

        var added = await InstallerNetworkDefaultsMigrator.MigrateAsync(db.Context, TestContext.Current.CancellationToken);

        var entries = await db.Context.NetworkPolicyEntries.ToListAsync(TestContext.Current.CancellationToken);
        Assert.Equal(InstallerNetworkDefaultsMigrator.DefaultHosts.Count, added);
        Assert.All(entries, e =>
        {
            Assert.True(e.Installers);
            Assert.Equal(NetworkListKind.Whitelist, e.ListKind);
            Assert.Null(e.AiProviderId);
        });
        Assert.All(InstallerNetworkDefaultsMigrator.DefaultHosts, host =>
        {
            Assert.True(EgressRules.TryNormalizePattern(host, out var pattern, out _), host);
            Assert.Equal(host, pattern);
        });
        foreach (var host in new[] { "registry.npmjs.org", "pypi.org", "files.pythonhosted.org", "crates.io", "static.crates.io", "api.nuget.org", "github.com", "nodejs.org" })
            Assert.Contains(entries, e => EgressRules.Matches(e.Host, host));
        Assert.Contains(entries, e => EgressRules.Matches(e.Host, "objects.githubusercontent.com"));
    }

    [Fact]
    public async Task It_runs_once_so_a_default_the_operator_deleted_stays_deleted()
    {
        using var db = new TestDb();
        var ct = TestContext.Current.CancellationToken;
        await InstallerNetworkDefaultsMigrator.MigrateAsync(db.Context, ct);
        await db.Context.NetworkPolicyEntries.Where(e => e.Host == "crates.io").ExecuteDeleteAsync(ct);

        var again = await InstallerNetworkDefaultsMigrator.MigrateAsync(db.Context, ct);

        Assert.Equal(0, again);
        Assert.DoesNotContain(await db.Context.NetworkPolicyEntries.ToListAsync(ct), e => e.Host == "crates.io");
    }

    [Fact]
    public async Task An_entry_the_operator_already_made_is_not_duplicated()
    {
        using var db = new TestDb();
        var ct = TestContext.Current.CancellationToken;
        db.Context.NetworkPolicyEntries.Add(new NetworkPolicyEntry { Id = Guid.NewGuid(), Host = "pypi.org", ListKind = NetworkListKind.Whitelist, Installers = true });
        await db.Context.SaveChangesAsync(ct);

        await InstallerNetworkDefaultsMigrator.MigrateAsync(db.Context, ct);

        Assert.Single(await db.Context.NetworkPolicyEntries.Where(e => e.Host == "pypi.org").ToListAsync(ct));
    }
}
