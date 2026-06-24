using System.Net;
using System.Text;
using ILD.Core.Services.Implementations;
using ILD.Core.Services.Implementations.Adapters;
using ILD.Core.Services.Interfaces;
using Microsoft.Extensions.DependencyInjection;

namespace ILD.Tests;

public class ManagedAgentServiceTests : IDisposable
{
    private readonly string _dataRoot;
    private readonly ManagedAgent _agent = ManagedAgentCatalog.Pi;

    public ManagedAgentServiceTests()
    {
        _dataRoot = Path.Combine(Path.GetTempPath(), "ild-managed-agent-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dataRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dataRoot, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>Simulates npm + the agent's <c>--version</c> without spawning real processes.</summary>
    private sealed class FakeRunner : IProcessRunner
    {
        public string BinaryName = "pi";
        public string? InstalledVersion;
        public bool InstallSucceeds = true;
        public bool ProduceBinary = true;
        public List<IReadOnlyList<string>> Calls { get; } = new();

        // Concurrency instrumentation: when InstallGate is set, an install
        // parks on it so a test can observe how many installs run at once.
        public TaskCompletionSource? InstallGate;
        public TaskCompletionSource InstallEntered { get; } = new();
        private int _concurrent;
        private int _maxConcurrent;
        public int MaxConcurrentInstalls => Volatile.Read(ref _maxConcurrent);

        public async Task<ProcessResult> RunAsync(
            string fileName,
            IReadOnlyList<string> args,
            string? workingDirectory = null,
            CancellationToken ct = default,
            IReadOnlyDictionary<string, string?>? environmentVariables = null)
        {
            lock (Calls) Calls.Add([fileName, .. args]);

            if (args.Count > 0 && args[0] == "install")
            {
                var current = Interlocked.Increment(ref _concurrent);
                InterlockedMax(ref _maxConcurrent, current);
                InstallEntered.TrySetResult();
                try
                {
                    if (InstallGate is not null)
                        await InstallGate.Task;

                    var prefix = ArgValue(args, "--prefix")!;
                    if (InstallSucceeds && ProduceBinary)
                    {
                        var binDir = Path.Combine(prefix, "node_modules", ".bin");
                        Directory.CreateDirectory(binDir);
                        File.WriteAllText(Path.Combine(binDir, BinaryName), "#!/bin/sh\n");
                    }
                    return InstallSucceeds
                        ? new ProcessResult(0, "added 1 package", "")
                        : new ProcessResult(1, "", "npm ERR! 404 not found");
                }
                finally
                {
                    Interlocked.Decrement(ref _concurrent);
                }
            }

            if (args.Count > 0 && args[0] == "--version")
            {
                return InstalledVersion is null
                    ? new ProcessResult(127, "", "command not found")
                    : new ProcessResult(0, InstalledVersion + "\n", "");
            }

            return new ProcessResult(0, "", "");
        }

        private static void InterlockedMax(ref int target, int value)
        {
            int snapshot;
            while ((snapshot = Volatile.Read(ref target)) < value
                && Interlocked.CompareExchange(ref target, value, snapshot) != snapshot)
            {
                // retry on lost race
            }
        }

        private static string? ArgValue(IReadOnlyList<string> args, string name)
        {
            var idx = args.ToList().IndexOf(name);
            return idx >= 0 && idx + 1 < args.Count ? args[idx + 1] : null;
        }
    }

    private sealed class RegistryHandler : HttpMessageHandler
    {
        public string? Version = "1.0.0";
        public HttpStatusCode Status = HttpStatusCode.OK;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Status != HttpStatusCode.OK)
                return Task.FromResult(new HttpResponseMessage(Status));

            var json = $"{{\"version\":\"{Version}\"}}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            });
        }
    }

    private ManagedAgentService CreateService(FakeRunner runner, RegistryHandler handler)
        => new(new HttpClient(handler), runner, _dataRoot);

