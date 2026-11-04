using System.Net;
using System.Text;
using ILD.Core.Services.Implementations;
using ILD.Core.Services.Implementations.Adapters;
using ILD.Core.Services.Interfaces;

namespace ILD.Tests;

/// <summary>
/// The auto-update half of <see cref="ManagedAgentService"/>: choosing the newest
/// stable version past the release-age cooldown from the registry packument, and
/// installing exactly that version unless the agent is in use at swap time.
/// </summary>
[Collection(ManagedAgentInstallsCollection.Name)]
public class ManagedAgentAutoUpdateTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 6, 30, 12, 0, 0, TimeSpan.Zero);
    private readonly string _dataRoot;
    private readonly ManagedAgent _agent = ManagedAgentCatalog.Pi;
    private readonly IManagedAgentUsage _usage = new ManagedAgentUsage();

    public ManagedAgentAutoUpdateTests()
    {
        _dataRoot = Path.Combine(Path.GetTempPath(), "ild-managed-autoupdate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dataRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dataRoot, recursive: true); } catch { /* best effort */ }
    }

    private sealed class Runner : IProcessRunner
    {
        public TaskCompletionSource? InstallGate;
        public TaskCompletionSource InstallEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<IReadOnlyList<string>> Calls { get; } = new();

        public async Task<ProcessResult> RunAsync(
            string fileName,
            IReadOnlyList<string> args,
            string? workingDirectory = null,
            CancellationToken ct = default,
            IReadOnlyDictionary<string, string?>? environmentVariables = null,
            ProcessRunOptions? options = null)
        {
            lock (Calls) Calls.Add([fileName, .. args]);
            if (args.Count > 0 && args[0] == "install")
            {
                InstallEntered.TrySetResult();
                if (InstallGate is not null) await InstallGate.Task;
                var prefix = args[args.ToList().IndexOf("--prefix") + 1];
                var binDir = Path.Combine(prefix, "node_modules", ".bin");
                Directory.CreateDirectory(binDir);
                File.WriteAllText(Path.Combine(binDir, "pi"), "#!/bin/sh\n");
                return new ProcessResult(0, "added 1 package", "");
            }
            return new ProcessResult(127, "", "command not found");
        }

        public string InstallSpec()
        {
            lock (Calls)
                return Calls.Single(c => c.Count > 1 && c[1] == "install")[2];
        }

        public bool Installed() { lock (Calls) return Calls.Any(c => c.Count > 1 && c[1] == "install"); }
    }

    /// <summary>Serves the packument for the package and the <c>/latest</c> manifest.</summary>
    private sealed class Registry : HttpMessageHandler
    {
        public HttpStatusCode Status = HttpStatusCode.OK;
        public string? Packument;
        public List<string> Urls { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Urls.Add(request.RequestUri!.AbsoluteUri);
            if (Status != HttpStatusCode.OK)
                return Task.FromResult(new HttpResponseMessage(Status));
            var url = request.RequestUri.AbsoluteUri;
            var body = url.EndsWith("/latest", StringComparison.Ordinal)
                ? "{\"version\":\"2.0.0\"}"
                : Packument ?? "{}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }

    private static string Iso(TimeSpan age) => (Now - age).ToString("yyyy-MM-ddTHH:mm:ss.fffZ");

    private static string Packument(string latest, params (string Version, TimeSpan Age)[] releases)
        => PackumentWithout(latest, [], releases);

    /// <summary>A packument whose <c>time</c> map lists every release but whose <c>versions</c> omit the withdrawn ones.</summary>
    private static string PackumentWithout(string latest, string[] withdrawn, params (string Version, TimeSpan Age)[] releases)
    {
        var time = string.Join(",", releases.Select(r => $"\"{r.Version}\":\"{Iso(r.Age)}\""));
        var versions = string.Join(",", releases.Where(r => !withdrawn.Contains(r.Version)).Select(r => $"\"{r.Version}\":{{}}"));
        return "{\"name\":\"pkg\",\"dist-tags\":{\"latest\":\"" + latest + "\"},\"versions\":{" + versions + "},"
            + "\"time\":{\"created\":\"" + Iso(TimeSpan.FromDays(900)) + "\",\"modified\":\"" + Iso(TimeSpan.FromHours(1)) + "\"," + time + "}}";
    }

    private static TimeSpan Days(double d) => TimeSpan.FromDays(d);

    private ManagedAgentService Create(Runner runner, Registry registry, IManagedAgentUsage? usage = null)
        => new(new HttpClient(registry), runner, _dataRoot, logger: null, agentUser: null, usage: usage ?? _usage);

    private string[] VersionDirs()
    {
        var root = ManagedAgentInstall.VersionsRoot(_dataRoot, _agent);
        return Directory.Exists(root) ? Directory.GetDirectories(root).Order().ToArray() : [];
    }

    private static readonly (string, TimeSpan)[] Releases =
    [
        ("1.0.0", Days(60)),
        ("1.5.0", Days(10)),
        ("1.6.0", Days(7)),
        ("1.9.0", Days(3)),
        ("2.0.0-beta.1", Days(20)),
        ("2.0.0", Days(1)),
        ("2.1.0", Days(15)),
    ];

    [Theory]
    [InlineData(7, "1.6.0")]
    [InlineData(8, "1.5.0")]
    [InlineData(2, "1.9.0")]
    [InlineData(0, "2.0.0")]
    [InlineData(365, null)]
    public async Task The_target_is_the_newest_stable_version_at_least_cooldown_old_and_not_past_latest(int cooldownDays, string? expected)
    {
        var registry = new Registry { Packument = Packument("2.0.0", Releases) };
        var service = Create(new Runner(), registry);

        var target = (await service.GetAutoUpdatePlanAsync(
            _agent.Key, TimeSpan.FromDays(cooldownDays), Now, TestContext.Current.CancellationToken))?.Target;

        Assert.Equal(expected, target);
    }

    [Fact]
    public async Task A_withdrawn_version_is_skipped_for_the_next_older_one()
    {
        var registry = new Registry
        {
            Packument = PackumentWithout("2.0.0", ["1.9.0"], ("1.5.0", Days(10)), ("1.9.0", Days(8)), ("2.0.0", Days(1))),
        };
        var service = Create(new Runner(), registry);

        Assert.Equal("1.5.0", (await service.GetAutoUpdatePlanAsync(_agent.Key, Days(7), Now, TestContext.Current.CancellationToken))?.Target);
    }

    [Fact]
    public async Task A_packument_without_versions_gives_no_target()
    {
        var service = Create(new Runner(), new Registry
        {
            Packument = "{\"dist-tags\":{\"latest\":\"2.0.0\"},\"time\":{\"2.0.0\":\"" + Iso(Days(30)) + "\"}}",
        });

        Assert.Null((await service.GetAutoUpdatePlanAsync(_agent.Key, Days(7), Now, TestContext.Current.CancellationToken))?.Target);
    }

    [Fact]
    public async Task A_prerelease_is_never_a_target_even_when_it_is_the_only_old_release()
    {
        var registry = new Registry { Packument = Packument("2.0.0", ("2.0.0-beta.1", Days(30)), ("2.0.0", Days(1))) };
        var service = Create(new Runner(), registry);

        Assert.Null((await service.GetAutoUpdatePlanAsync(_agent.Key, Days(7), Now, TestContext.Current.CancellationToken))?.Target);
    }

    [Fact]
    public async Task A_registry_failure_or_a_packument_without_time_data_gives_no_target()
    {
        var down = Create(new Runner(), new Registry { Status = HttpStatusCode.ServiceUnavailable });
        Assert.Null((await down.GetAutoUpdatePlanAsync(_agent.Key, Days(7), Now, TestContext.Current.CancellationToken))?.Target);

        var noTime = Create(new Runner(), new Registry { Packument = "{\"dist-tags\":{\"latest\":\"2.0.0\"}}" });
        Assert.Null((await noTime.GetAutoUpdatePlanAsync(_agent.Key, Days(7), Now, TestContext.Current.CancellationToken))?.Target);
    }

    [Fact]
    public async Task Auto_update_installs_exactly_the_given_version_and_makes_it_active()
    {
        var runner = new Runner();
        var service = Create(runner, new Registry());

        var result = await service.AutoUpdateAsync(_agent.Key, "1.6.0", TestContext.Current.CancellationToken);

        Assert.Equal("Installed", result.ToString());
        Assert.Equal($"{_agent.NpmPackage}@1.6.0", runner.InstallSpec());
        Assert.NotNull(ManagedAgentInstall.CurrentBinaryPath(_dataRoot, _agent));
    }

    [Fact]
    public async Task An_agent_in_use_is_updated_right_away()
    {
        var runner = new Runner();
        var service = Create(runner, new Registry());

        using (_usage.Acquire(_agent.Key))
            Assert.Equal("Installed", (await service.AutoUpdateAsync(_agent.Key, "1.6.0", TestContext.Current.CancellationToken)).ToString());

        Assert.Equal($"{_agent.NpmPackage}@1.6.0", runner.InstallSpec());
        Assert.NotNull(ManagedAgentInstall.CurrentBinaryPath(_dataRoot, _agent));
    }

    [Fact]
    public async Task A_run_starting_during_the_install_does_not_stop_the_swap()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new Runner { InstallGate = gate };
        var service = Create(runner, new Registry());
        var pending = service.AutoUpdateAsync(_agent.Key, "1.6.0", TestContext.Current.CancellationToken);
        await runner.InstallEntered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        using (_usage.Acquire(_agent.Key))
        {
            gate.SetResult();
            Assert.Equal("Installed", (await pending.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken)).ToString());
            Assert.NotNull(ManagedAgentInstall.CurrentBinaryPath(_dataRoot, _agent));
        }
    }

    [Fact]
    public async Task Manual_update_still_installs_latest_while_the_agent_is_in_use()
    {
        var runner = new Runner();
        var service = Create(runner, new Registry());

        using (_usage.Acquire(_agent.Key))
            await service.UpdateAsync(_agent.Key, TestContext.Current.CancellationToken);

        Assert.Equal($"{_agent.NpmPackage}@latest", runner.InstallSpec());
        Assert.NotNull(ManagedAgentInstall.CurrentBinaryPath(_dataRoot, _agent));
    }
}
