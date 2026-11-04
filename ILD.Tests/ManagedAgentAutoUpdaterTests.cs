using System.Net;
using System.Text;
using ILD.Core.Services.Implementations;
using ILD.Core.Services.Implementations.Adapters;
using ILD.Core.Services.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ILD.Tests;

/// <summary>
/// One wake of the managed-agent updater, driven against the real
/// <see cref="ManagedAgentService"/> with a fake npm and a fake registry: the
/// cooldown-gated installs that swap at once, the check schedule, the idle cleanup
/// of old installs and the hold a rollback leaves.
/// </summary>
[Collection(ManagedAgentInstallsCollection.Name)]
public sealed class ManagedAgentAutoUpdaterTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 6, 30, 12, 0, 0, TimeSpan.Zero);
    private static readonly ManagedAgent Pi = ManagedAgentCatalog.Pi;
    private static readonly ManagedAgent OpenCode = ManagedAgentCatalog.OpenCode;

    private readonly string _dataRoot = Path.Combine(Path.GetTempPath(), "ild-managed-updater-" + Guid.NewGuid().ToString("N"));
    private readonly IManagedAgentUsage _usage = new ManagedAgentUsage();
    private readonly FakeNpm _npm = new();
    private readonly FakeRegistry _registry = new();
    private readonly AutoInstallForecast _forecast = new();
    private readonly ManagedAgentService _service;
    private ManagedAgentAutoUpdater _updater;
    private TimeSpan _wake = TimeSpan.Zero;

    public ManagedAgentAutoUpdaterTests()
    {
        Directory.CreateDirectory(_dataRoot);
        _service = new ManagedAgentService(new HttpClient(_registry), _npm, _dataRoot, logger: null, agentUser: null, usage: _usage, forecast: _forecast);
        _updater = BuildUpdater(0);
    }

    /// <summary>A fresh updater with its own check history, for the given cooldown.</summary>
    private ManagedAgentAutoUpdater BuildUpdater(int cooldownDays)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IManagedAgentService>(_service);
        services.AddSingleton(new AgentUpdateOptions(cooldownDays));
        services.AddSingleton(_forecast);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        return ActivatorUtilities.CreateInstance<ManagedAgentAutoUpdater>(services.BuildServiceProvider());
    }

    private void UseCooldown(int cooldownDays) => _updater = BuildUpdater(cooldownDays);

    public void Dispose()
    {
        try { Directory.Delete(_dataRoot, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>npm install and each agent's <c>--version</c>, keyed by binary name.</summary>
    private sealed class FakeNpm : IProcessRunner
    {
        public Dictionary<string, string> Installed { get; } = new();
        private readonly Dictionary<string, string> _versionByPrefix = new();
        public HashSet<string> FailingPackages { get; } = new();
        public Action<string>? OnInstall;
        public List<string> Specs { get; } = new();
        public int VersionProbes;

        /// <summary>What <c>npm install pkg@latest</c> installs.</summary>
        public string LatestVersion { get; set; } = "99.0.0";

        public Task<ProcessResult> RunAsync(
            string fileName,
            IReadOnlyList<string> args,
            string? workingDirectory = null,
            CancellationToken ct = default,
            IReadOnlyDictionary<string, string?>? environmentVariables = null,
            ProcessRunOptions? options = null)
        {
            ct.ThrowIfCancellationRequested();
            if (args.Count > 0 && args[0] == "install")
            {
                var spec = args[1];
                Specs.Add(spec);
                var at = spec.LastIndexOf('@');
                var package = spec[..at];
                OnInstall?.Invoke(package);
                ct.ThrowIfCancellationRequested();
                if (FailingPackages.Contains(package))
                    return Task.FromResult(new ProcessResult(1, "", "npm ERR! boom"));

                var agent = ManagedAgentCatalog.All.Single(a => a.NpmPackage == package);
                var prefix = args[args.ToList().IndexOf("--prefix") + 1];
                var binDir = Path.Combine(prefix, "node_modules", ".bin");
                Directory.CreateDirectory(binDir);
                File.WriteAllText(Path.Combine(binDir, agent.BinaryName), "#!/bin/sh\n");
                var installedVersion = spec[(at + 1)..] == "latest" ? LatestVersion : spec[(at + 1)..];
                _versionByPrefix[prefix] = installedVersion;
                var manifestDir = Path.Combine(prefix, "node_modules", package);
                Directory.CreateDirectory(manifestDir);
                File.WriteAllText(Path.Combine(manifestDir, "package.json"), "{\"version\":\"" + installedVersion + "\"}");
                return Task.FromResult(new ProcessResult(0, "added 1 package", ""));
            }

            Interlocked.Increment(ref VersionProbes);
            // A binary under an install dir reports what was installed there, so a
            // discarded install never changes what the active one reports.
            var installDir = fileName.Contains(Path.Combine("node_modules", ".bin"), StringComparison.Ordinal)
                ? fileName[..fileName.IndexOf(Path.Combine("node_modules", ".bin"), StringComparison.Ordinal)].TrimEnd(Path.DirectorySeparatorChar)
                : null;
            var version = installDir is not null && _versionByPrefix.TryGetValue(installDir, out var installed)
                ? installed
                : Installed.GetValueOrDefault(Path.GetFileName(fileName));
            return Task.FromResult(version is not null
                ? new ProcessResult(0, version + "\n", "")
                : new ProcessResult(127, "", "command not found"));
        }
    }

    private sealed class FakeRegistry : HttpMessageHandler
    {
        public Dictionary<string, (string Latest, string Packument)> Packages { get; } = new();
        public HashSet<string> Unreachable { get; } = new();
        public int Requests;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref Requests);
            var path = Uri.UnescapeDataString(request.RequestUri!.AbsolutePath).TrimStart('/');
            var wantsLatest = path.EndsWith("/latest", StringComparison.Ordinal);
            var package = wantsLatest ? path[..^"/latest".Length] : path;
            if (Unreachable.Contains(package) || !Packages.TryGetValue(package, out var entry))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            var body = wantsLatest ? "{\"version\":\"" + entry.Latest + "\"}" : entry.Packument;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }

    private static string Iso(TimeSpan age) => (Now - age).ToString("yyyy-MM-ddTHH:mm:ss.fffZ");

    private void Publish(ManagedAgent agent, string latest, params (string Version, double AgeDays)[] releases)
    {
        var time = string.Join(",", releases.Select(r => $"\"{r.Version}\":\"{Iso(TimeSpan.FromDays(r.AgeDays))}\""));
        var versions = string.Join(",", releases.Select(r => $"\"{r.Version}\":{{}}"));
        var packument = "{\"dist-tags\":{\"latest\":\"" + latest + "\"},\"versions\":{" + versions + "},\"time\":{\"created\":\"" + Iso(TimeSpan.FromDays(900))
            + "\",\"modified\":\"" + Iso(TimeSpan.FromHours(1)) + "\"," + time + "}}";
        _registry.Packages[agent.NpmPackage] = (latest, packument);
    }

    /// <summary>One wake, an hour after the previous one, as the hosted service wakes.</summary>
    private Task PassAsync(CancellationToken? ct = null) => PassAtAsync(_wake + TimeSpan.FromHours(1), ct);

    private Task PassAtAsync(TimeSpan sinceStart, CancellationToken? ct = null)
    {
        _wake = sinceStart;
        return _updater.RunOnceAsync(Now + sinceStart, ct ?? TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
    }

    private string? Active(ManagedAgent agent) => ManagedAgentInstall.CurrentVersionId(_dataRoot, agent);

    [Fact]
    public async Task An_agent_that_is_up_to_date_or_not_installed_is_left_alone()
    {
        _npm.Installed[Pi.BinaryName] = "1.1.0";
        Publish(Pi, "1.1.0", ("1.1.0", 30));
        Publish(OpenCode, "3.0.0", ("3.0.0", 30));

        await PassAsync();

        Assert.Empty(_npm.Specs);
    }

    [Fact]
    public async Task An_installed_agent_gets_the_newest_version_past_the_cooldown_exactly()
    {
        UseCooldown(7);
        _npm.Installed[Pi.BinaryName] = "1.0.0";
        Publish(Pi, "2.0.0", ("1.0.0", 60), ("1.6.0", 10), ("1.9.0", 3), ("2.0.0", 1));

        await PassAsync();

        Assert.Equal([$"{Pi.NpmPackage}@1.6.0"], _npm.Specs);
        Assert.NotNull(Active(Pi));
    }

    [Fact]
    public async Task A_version_that_is_not_newer_than_the_installed_one_is_not_installed()
    {
        UseCooldown(7);
        _npm.Installed[Pi.BinaryName] = "1.6.0";
        Publish(Pi, "2.0.0", ("1.6.0", 10), ("2.0.0", 1));

        await PassAsync();

        Assert.Empty(_npm.Specs);
    }

    [Fact]
    public async Task Missing_agents_are_never_installed_by_the_pass()
    {
        UseCooldown(0);
        foreach (var agent in ManagedAgentCatalog.All)
            Publish(agent, "2.0.0", ("2.0.0", 30));

        await PassAsync();

        Assert.Empty(_npm.Specs);
    }

    [Fact]
    public async Task Wakes_before_the_next_check_is_due_make_no_registry_call_and_start_no_probe()
    {
        _npm.Installed[Pi.BinaryName] = "1.0.0";
        Publish(Pi, "1.1.0", ("1.1.0", 0.1));

        await PassAtAsync(TimeSpan.Zero);
        var requests = _registry.Requests;
        var probes = _npm.VersionProbes;
        Assert.True(requests > 0 && probes > 0);

        await PassAtAsync(TimeSpan.FromMinutes(59));
        Assert.Equal(requests, _registry.Requests);
        Assert.Equal(probes, _npm.VersionProbes);

        await PassAtAsync(TimeSpan.FromHours(1));
        Assert.True(_registry.Requests > requests);
    }

    [Fact]
    public async Task A_failing_agent_does_not_stop_the_pass_and_is_retried_next_time()
    {
        UseCooldown(0);
        _npm.Installed[Pi.BinaryName] = "1.0.0";
        _npm.Installed[OpenCode.BinaryName] = "1.0.0";
        Publish(Pi, "2.0.0", ("2.0.0", 5));
        Publish(OpenCode, "2.0.0", ("2.0.0", 5));
        _npm.FailingPackages.Add(Pi.NpmPackage);

        await PassAsync();

        Assert.Null(Active(Pi));
        Assert.NotNull(Active(OpenCode));

        _npm.FailingPackages.Clear();
        await PassAsync();
        Assert.NotNull(Active(Pi));
    }

    [Fact]
    public async Task An_unreadable_packument_leaves_the_agent_alone()
    {
        UseCooldown(0);
        _npm.Installed[Pi.BinaryName] = "1.0.0";
        _registry.Packages[Pi.NpmPackage] = ("2.0.0", "{\"dist-tags\":{\"latest\":\"2.0.0\"}}");

        await PassAsync();

        Assert.Empty(_npm.Specs);
    }

    [Fact]
    public async Task Shutdown_during_an_install_cancels_the_pass_and_leaves_no_half_swapped_install()
    {
        UseCooldown(0);
        _npm.Installed[Pi.BinaryName] = "1.0.0";
        Publish(Pi, "2.0.0", ("2.0.0", 5));
        using var cts = new CancellationTokenSource();
        _npm.OnInstall = _ => cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PassAsync(cts.Token));

        Assert.Null(Active(Pi));
        var versions = ManagedAgentInstall.VersionsRoot(_dataRoot, Pi);
        Assert.True(!Directory.Exists(versions) || Directory.GetDirectories(versions).Length == 0);
    }

    [Fact]
    public async Task An_update_is_never_installed_over_a_version_that_is_already_that_new_or_newer()
    {
        _npm.Installed[Pi.BinaryName] = "1.7.0";

        var result = await _service.AutoUpdateAsync(Pi.Key, "1.6.0", TestContext.Current.CancellationToken);

        Assert.Equal(AutoUpdateResult.NotNewer, result);
        Assert.Empty(_npm.Specs);
    }


    [Fact]
    public async Task A_cooldown_of_zero_takes_the_current_latest_and_a_long_one_takes_nothing()
    {
        UseCooldown(365);
        _npm.Installed[Pi.BinaryName] = "1.0.0";
        Publish(Pi, "2.0.0", ("1.0.0", 60), ("2.0.0", 1));
        await PassAsync();
        Assert.Empty(_npm.Specs);

        UseCooldown(0);
        await PassAsync();
        Assert.Equal([$"{Pi.NpmPackage}@2.0.0"], _npm.Specs);
    }

    [Theory]
    [InlineData(7, 23, 24)]
    [InlineData(3, 10, 11)]
    [InlineData(1, 0.9, 1)]
    [InlineData(0, 0.9, 1)]
    [InlineData(365, 23, 24)]
    public async Task The_next_check_is_due_after_a_cooldown_scaled_interval(int cooldownDays, double notDueHours, double dueHours)
    {
        UseCooldown(cooldownDays);
        _npm.Installed[Pi.BinaryName] = "1.0.0";
        Publish(Pi, "1.1.0", ("1.0.0", 60), ("1.1.0", 0.1));

        await PassAtAsync(TimeSpan.Zero);
        var requests = _registry.Requests;

        await PassAtAsync(TimeSpan.FromHours(notDueHours));
        Assert.Equal(requests, _registry.Requests);

        await PassAtAsync(TimeSpan.FromHours(dueHours));
        Assert.True(_registry.Requests > requests);
    }

    [Fact]
    public async Task The_status_names_the_version_that_installs_automatically_and_when()
    {
        UseCooldown(7);
        _npm.Installed[Pi.BinaryName] = "1.0.0";
        Publish(Pi, "2.0.0", ("1.0.0", 60), ("2.0.0", 3));

        await PassAtAsync(TimeSpan.Zero);
        var status = await _service.GetStatusAsync(Pi, TestContext.Current.CancellationToken);

        Assert.Equal("2.0.0", status.AutoInstallVersion);
        Assert.Equal(Now - TimeSpan.FromDays(3) + TimeSpan.FromDays(7), status.AutoInstallAfter);
        Assert.Empty(_npm.Specs);
    }

    /// <summary>Installs 1.0.0, then 2.0.0 (so 1.0.0 is the previous version), then rolls back.</summary>
    private async Task RollBackFrom2Async()
    {
        var ct = TestContext.Current.CancellationToken;
        await _service.AutoUpdateAsync(Pi.Key, "1.0.0", ct);
        await _service.AutoUpdateAsync(Pi.Key, "2.0.0", ct);
        await _service.RollbackAsync(Pi.Key, ct: ct);
    }

    [Fact]
    public async Task After_a_rollback_auto_update_skips_the_held_version_and_installs_a_newer_one_past_the_cooldown()
    {
        await RollBackFrom2Async();
        UseCooldown(1);
        Publish(Pi, "2.0.0", ("1.0.0", 60), ("2.0.0", 5));
        var installs = _npm.Specs.Count;

        await PassAsync();
        Assert.Equal(installs, _npm.Specs.Count);

        Publish(Pi, "2.1.0", ("1.0.0", 60), ("2.0.0", 5), ("2.1.0", 0.5));
        await PassAsync();
        Assert.Equal(installs, _npm.Specs.Count);

        Publish(Pi, "2.1.0", ("1.0.0", 60), ("2.0.0", 5), ("2.1.0", 3));
        await PassAsync();

        Assert.Equal($"{Pi.NpmPackage}@2.1.0", _npm.Specs.Last());
        Assert.Null((await _service.GetStatusAsync(Pi, TestContext.Current.CancellationToken)).HeldVersion);
    }

    [Fact]
    public async Task A_release_the_hold_rules_out_is_not_promised_as_an_automatic_install()
    {
        UseCooldown(7);
        var ct = TestContext.Current.CancellationToken;
        await _service.AutoUpdateAsync(Pi.Key, "1.0.0", ct);
        await _service.AutoUpdateAsync(Pi.Key, "2.0.0", ct);
        Publish(Pi, "2.0.0", ("1.0.0", 60), ("2.0.0", 3));
        await PassAtAsync(TimeSpan.Zero);
        await _service.RollbackAsync(Pi.Key, ct: ct);

        var status = await _service.GetStatusAsync(Pi, ct);

        Assert.Equal("2.0.0", status.HeldVersion);
        Assert.Null(status.AutoInstallVersion);
    }

    [Fact]
    public async Task A_manual_update_lifts_the_hold()
    {
        await RollBackFrom2Async();
        _npm.LatestVersion = "2.0.0";
        Assert.Equal("2.0.0", (await _service.GetStatusAsync(Pi, TestContext.Current.CancellationToken)).HeldVersion);

        var status = await _service.UpdateAsync(Pi.Key, TestContext.Current.CancellationToken);

        Assert.Null(status.HeldVersion);
    }

    [Fact]
    public async Task An_agent_in_use_is_updated_right_away_whether_the_lease_began_before_or_during_the_install()
    {
        UseCooldown(0);
        _npm.Installed[Pi.BinaryName] = "1.0.0";
        _npm.Installed[OpenCode.BinaryName] = "1.0.0";
        Publish(Pi, "2.0.0", ("2.0.0", 5));
        Publish(OpenCode, "2.0.0", ("2.0.0", 5));
        using var piLease = _usage.Acquire(Pi.Key);
        IDisposable? openCodeLease = null;
        _npm.OnInstall = package => { if (package == OpenCode.NpmPackage) openCodeLease = _usage.Acquire(OpenCode.Key); };

        await PassAsync();

        Assert.NotNull(Active(Pi));
        Assert.NotNull(Active(OpenCode));
        openCodeLease!.Dispose();
    }

    [Fact]
    public async Task Several_updates_under_one_long_lease_keep_every_install_until_a_wake_finds_the_agent_idle()
    {
        UseCooldown(0);
        await _service.AutoUpdateAsync(Pi.Key, "1.0.0", TestContext.Current.CancellationToken);
        var versions = ManagedAgentInstall.VersionsRoot(_dataRoot, Pi);
        var lease = _usage.Acquire(Pi.Key);

        Publish(Pi, "2.0.0", ("1.0.0", 60), ("2.0.0", 5));
        await PassAsync();
        Publish(Pi, "3.0.0", ("1.0.0", 60), ("2.0.0", 5), ("3.0.0", 4));
        await PassAsync();
        Publish(Pi, "4.0.0", ("1.0.0", 60), ("2.0.0", 5), ("3.0.0", 4), ("4.0.0", 3));
        await PassAsync();
        Assert.Equal(4, Directory.GetDirectories(versions).Length);

        lease!.Dispose();
        var requests = _registry.Requests;
        await PassAtAsync(TimeSpan.FromHours(3) + TimeSpan.FromMinutes(10));

        Assert.Equal(2, Directory.GetDirectories(versions).Length);
        Assert.Equal(requests, _registry.Requests);
        var status = await _service.GetStatusAsync(Pi, TestContext.Current.CancellationToken);
        Assert.Equal("4.0.0", status.InstalledVersion);
        Assert.Equal("3.0.0", status.PreviousVersion);
    }

    [Fact]
    public async Task An_update_that_keeps_failing_does_not_keep_old_installs_on_disk()
    {
        UseCooldown(0);
        var ct = TestContext.Current.CancellationToken;
        await _service.AutoUpdateAsync(Pi.Key, "1.0.0", ct);
        var lease = _usage.Acquire(Pi.Key);
        await _service.AutoUpdateAsync(Pi.Key, "2.0.0", ct);
        await _service.AutoUpdateAsync(Pi.Key, "3.0.0", ct);
        var versions = ManagedAgentInstall.VersionsRoot(_dataRoot, Pi);
        Assert.Equal(3, Directory.GetDirectories(versions).Length);
        lease!.Dispose();
        Publish(Pi, "4.0.0", ("1.0.0", 60), ("4.0.0", 5));
        _npm.FailingPackages.Add(Pi.NpmPackage);

        await PassAsync();

        Assert.Equal(2, Directory.GetDirectories(versions).Length);
    }
}