    [Fact]
    public async Task GetStatus_reports_update_available_when_installed_is_behind()
    {
        var runner = new FakeRunner { InstalledVersion = "0.80.1" };
        var handler = new RegistryHandler { Version = "0.80.2" };
        var service = CreateService(runner, handler);

        var status = await service.GetStatusAsync(_agent);

        Assert.Equal("0.80.1", status.InstalledVersion);
        Assert.Equal("0.80.2", status.LatestVersion);
        Assert.True(status.UpdateAvailable);
        Assert.Null(status.Error);
    }

    [Fact]
    public async Task GetStatus_reports_no_update_when_up_to_date()
    {
        var runner = new FakeRunner { InstalledVersion = "0.80.2" };
        var handler = new RegistryHandler { Version = "0.80.2" };
        var service = CreateService(runner, handler);

        var status = await service.GetStatusAsync(_agent);

        Assert.False(status.UpdateAvailable);
    }

    [Fact]
    public async Task GetStatus_surfaces_error_when_registry_unreachable()
    {
        var runner = new FakeRunner { InstalledVersion = "0.80.1" };
        var handler = new RegistryHandler { Status = HttpStatusCode.ServiceUnavailable };
        var service = CreateService(runner, handler);

        var status = await service.GetStatusAsync(_agent);

        Assert.Null(status.LatestVersion);
        Assert.False(status.UpdateAvailable);
        Assert.NotNull(status.Error);
    }

    [Fact]
    public async Task Update_installs_to_data_and_makes_it_the_active_binary()
    {
        var runner = new FakeRunner { InstalledVersion = "0.80.2" };
        var handler = new RegistryHandler { Version = "0.80.2" };
        var service = CreateService(runner, handler);

        var status = await service.UpdateAsync(_agent.Key, version: null);

        var active = ManagedAgentInstall.CurrentBinaryPath(_dataRoot, _agent);
        Assert.NotNull(active);
        Assert.True(File.Exists(active));
        Assert.StartsWith(ManagedAgentInstall.VersionsRoot(_dataRoot, _agent), active);
        Assert.Equal("0.80.2", status.InstalledVersion);
        // npm was asked to install the latest published version.
        Assert.Contains(runner.Calls, c => c.Contains("install") && c.Any(a => a.Contains("@latest")));
    }

    [Fact]
    public async Task Update_with_explicit_version_pins_that_version()
    {
        var runner = new FakeRunner { InstalledVersion = "0.79.0" };
        var handler = new RegistryHandler { Version = "0.80.2" };
        var service = CreateService(runner, handler);

        await service.UpdateAsync(_agent.Key, version: "0.79.0");

        Assert.Contains(runner.Calls, c => c.Any(a => a.EndsWith("@0.79.0")));
    }

    [Fact]
    public async Task Update_keeps_only_the_latest_install()
    {
        var runner = new FakeRunner { InstalledVersion = "0.80.1" };
        var handler = new RegistryHandler { Version = "0.80.2" };
        var service = CreateService(runner, handler);

        await service.UpdateAsync(_agent.Key, version: null);
        await service.UpdateAsync(_agent.Key, version: null);

        var versionDirs = Directory.GetDirectories(ManagedAgentInstall.VersionsRoot(_dataRoot, _agent));
        Assert.Single(versionDirs);
    }

    [Fact]
    public async Task Update_failure_leaves_previous_version_intact()
    {
        var runner = new FakeRunner { InstalledVersion = "0.80.1" };
        var handler = new RegistryHandler { Version = "0.80.2" };
        var service = CreateService(runner, handler);

        // First update succeeds and becomes the live version.
        await service.UpdateAsync(_agent.Key, version: null);
        var liveBefore = ManagedAgentInstall.CurrentBinaryPath(_dataRoot, _agent);
        var pointerBefore = File.ReadAllText(ManagedAgentInstall.PointerFile(_dataRoot, _agent));

        // Second update fails mid-install.
        runner.InstallSucceeds = false;
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.UpdateAsync(_agent.Key, version: null));

