using ILD.Core.Services.RunHosts.Local;
using ILD.Core.Services.RunHosts;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using ILD.Core.Services.Implementations;
using ILD.Core.Services.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ILD.Tests;

/// <summary>
/// Named preview volumes (<c>${VOLUME:name}</c>) and the repository's Keep preview
/// data between runs setting: where each volume's folder is, how a run's copy of the
/// kept data reaches the next run, and the rule that only one work item of a keep-on repository holds its
/// volumes at a time. Health probes are answered by a stub handler unless a test
/// needs a service's own output, so services can be plain <c>sleep</c>s.
/// </summary>
public class WorktreePreviewServiceVolumeTests : IDisposable
{
    private readonly string _root;
    private readonly string _worktreesRoot;
    private readonly Guid _repository = Guid.NewGuid();
    private readonly List<WorktreePreviewService> _services = [];
    private readonly List<string> _fifosToRelease = [];
    private readonly Dictionary<string, Guid> _runs = [];
    private LocalRunHost? _host;

    public WorktreePreviewServiceVolumeTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "ild-volume-tests-" + Guid.NewGuid().ToString("N"));
        _worktreesRoot = Path.Combine(_root, "worktrees");
        Directory.CreateDirectory(_worktreesRoot);
    }

    public void Dispose()
    {
        foreach (var (worktree, run) in _runs)
            try { Directory.Delete(LocalRunHost.PreviewDataCopyOf(run, worktree), recursive: true); } catch { /* best effort */ }
        foreach (var fifo in _fifosToRelease)
            Unblock(fifo);
        foreach (var service in _services)
        {
            try { service.Dispose(); } catch { /* best effort */ }
        }
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The stored copy of the repository's kept preview data, which only a run that ends puts anything in.</summary>
    private string RepositoryFolder => Path.Combine(Path.GetFullPath(_worktreesRoot), ".preview-volumes", _repository.ToString("D"));

    /// <summary>The folder of the "data" volume in the run of <paramref name="worktree"/>'s own copy of the kept preview data.</summary>
    private string KeptVolume(string worktree) => Path.Combine(Copy(worktree), "data");

    private string Copy(string worktree) => LocalRunHost.PreviewDataCopyOf(_runs[worktree], worktree);

    private IConfiguration Configuration => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { ["App:WorktreesPath"] = _worktreesRoot })
        .Build();

    // Every worktree is a run made by the local host with Keep preview data on, so each has its own copy of the stored data.
    private LocalRunHost Host => _host ??= new LocalRunHost(repositories: Worktrees().Object, configuration: Configuration);

    private static Mock<IRepositoryManager> Worktrees()
    {
        var worktrees = new Mock<IRepositoryManager>();
        worktrees.Setup(r => r.CreateWorktreeAsync(It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync((string path, string _) => path);
        return worktrees;
    }

    /// <summary>Ends the run of <paramref name="worktree"/> cleanly, which puts its copy of the kept preview data back in the store.</summary>
    private Task EndRunAsync(string worktree) => Host.DeleteAsync(new RunEnvironment(_runs[worktree], worktree, _repository), Ct);

    private PreviewVolumeScope Scope(string workItemId, bool keep = true)
        => new(_repository, keep, workItemId, Title(workItemId));

    private static string Title(string workItemId) => $"Title of {workItemId}";

    private static string Refusal(string holder)
        => $"A preview is already running for work item {holder} ({Title(holder)}). Stop it before starting this one.";

    private WorktreePreviewService BuildService(
        HttpMessageHandler? health = null, Func<int, bool>? isPortAvailable = null, PreviewDataClaims? claims = null)
    {
        var handler = health ?? new HealthStub();
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(() => new HttpClient(handler, disposeHandler: false));
        var environment = new TestProcessEnvironment { { "HOME", Path.Combine(_root, "home") } }.WithRealPath();
        var service = new WorktreePreviewService(factory.Object, Configuration, PreviewProxyBase.Disabled,
            NullLogger<WorktreePreviewService>.Instance,
            environment: environment, isPortAvailable: isPortAvailable, claims: claims, host: Host);
        _services.Add(service);
        return service;
    }

    // --- config ---

    private sealed record Service(
        string Name,
        string Command,
        string? Cwd = null,
        Dictionary<string, string>? Env = null,
        string HealthUrl = "http://127.0.0.1:${PORT}/",
        bool Public = false,
        string? PublicUrl = null);

    private static readonly JsonSerializerOptions ConfigJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static void WriteConfig(string worktree, IReadOnlyList<string>? volumes, IReadOnlyList<string> install, IReadOnlyList<Service> services, string profile = "app")
    {
        var config = new
        {
            preview = new
            {
                defaultProfile = profile,
                profiles = new Dictionary<string, object>
                {
                    [profile] = new
                    {
                        volumes,
                        install = install.Select(command => new { command }).ToList(),
                        services = services.Select(s => new
                        {
                            s.Name,
                            Port = s.Name,
                            s.Command,
                            s.Cwd,
                            s.Env,
                            s.HealthUrl,
                            s.Public,
                            s.PublicUrl,
                        }).ToList(),
                    },
                },
            },
        };
        File.WriteAllText(Path.Combine(worktree, "ild.config.json"), JsonSerializer.Serialize(config, ConfigJson));
    }

    /// <summary>
    /// One volume, declared as "Data": the install reads what an earlier preview left
    /// in it (seen.txt), records where it is (install.marker), then claims it (owner).
    /// The one service only records that it was launched.
    /// </summary>
    private static readonly string[] StandardInstall =
    [
        "cat \"${VOLUME:data}/owner\" > \"${WORKTREE}/seen.txt\" 2>/dev/null; true",
        "printf '%s' \"${VOLUME:data}\" > \"${WORKTREE}/install.marker\" && printf '%s' \"${WORKTREE}\" > \"${VOLUME:data}/owner\"",
    ];

    private static readonly Service StandardWeb = new("web", "printf up > \"${WORKTREE}/web.marker\"; exec sleep 60",
        Env: new() { ["DATA"] = "${VOLUME:DATA}" });

    private async Task<string> WorktreeAsync(string name, IReadOnlyList<string>? install = null, IReadOnlyList<Service>? services = null, IReadOnlyList<string>? volumes = null)
    {
        var worktree = Path.GetFullPath(Path.Combine(_worktreesRoot, name));
        Directory.CreateDirectory(worktree);
        WriteConfig(worktree, volumes ?? ["Data"], install ?? StandardInstall, services ?? [StandardWeb]);
        _runs[worktree] = Guid.NewGuid();
        await Host.CreateAsync(new RunEnvironmentRequest(_runs[worktree], worktree, name, _repository, KeepPreviewData: true), Ct);
        return worktree;
    }

    private static string VolumeOf(string worktree) => File.ReadAllText(Path.Combine(worktree, "install.marker"));
    private static bool Installed(string worktree) => File.Exists(Path.Combine(worktree, "install.marker"));
    private static bool Launched(string worktree) => File.Exists(Path.Combine(worktree, "web.marker"));
    private static string Seen(string worktree) => File.ReadAllText(Path.Combine(worktree, "seen.txt"));

    public enum Op { Start, BeginStart, StartService, Install }

    private static Task Run(WorktreePreviewService service, Op op, string worktree, PreviewVolumeScope scope, CancellationToken? ct = null)
        => op switch
        {
            Op.Start => service.StartAsync(worktree, new WorktreePreviewStartOptions(Volumes: scope), ct ?? Ct),
            Op.BeginStart => service.BeginStartAsync(worktree, new WorktreePreviewStartOptions(Volumes: scope), ct ?? Ct),
            Op.StartService => service.StartServiceAsync(worktree, "web", new WorktreePreviewStartOptions(Volumes: scope), ct ?? Ct),
            _ => service.InstallAsync(worktree, volumes: scope, cancellationToken: ct ?? Ct),
        };

    // --- where volumes live ---

    [Fact]
    public async Task With_keep_on_each_run_works_on_its_own_copy_and_what_a_run_left_reaches_the_next_once_it_ends()
    {
        var service = BuildService();
        var a = await WorktreeAsync("wt-a");

        await service.StartAsync(a, new WorktreePreviewStartOptions(Volumes: Scope("301")), Ct);
        Assert.Equal(KeptVolume(a), VolumeOf(a));
        Assert.Equal("", Seen(a));
        await service.StopAsync(a, Ct);
        Assert.False(Directory.Exists(RepositoryFolder));

        await EndRunAsync(a);
        Assert.False(Directory.Exists(Copy(a)));
        Assert.Equal(a, File.ReadAllText(Path.Combine(RepositoryFolder, "data", "owner")));

        var b = await WorktreeAsync("wt-b");
        await service.StartAsync(b, new WorktreePreviewStartOptions(Volumes: Scope("302")), Ct);

        Assert.Equal(KeptVolume(b), VolumeOf(b));
        // What work item 301's run kept is there for work item 302's.
        Assert.Equal(a, Seen(b));
        Assert.Equal(b, File.ReadAllText(Path.Combine(KeptVolume(b), "owner")));
        // The stored copy changes only when this run ends.
        Assert.Equal(a, File.ReadAllText(Path.Combine(RepositoryFolder, "data", "owner")));
    }

    [Fact]
    public async Task With_keep_off_each_worktree_gets_its_own_folder_under_its_state_directory_which_a_restart_reuses()
    {
        var service = BuildService();
        var a = await WorktreeAsync("wt-a");
        var b = await WorktreeAsync("wt-b");

        var startedA = await service.StartAsync(a, new WorktreePreviewStartOptions(Volumes: Scope("301", keep: false)), Ct);
        // Previews of one keep-off repository run side by side.
        var startedB = await service.StartAsync(b, new WorktreePreviewStartOptions(Volumes: Scope("302", keep: false)), Ct);

        Assert.True(service.IsPreviewRunning(a));
        Assert.True(service.IsPreviewRunning(b));
        var volumeA = VolumeOf(a);
        var volumeB = VolumeOf(b);
        Assert.NotEqual(volumeA, volumeB);
        Assert.StartsWith(startedA.StateDirectory! + Path.DirectorySeparatorChar, volumeA);
        Assert.StartsWith(startedB.StateDirectory! + Path.DirectorySeparatorChar, volumeB);
        Assert.False(Directory.Exists(RepositoryFolder));

        await service.StopAsync(a, Ct);
        await service.StartAsync(a, new WorktreePreviewStartOptions(Volumes: Scope("301", keep: false)), Ct);

        Assert.Equal(volumeA, VolumeOf(a));
        Assert.Equal(a, Seen(a));
    }

    [Fact]
    public async Task Switching_the_setting_moves_no_data_either_way()
    {
        var service = BuildService();
        var a = await WorktreeAsync("wt-a");
        var b = await WorktreeAsync("wt-b");
        var c = await WorktreeAsync("wt-c");

        await service.StartAsync(a, new WorktreePreviewStartOptions(Volumes: Scope("301", keep: false)), Ct);
        await service.StopAsync(a, Ct);

        // Off → on: the repository's folder starts empty; the per-run data stays where it was.
        await service.StartAsync(b, new WorktreePreviewStartOptions(Volumes: Scope("302")), Ct);
        Assert.Equal("", Seen(b));
        Assert.Equal(a, File.ReadAllText(Path.Combine(VolumeOf(a), "owner")));
        await service.StopAsync(b, Ct);
        await EndRunAsync(b);

        // On → off: the repository's folder is left exactly as it was.
        await service.StartAsync(c, new WorktreePreviewStartOptions(Volumes: Scope("303", keep: false)), Ct);
        Assert.NotEqual(VolumeOf(b), VolumeOf(c));
        Assert.Equal("", Seen(c));
        Assert.Equal(b, File.ReadAllText(Path.Combine(RepositoryFolder, "data", "owner")));
    }

    [Fact]
    public async Task A_volume_token_expands_in_every_templated_field_and_its_folder_exists_before_the_first_step()
    {
        var health = new RecordingForwarder();
        var service = BuildService(health);
        var a = await WorktreeAsync("wt-a",
            install: ["test -d \"${VOLUME:data}\" && printf ok > \"${WORKTREE}/existed\""],
            services:
            [
                new Service("web",
                    "printf here > cwd.marker; printf '%s' \"$DATA\" > \"${WORKTREE}/web.env\"; "
                    + "exec node -e \"require('http').createServer((q,r)=>r.end('ok')).listen(process.env.PORT)\"",
                    Cwd: "${VOLUME:data}/web-cwd",
                    Env: new() { ["PORT"] = "${PORT}", ["DATA"] = "${VOLUME:Data}" },
                    HealthUrl: "http://127.0.0.1:${PORT}/health${VOLUME:DATA}",
                    Public: true,
                    PublicUrl: "https://web.example${VOLUME:data}"),
            ]);
        var volume = KeptVolume(a);

        var started = await service.StartAsync(a, new WorktreePreviewStartOptions(Volumes: Scope("301")), Ct);

        Assert.Equal("ok", File.ReadAllText(Path.Combine(a, "existed")));
        Assert.Equal(volume, File.ReadAllText(Path.Combine(a, "web.env")));
        // A working directory inside a volume is accepted and used.
        Assert.True(File.Exists(Path.Combine(volume, "web-cwd", "cwd.marker")));
        var web = started.Services.Single(s => s.Name == "web");
        Assert.Contains(health.Requests, uri => uri.AbsoluteUri == $"http://127.0.0.1:{web.Port}/health{volume}");
        Assert.Equal($"https://web.example{volume}", web.PublicUrl);
    }

    [Fact]
    public async Task A_running_preview_keeps_the_folders_it_was_created_with_when_the_setting_changes()
    {
        var service = BuildService(new RecordingForwarder());
        string Server(string name) =>
            $"printf '%s' \"$DATA\" > \"${{WORKTREE}}/{name}.env\"; "
            + "exec node -e \"require('http').createServer((q,r)=>r.end('ok')).listen(process.env.PORT)\"";
        var a = await WorktreeAsync("wt-a", install: [], services:
        [
            new Service("web", Server("web"), Env: new() { ["PORT"] = "${PORT}", ["DATA"] = "${VOLUME:data}" }),
            new Service("api", Server("api"), Env: new() { ["PORT"] = "${PORT}", ["DATA"] = "${VOLUME:data}" }),
        ]);

        await service.StartServiceAsync(a, "web", new WorktreePreviewStartOptions(Volumes: Scope("301", keep: false)), Ct);
        await service.StartServiceAsync(a, "api", new WorktreePreviewStartOptions(Volumes: Scope("301", keep: true)), Ct);

        var web = File.ReadAllText(Path.Combine(a, "web.env"));
        Assert.Equal(web, File.ReadAllText(Path.Combine(a, "api.env")));
        Assert.NotEqual(KeptVolume(a), web);
    }

    // --- config errors ---

    [Theory]
    [InlineData("bad_name", "bad_name")]
    [InlineData("a/b", "a/b")]
    [InlineData("../up", "../up")]
    [InlineData("with space", "with space")]
    [InlineData("", "")]
    [InlineData("data,DATA", "data")]
    public async Task A_volume_name_that_is_not_letters_digits_and_dashes_or_is_declared_twice_is_a_config_error(string declared, string named)
    {
        var service = BuildService();
        var worktree = Path.Combine(_worktreesRoot, "wt-a");
        Directory.CreateDirectory(worktree);
        WriteConfig(worktree, declared.Split(','), [], [StandardWeb with { Env = null }], profile: "frontend-stack");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.ValidateConfigAsync(worktree, cancellationToken: Ct));

        Assert.Contains("frontend-stack", error.Message);
        Assert.Contains(named, error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Volume_names_of_letters_digits_and_dashes_are_accepted()
    {
        var service = BuildService();
        var worktree = await WorktreeAsync("wt-a", install: [], services: [StandardWeb with { Env = new() { ["DATA"] = "${VOLUME:cache-2}" } }],
            volumes: ["Data", "cache-2"]);

        var result = await service.ValidateConfigAsync(worktree, cancellationToken: Ct);

        Assert.True(result.Configured);
    }

    public enum Reference { InstallCommand, ServiceCommand, ServiceEnv, ServiceCwd, HealthUrl, PublicUrl }

    public static TheoryData<Reference, Op?> UndeclaredReferences()
    {
        var data = new TheoryData<Reference, Op?>();
        foreach (var reference in Enum.GetValues<Reference>())
            foreach (var op in new Op?[] { null, Op.Start, Op.StartService, Op.Install })
                data.Add(reference, op);
        return data;
    }

    /// <param name="op">Null for <c>ValidateConfigAsync</c>.</param>
    [Theory]
    [MemberData(nameof(UndeclaredReferences))]
    public async Task A_reference_to_an_undeclared_volume_is_a_config_error_raised_before_any_step_runs(Reference reference, Op? op)
    {
        var service = BuildService();
        const string undeclared = "${VOLUME:missing}";
        var web = reference switch
        {
            Reference.ServiceCommand => StandardWeb with { Command = $"printf '%s' {undeclared} > /dev/null; " + StandardWeb.Command },
            Reference.ServiceEnv => StandardWeb with { Env = new() { ["OTHER"] = undeclared } },
            Reference.ServiceCwd => StandardWeb with { Cwd = undeclared },
            Reference.HealthUrl => StandardWeb with { HealthUrl = "http://127.0.0.1:${PORT}/" + undeclared },
            Reference.PublicUrl => StandardWeb with { Public = true, PublicUrl = "https://web.example/" + undeclared },
            _ => StandardWeb,
        };
        string[] install = reference == Reference.InstallCommand
            ? [.. StandardInstall, $"printf '%s' \"{undeclared}\" > /dev/null"]
            : StandardInstall;
        var worktree = await WorktreeAsync("wt-a", install: install, services: [web]);

        var error = op is { } operation
            ? await Assert.ThrowsAsync<InvalidOperationException>(() => Run(service, operation, worktree, Scope("301")))
            : await Assert.ThrowsAsync<InvalidOperationException>(() => service.ValidateConfigAsync(worktree, cancellationToken: Ct));

        Assert.Contains("missing", error.Message);
        Assert.False(Installed(worktree), "an install step ran");
        Assert.False(Launched(worktree), "a service was launched");
        Assert.False(service.IsPreviewRunning(worktree));
    }

    // --- one preview per keep-on repository ---

    [Theory]
    [InlineData(Op.Start)]
    [InlineData(Op.BeginStart)]
    [InlineData(Op.StartService)]
    [InlineData(Op.Install)]
    public async Task While_one_work_item_holds_a_keep_on_repository_another_starts_nothing_and_is_told_who_holds_it(Op op)
    {
        var service = BuildService();
        var a = await WorktreeAsync("wt-a");
        var b = await WorktreeAsync("wt-b");
        await service.StartAsync(a, new WorktreePreviewStartOptions(Volumes: Scope("305")), Ct);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Run(service, op, b, Scope("306")));

        Assert.Equal(Refusal("305"), error.Message);
        Assert.False(Installed(b), "an install step ran");
        Assert.False(Launched(b), "a service was launched");
        Assert.False(File.Exists(Path.Combine(b, "seen.txt")), "an install step ran");
        Assert.False(service.IsPreviewRunning(b));
        Assert.Equal(a, File.ReadAllText(Path.Combine(KeptVolume(a), "owner")));
        Assert.True(service.IsPreviewRunning(a));
    }

    [Theory]
    [InlineData(Op.Start)]
    [InlineData(Op.StartService)]
    [InlineData(Op.Install)]
    public async Task The_work_item_that_holds_the_volumes_may_start_and_install_again(Op op)
    {
        var service = BuildService();
        var a = await WorktreeAsync("wt-a");
        await service.StartAsync(a, new WorktreePreviewStartOptions(Volumes: Scope("305")), Ct);

        await Run(service, op, a, Scope("305"));

        Assert.True(service.IsPreviewRunning(a));
    }

    [Theory]
    [InlineData(Op.Start)]
    [InlineData(Op.StartService)]
    [InlineData(Op.Install)]
    public async Task Starting_the_holding_work_item_on_a_new_worktree_stops_its_earlier_preview_first(Op op)
    {
        var service = BuildService();
        var earlier = await WorktreeAsync("wt-a-run1");
        var current = await WorktreeAsync("wt-a-run2");
        var other = await WorktreeAsync("wt-b");
        await service.StartAsync(earlier, new WorktreePreviewStartOptions(Volumes: Scope("305")), Ct);

        await Run(service, op, current, Scope("305"));

        Assert.False(service.IsPreviewRunning(earlier));
        Assert.True(Installed(current));
        if (op != Op.Install)
        {
            // The new preview holds the volumes now, in place of the earlier one.
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Run(service, Op.Start, other, Scope("306")));
            Assert.Equal(Refusal("305"), error.Message);
            await service.StopAsync(current, Ct);
        }

        await service.StartAsync(other, new WorktreePreviewStartOptions(Volumes: Scope("306")), Ct);
        Assert.True(service.IsPreviewRunning(other));
    }

    [Fact]
    public async Task A_keep_off_preview_of_another_work_item_also_blocks_a_keep_on_start()
    {
        var service = BuildService();
        var a = await WorktreeAsync("wt-a");
        var b = await WorktreeAsync("wt-b");
        await service.StartAsync(a, new WorktreePreviewStartOptions(Volumes: Scope("305", keep: false)), Ct);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Run(service, Op.Start, b, Scope("306", keep: true)));

        Assert.Equal(Refusal("305"), error.Message);
        Assert.False(Installed(b));
    }

    [Fact]
    public async Task A_keep_off_start_is_never_refused()
    {
        var service = BuildService();
        var a = await WorktreeAsync("wt-a");
        var b = await WorktreeAsync("wt-b");
        await service.StartAsync(a, new WorktreePreviewStartOptions(Volumes: Scope("305", keep: true)), Ct);

        await service.StartAsync(b, new WorktreePreviewStartOptions(Volumes: Scope("306", keep: false)), Ct);

        Assert.True(service.IsPreviewRunning(a));
        Assert.True(service.IsPreviewRunning(b));
    }

    [Fact]
    public async Task An_install_in_progress_holds_the_volumes_against_every_other_start_and_install()
    {
        var service = BuildService();
        var (started, go) = (Fifo("started"), Fifo("go"));
        var a = await WorktreeAsync("wt-a", install: [$"printf x > '{started}'; cat '{go}' > /dev/null", .. StandardInstall]);
        var b = await WorktreeAsync("wt-b");

        var installA = service.InstallAsync(a, volumes: Scope("305"), cancellationToken: Ct);
        await WaitUntilOpenedAsync(started);

        var install = await Assert.ThrowsAsync<InvalidOperationException>(() => Run(service, Op.Install, b, Scope("306")));
        var start = await Assert.ThrowsAsync<InvalidOperationException>(() => Run(service, Op.Start, b, Scope("306")));

        Assert.Equal(Refusal("305"), install.Message);
        Assert.Equal(Refusal("305"), start.Message);
        Assert.False(Installed(b));
        Assert.False(Launched(b));

        await ReleaseAsync(go);
        await installA;
        Assert.Equal(a, File.ReadAllText(Path.Combine(KeptVolume(a), "owner")));

        // Nothing is registered for the worktree, so the hold ended with the install.
        await service.InstallAsync(b, volumes: Scope("306"), cancellationToken: Ct);
        Assert.True(Installed(b));
    }

    // --- the hold is released ---

    public enum StartFailure { InstallStepFails, PortInUse, HealthCancelled, ConfigError }

    [Theory]
    [InlineData(Op.Start, StartFailure.InstallStepFails)]
    [InlineData(Op.Start, StartFailure.PortInUse)]
    [InlineData(Op.Start, StartFailure.HealthCancelled)]
    [InlineData(Op.Start, StartFailure.ConfigError)]
    [InlineData(Op.StartService, StartFailure.InstallStepFails)]
    [InlineData(Op.StartService, StartFailure.PortInUse)]
    [InlineData(Op.StartService, StartFailure.ConfigError)]
    public async Task A_start_that_fails_before_its_preview_is_registered_holds_nothing_afterwards(Op op, StartFailure failure)
    {
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var health = new HealthStub();
        if (failure == StartFailure.HealthCancelled)
            health.OnNextRequest = cancel.Cancel;
        var service = BuildService(health, isPortAvailable: failure == StartFailure.PortInUse ? _ => false : null);
        var a = failure switch
        {
            StartFailure.InstallStepFails => await WorktreeAsync("wt-a", install: ["exit 3"]),
            StartFailure.ConfigError => await WorktreeAsync("wt-a", services: [StandardWeb with { Env = new() { ["DATA"] = "${VOLUME:missing}" } }]),
            _ => await WorktreeAsync("wt-a"),
        };
        var b = await WorktreeAsync("wt-b");
        var c = await WorktreeAsync("wt-c");
        var options = new WorktreePreviewStartOptions(
            Volumes: Scope("305"),
            PortOverrides: failure == StartFailure.PortInUse ? new Dictionary<string, int> { ["web"] = 4321 } : null);

        var attempt = op == Op.Start
            ? service.StartAsync(a, options, cancel.Token)
            : service.StartServiceAsync(a, "web", options, cancel.Token);
        await Assert.ThrowsAnyAsync<Exception>(() => attempt);
        Assert.False(service.IsPreviewRunning(a));

        var installed = await service.InstallAsync(c, volumes: Scope("307"), cancellationToken: Ct);
        Assert.True(installed.Installed);
        await service.StartAsync(b, new WorktreePreviewStartOptions(Volumes: Scope("306")), Ct);
        Assert.True(service.IsPreviewRunning(b));
    }

    public enum Release { StopAsync, StopLastService, ReplaceNotRunning }

    [Theory]
    [InlineData(Release.StopAsync)]
    [InlineData(Release.StopLastService)]
    [InlineData(Release.ReplaceNotRunning)]
    public async Task Once_no_registered_preview_owns_the_hold_another_work_item_can_start(Release release)
    {
        var service = BuildService();
        var a = release == Release.ReplaceNotRunning
            ? await WorktreeAsync("wt-a", services: [StandardWeb with { Command = "exit 0" }])
            : await WorktreeAsync("wt-a");
        var b = await WorktreeAsync("wt-b");

        switch (release)
        {
            case Release.StopAsync:
                await service.StartAsync(a, new WorktreePreviewStartOptions(Volumes: Scope("305")), Ct);
                await service.StopAsync(a, Ct);
                break;
            case Release.StopLastService:
                await service.StartServiceAsync(a, "web", new WorktreePreviewStartOptions(Volumes: Scope("305")), Ct);
                await service.StopServiceAsync(a, "web", Ct);
                break;
            case Release.ReplaceNotRunning:
                // The service exits at once, so the registered preview is not running
                // and the second start replaces it.
                await service.StartAsync(a, new WorktreePreviewStartOptions(Volumes: Scope("305")), Ct);
                await WaitForExitAsync(service, a);
                await service.StartAsync(a, new WorktreePreviewStartOptions(Volumes: Scope("305")), Ct);
                await WaitForExitAsync(service, a);
                await service.StopAsync(a, Ct);
                break;
        }

        await service.StartAsync(b, new WorktreePreviewStartOptions(Volumes: Scope("306")), Ct);
        Assert.True(service.IsPreviewRunning(b));
    }

    public enum InstallEnd { Success, StepFails, Cancelled }

    [Theory]
    [InlineData(InstallEnd.Success)]
    [InlineData(InstallEnd.StepFails)]
    [InlineData(InstallEnd.Cancelled)]
    public async Task An_install_holds_nothing_once_it_ends_however_it_ends(InstallEnd end)
    {
        var service = BuildService();
        var (started, go) = (Fifo("started"), Fifo("go"));
        var a = await WorktreeAsync("wt-a", install: end switch
        {
            InstallEnd.StepFails => ["exit 4"],
            InstallEnd.Cancelled => [$"printf x > '{started}'; cat '{go}' > /dev/null"],
            _ => StandardInstall,
        });
        var b = await WorktreeAsync("wt-b");
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        var install = service.InstallAsync(a, volumes: Scope("305"), cancellationToken: cancel.Token);
        if (end == InstallEnd.Cancelled)
        {
            await WaitUntilOpenedAsync(started);
            cancel.Cancel();
        }
        if (end == InstallEnd.Success)
            await install;
        else
            await Assert.ThrowsAnyAsync<Exception>(() => install);

        await service.StartAsync(b, new WorktreePreviewStartOptions(Volumes: Scope("306")), Ct);
        Assert.True(service.IsPreviewRunning(b));
    }

    [Fact]
    public async Task A_start_that_joins_one_already_queued_is_not_refused_for_data_that_was_free_when_that_one_began()
    {
        var claims = new PreviewDataClaims();
        var service = BuildService(claims: claims);
        var (started, go) = (Fifo("started"), Fifo("go"));
        var blocker = await WorktreeAsync("wt-c", install: [$"printf x > '{started}'; cat '{go}' > /dev/null"],
            services: [new Service("web", "exec sleep 60")], volumes: []);
        var holding = service.StartAsync(blocker, new WorktreePreviewStartOptions(), Ct);
        await WaitUntilOpenedAsync(started);
        var a = await WorktreeAsync("wt-a");
        var b = await WorktreeAsync("wt-b");
        Assert.Equal("starting", (await service.BeginStartAsync(a, new WorktreePreviewStartOptions(Volumes: Scope("301")), Ct)).State);

        Assert.True(claims.TryBeginReset(_repository, out _));
        var again = await service.BeginStartAsync(a, new WorktreePreviewStartOptions(Volumes: Scope("301")), Ct);

        Assert.Equal("starting", again.State);
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.BeginStartAsync(b, new WorktreePreviewStartOptions(Volumes: Scope("302")), Ct));
        Assert.Contains("being reset", refused.Message);
        Assert.Equal("stopped", (await service.GetStatusAsync(b, Ct)).State);
        claims.EndReset(_repository);
        File.WriteAllText(go, "go");
        await holding;
    }

    [Fact]
    public async Task A_refused_start_leaves_the_earlier_failure_on_show()
    {
        var claims = new PreviewDataClaims();
        var service = BuildService(claims: claims);
        var a = await WorktreeAsync("wt-a", install: ["exit 4"]);
        await Assert.ThrowsAnyAsync<Exception>(
            () => service.StartAsync(a, new WorktreePreviewStartOptions(Volumes: Scope("301")), Ct));
        Assert.Equal("failed", (await service.GetStatusAsync(a, Ct)).State);

        Assert.True(claims.TryBeginReset(_repository, out _));
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.BeginStartAsync(a, new WorktreePreviewStartOptions(Volumes: Scope("301")), Ct));
        claims.EndReset(_repository);

        Assert.Contains("being reset", refused.Message);
        Assert.Equal("failed", (await service.GetStatusAsync(a, Ct)).State);
    }

    // --- helpers ---

    private static async Task WaitForExitAsync(WorktreePreviewService service, string worktree)
    {
        // The service is "exit 0": its exit is immediate, and the state says when it is seen.
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if ((await service.GetStatusAsync(worktree, Ct)).State == "failed") return;
            await Task.Delay(50, Ct);
        }
        Assert.Equal("failed", (await service.GetStatusAsync(worktree, Ct)).State);
    }

    /// <summary>
    /// A named pipe an install step blocks on: opening one end waits for the other,
    /// which is how a step and the test hand each other the turn without sleeping.
    /// </summary>
    private string Fifo(string name)
    {
        var path = Path.Combine(_root, "fifo-" + name + "-" + Guid.NewGuid().ToString("N")[..8]);
        using var mkfifo = Process.Start("mkfifo", path);
        mkfifo.WaitForExit();
        Assert.Equal(0, mkfifo.ExitCode);
        _fifosToRelease.Add(path);
        return path;
    }

    /// <summary>Returns once the step has written to <paramref name="fifo"/>, i.e. is running.</summary>
    private static Task WaitUntilOpenedAsync(string fifo)
        => Task.Run(() => File.ReadAllText(fifo), Ct);

    private Task ReleaseAsync(string fifo) => Task.Run(() => File.WriteAllText(fifo, "go"), Ct);

    private static void Unblock(string fifo)
    {
        // Lets a step still blocked on the pipe finish, without waiting on one that is not.
        try
        {
            using var stream = new FileStream(fifo, FileMode.Open, FileAccess.Write | FileAccess.Read);
        }
        catch { /* best effort */ }
    }

    private sealed class HealthStub : HttpMessageHandler
    {
        public Action? OnNextRequest { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var hook = OnNextRequest;
            OnNextRequest = null;
            hook?.Invoke();
            return Task.FromResult(new HttpResponseMessage(hook is null ? HttpStatusCode.OK : HttpStatusCode.ServiceUnavailable));
        }
    }

    /// <summary>Records each health probe's URL and sends it to the real service.</summary>
    private sealed class RecordingForwarder() : DelegatingHandler(new HttpClientHandler { UseProxy = false })
    {
        public ConcurrentQueue<Uri> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Enqueue(request.RequestUri!);
            return base.SendAsync(request, cancellationToken);
        }
    }
}