        // The previously-active version is untouched, and the failed staging dir is gone.
        Assert.Equal(liveBefore, ManagedAgentInstall.CurrentBinaryPath(_dataRoot, _agent));
        Assert.True(File.Exists(liveBefore));
        Assert.Equal(pointerBefore, File.ReadAllText(ManagedAgentInstall.PointerFile(_dataRoot, _agent)));
        Assert.Single(Directory.GetDirectories(ManagedAgentInstall.VersionsRoot(_dataRoot, _agent)));
    }

    [Fact]
    public async Task Update_fails_when_install_omits_the_binary()
    {
        var runner = new FakeRunner { ProduceBinary = false };
        var handler = new RegistryHandler { Version = "0.80.2" };
        var service = CreateService(runner, handler);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.UpdateAsync(_agent.Key, version: null));

        Assert.False(Directory.Exists(ManagedAgentInstall.VersionsRoot(_dataRoot, _agent))
            && Directory.GetDirectories(ManagedAgentInstall.VersionsRoot(_dataRoot, _agent)).Length > 0);
    }

    [Fact]
    public async Task Update_rejects_unknown_agent_key()
    {
        var service = CreateService(new FakeRunner(), new RegistryHandler());
        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => service.UpdateAsync("does-not-exist", version: null));
    }

    [Fact]
    public async Task Concurrent_updates_of_the_same_agent_are_serialized_across_instances()
    {
        // Two separate instances stand in for the transient typed-client
        // instances two racing requests would get. A genuinely-shared (static)
        // lock must stop both installs running at once — otherwise one prune
        // could delete the other's just-swapped version dir.
        var gateA = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runnerA = new FakeRunner { InstalledVersion = "0.80.2", InstallGate = gateA };
        var runnerB = new FakeRunner { InstalledVersion = "0.80.2" };
        var serviceA = CreateService(runnerA, new RegistryHandler { Version = "0.80.2" });
        var serviceB = CreateService(runnerB, new RegistryHandler { Version = "0.80.2" });

        var taskA = serviceA.UpdateAsync(_agent.Key, version: null);
        var taskB = serviceB.UpdateAsync(_agent.Key, version: null);

        // A is inside its install (holding the shared lock).
        await runnerA.InstallEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        // B must not have started installing — it is parked on the lock.
        Assert.False(runnerB.InstallEntered.Task.IsCompleted);

        gateA.SetResult();
        await Task.WhenAll(taskA, taskB).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(runnerB.InstallEntered.Task.IsCompleted);
        // The end state is a single, valid install.
        Assert.Single(Directory.GetDirectories(ManagedAgentInstall.VersionsRoot(_dataRoot, _agent)));
        Assert.NotNull(ManagedAgentInstall.CurrentBinaryPath(_dataRoot, _agent));
    }

    [Fact]
    public void Service_resolves_from_DI_as_a_typed_http_client()
    {
        // The service has a second public ctor (the test seam); make sure the
        // typed-client factory still selects the DI ctor and constructs it.
        var services = new ServiceCollection();
        services.AddSingleton<IProcessRunner>(new FakeRunner());
        services.AddHttpClient<IManagedAgentService, ManagedAgentService>();
        using var provider = services.BuildServiceProvider();

        var resolved = provider.GetRequiredService<IManagedAgentService>();

        Assert.IsType<ManagedAgentService>(resolved);
    }

    [Theory]
    [InlineData("npm:malicious-package")]
    [InlineData("latest")]
    [InlineData("^1.2.3")]
    [InlineData("https://evil.example/x.tgz")]
    [InlineData("../../escape")]
    public async Task Update_rejects_a_non_semver_version(string version)
    {
        var service = CreateService(new FakeRunner(), new RegistryHandler());

        await Assert.ThrowsAsync<ArgumentException>(
            () => service.UpdateAsync(_agent.Key, version));

        // Rejected before any install ran.
        Assert.False(Directory.Exists(ManagedAgentInstall.AgentRoot(_dataRoot, _agent)));
    }

    [Theory]
    [InlineData("1.2.3", true)]
    [InlineData("0.80.2", true)]
    [InlineData("1.2.3-beta.1", true)]
    [InlineData("latest", false)]
    [InlineData("npm:other", false)]
    [InlineData("^1.2.3", false)]
    [InlineData("1.2", false)]
    public void IsValidVersion_accepts_only_exact_semver(string version, bool expected)
    {
        Assert.Equal(expected, ManagedAgentService.IsValidVersion(version));
    }

    [Fact]
    public void Catalog_manages_pi_opencode_and_claude_code()
    {
        Assert.Equal(
            ["pi", "opencode", "claude-code"],
            ManagedAgentCatalog.All.Select(a => a.Key).ToArray());

        var claude = ManagedAgentCatalog.Find("claude-code");
        Assert.NotNull(claude);
        Assert.Equal("@anthropic-ai/claude-code", claude!.NpmPackage);
        Assert.Equal("claude", claude.BinaryName);
        Assert.Equal("claude", claude.Command);
    }

    [Fact]
    public async Task GetStatuses_covers_every_managed_agent()
    {
        var runner = new FakeRunner { InstalledVersion = "1.0.0" };
        var service = CreateService(runner, new RegistryHandler { Version = "1.0.0" });

        var statuses = await service.GetStatusesAsync();

        Assert.Equal(
            ManagedAgentCatalog.All.Select(a => a.Key).OrderBy(k => k).ToArray(),
            statuses.Select(s => s.Key).OrderBy(k => k).ToArray());
    }

    [Fact]
    public async Task Update_installs_claude_code_to_data()
    {
        var claude = ManagedAgentCatalog.ClaudeCode;
        var runner = new FakeRunner { BinaryName = claude.BinaryName, InstalledVersion = "2.1.187" };
        var service = CreateService(runner, new RegistryHandler { Version = "2.1.187" });

        await service.UpdateAsync(claude.Key, version: null);

        var active = ManagedAgentInstall.CurrentBinaryPath(_dataRoot, claude);
        Assert.NotNull(active);
        Assert.True(File.Exists(active));
        Assert.Contains(runner.Calls, c => c.Any(a => a.Contains("@anthropic-ai/claude-code@latest")));
    }

    [Fact]
    public async Task Installed_data_version_is_preferred_over_baked_in_copy()
    {
        var runner = new FakeRunner { InstalledVersion = "0.80.2" };
        var handler = new RegistryHandler { Version = "0.80.2" };
        var service = CreateService(runner, handler);

        // No /data install yet → resolves to the bare command on PATH.
        Assert.Equal(_agent.Command, ManagedAgentInstall.ResolveCommand(_agent, _dataRoot));

        await service.UpdateAsync(_agent.Key, version: null);

        // After install → resolves to the /data binary.
        var resolved = ManagedAgentInstall.ResolveCommand(_agent, _dataRoot);
        Assert.NotEqual(_agent.Command, resolved);
        Assert.StartsWith(ManagedAgentInstall.AgentRoot(_dataRoot, _agent), resolved);
    }

    [Theory]
    [InlineData("1.2.3", "1.2.4", true)]
    [InlineData("1.2.3", "1.3.0", true)]
    [InlineData("1.2.3", "2.0.0", true)]
    [InlineData("1.2.3", "1.2.3", false)]
    [InlineData("1.2.4", "1.2.3", false)]
    [InlineData("0.80.10", "0.80.9", false)] // numeric, not lexicographic
    public void IsNewer_compares_semver_numerically(string installed, string latest, bool expected)
    {
        Assert.Equal(expected, ManagedAgentService.IsNewer(latest, installed));
    }

    [Theory]
    [InlineData("0.80.2\n", "0.80.2")]
    [InlineData("pi 0.80.2 (build abc)", "0.80.2")]
    [InlineData("no version here", null)]
    public void ParseVersion_extracts_leading_semver(string input, string? expected)
    {
        Assert.Equal(expected, ManagedAgentService.ParseVersion(input));
    }
}
