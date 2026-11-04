using System.Net;
using ILD.Core.Services.Implementations;
using ILD.Core.Services.Implementations.Adapters;
using ILD.Core.Services.Interfaces;
using ILD.Data.DTOs;

namespace ILD.Tests;

/// <summary>
/// Rollback to the install a swap replaced, the hold it leaves, and what pruning keeps, against a
/// fake npm whose installs report the version they were made from.
/// </summary>
[Collection(ManagedAgentInstallsCollection.Name)]
public sealed class ManagedAgentRollbackTests : IDisposable
{
    private readonly string _dataRoot = Path.Combine(Path.GetTempPath(), "ild-managed-rollback-" + Guid.NewGuid().ToString("N"));
    private readonly ManagedAgent _agent = ManagedAgentCatalog.Pi;
    private readonly IManagedAgentUsage _usage = new ManagedAgentUsage();
    private readonly Npm _npm = new();
    private readonly ManagedAgentService _service;
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    public ManagedAgentRollbackTests()
    {
        Directory.CreateDirectory(_dataRoot);
        _service = new ManagedAgentService(new HttpClient(new OfflineRegistry()), _npm, _dataRoot, logger: null, agentUser: null, usage: _usage);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dataRoot, recursive: true); } catch { /* best effort */ }
    }

    private sealed class OfflineRegistry : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
    }

    private sealed class Npm : IProcessRunner
    {
        public string LatestVersion { get; set; } = "99.0.0";

        public Task<ProcessResult> RunAsync(
            string fileName,
            IReadOnlyList<string> args,
            string? workingDirectory = null,
            CancellationToken ct = default,
            IReadOnlyDictionary<string, string?>? environmentVariables = null,
            ProcessRunOptions? options = null)
        {
            if (args.Count > 0 && args[0] == "install")
            {
                var spec = args[1];
                var version = spec[(spec.LastIndexOf('@') + 1)..];
                var prefix = args[args.ToList().IndexOf("--prefix") + 1];
                Directory.CreateDirectory(Path.Combine(prefix, "node_modules", ".bin"));
                File.WriteAllText(Path.Combine(prefix, "node_modules", ".bin", "pi"), "#!/bin/sh\n");
                var manifestDir = Path.Combine(prefix, "node_modules", ManagedAgentCatalog.Pi.NpmPackage);
                Directory.CreateDirectory(manifestDir);
                File.WriteAllText(Path.Combine(manifestDir, "package.json"), "{\"version\":\"" + (version == "latest" ? LatestVersion : version) + "\"}");
                return Task.FromResult(new ProcessResult(0, "added 1 package", ""));
            }

            // A binary reports the version of the install it lives in.
            var binDir = Path.GetDirectoryName(fileName);
            var manifest = binDir is null ? null : Path.Combine(binDir, "..", ManagedAgentCatalog.Pi.NpmPackage, "package.json");
            if (manifest is null || !File.Exists(manifest)) return Task.FromResult(new ProcessResult(127, "", "command not found"));
            var text = File.ReadAllText(manifest);
            return Task.FromResult(new ProcessResult(0, System.Text.Json.JsonDocument.Parse(text).RootElement.GetProperty("version").GetString() + "\n", ""));
        }
    }

    private string[] VersionDirs()
    {
        var root = ManagedAgentInstall.VersionsRoot(_dataRoot, _agent);
        return Directory.Exists(root) ? Directory.GetDirectories(root).Order().ToArray() : [];
    }

    private string? Active() => ManagedAgentInstall.CurrentVersionId(_dataRoot, _agent);

    private Task<ManagedAgentStatus> StatusAsync() => _service.GetStatusAsync(_agent, _ct);

    private async Task InstallAsync(params string[] versions)
    {
        foreach (var version in versions)
            Assert.Equal(AutoUpdateResult.Installed, await _service.AutoUpdateAsync(_agent.Key, version, _ct));
    }

    [Fact]
    public async Task A_swap_records_the_version_it_replaced_as_the_one_to_roll_back_to()
    {
        await InstallAsync("1.0.0");
        Assert.Null((await StatusAsync()).PreviousVersion);

        await InstallAsync("2.0.0");

        Assert.Equal("1.0.0", (await StatusAsync()).PreviousVersion);
    }

    [Fact]
    public async Task Rollback_points_back_at_the_previous_install_clears_the_marker_and_holds_the_version_it_left()
    {
        await InstallAsync("1.0.0");
        var first = Active();
        await InstallAsync("2.0.0");
        var second = Active();

        var status = await _service.RollbackAsync(_agent.Key, ct: _ct);

        Assert.Equal(first, Active());
        Assert.Equal("1.0.0", status.InstalledVersion);
        Assert.Null(status.PreviousVersion);
        Assert.False(File.Exists(ManagedAgentInstall.PreviousFile(_dataRoot, _agent)));
        Assert.Equal("2.0.0", status.HeldVersion);
        Assert.True(Directory.Exists(ManagedAgentInstall.VersionDir(_dataRoot, _agent, second!)));
    }

    [Fact]
    public async Task Rollback_for_a_version_that_is_no_longer_the_previous_one_is_refused_and_changes_nothing()
    {
        await InstallAsync("1.0.0", "2.0.0");
        var active = Active();

        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.RollbackAsync(_agent.Key, "0.9.0", _ct));
        Assert.Equal(active, Active());

        var status = await _service.RollbackAsync(_agent.Key, "1.0.0", _ct);
        Assert.Equal("1.0.0", status.InstalledVersion);
    }

    [Fact]
    public async Task An_update_by_hand_to_the_version_already_installed_offers_no_rollback_to_it()
    {
        await InstallAsync("1.0.0");
        _npm.LatestVersion = "1.0.0";

        var status = await _service.UpdateAsync(_agent.Key, _ct);

        Assert.Null(status.PreviousVersion);
    }

    [Fact]
    public async Task Rollback_acts_at_once_even_while_the_agent_is_in_use()
    {
        await InstallAsync("1.0.0", "2.0.0");

        using (_usage.Acquire(_agent.Key))
            await _service.RollbackAsync(_agent.Key, ct: _ct);

        Assert.Equal("1.0.0", (await StatusAsync()).InstalledVersion);
    }

    [Fact]
    public async Task The_next_swap_removes_the_install_that_was_rolled_back_from_and_previous_is_available_again()
    {
        await InstallAsync("1.0.0", "2.0.0");
        var second = Active()!;
        await _service.RollbackAsync(_agent.Key, ct: _ct);

        await InstallAsync("3.0.0");

        Assert.False(Directory.Exists(ManagedAgentInstall.VersionDir(_dataRoot, _agent, second)));
        Assert.Equal(2, VersionDirs().Length);
        Assert.Equal("1.0.0", (await StatusAsync()).PreviousVersion);
    }

    [Fact]
    public async Task Rollback_is_refused_with_no_previous_install_and_after_a_rollback()
    {
        await InstallAsync("1.0.0");
        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.RollbackAsync(_agent.Key, ct: _ct));

        await InstallAsync("2.0.0");
        await _service.RollbackAsync(_agent.Key, ct: _ct);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => _service.RollbackAsync(_agent.Key, ct: _ct));

        Assert.Contains("no previous version", error.Message);
        Assert.Equal("1.0.0", (await StatusAsync()).InstalledVersion);
    }

    [Fact]
    public async Task An_install_made_before_there_was_a_marker_has_no_rollback_until_its_next_update()
    {
        await InstallAsync("1.0.0", "2.0.0");
        File.Delete(ManagedAgentInstall.PreviousFile(_dataRoot, _agent));

        Assert.Null((await StatusAsync()).PreviousVersion);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.RollbackAsync(_agent.Key, ct: _ct));

        await InstallAsync("3.0.0");
        Assert.Equal("2.0.0", (await StatusAsync()).PreviousVersion);
    }

    [Fact]
    public async Task An_idle_agent_keeps_only_the_current_and_the_previous_install()
    {
        await InstallAsync("1.0.0", "2.0.0", "3.0.0");

        Assert.Equal(2, VersionDirs().Length);
        var status = await StatusAsync();
        Assert.Equal("3.0.0", status.InstalledVersion);
        Assert.Equal("2.0.0", status.PreviousVersion);
    }

    [Fact]
    public async Task An_update_while_the_agent_is_in_use_swaps_right_away_and_deletes_nothing()
    {
        await InstallAsync("1.0.0", "2.0.0");
        var dirs = VersionDirs();

        using (_usage.Acquire(_agent.Key))
        {
            await InstallAsync("3.0.0");

            Assert.Equal("3.0.0", (await StatusAsync()).InstalledVersion);
            Assert.Equal(dirs.Length + 1, VersionDirs().Length);
        }
    }

    [Fact]
    public async Task While_a_lease_is_held_no_install_is_deleted_and_once_it_is_released_only_current_and_previous_remain()
    {
        await InstallAsync("1.0.0");
        var lease = _usage.Acquire(_agent.Key);

        await InstallAsync("2.0.0", "3.0.0", "4.0.0");
        await _service.RemoveOldVersionsWhenIdleAsync(_agent.Key, _ct);
        Assert.Equal(4, VersionDirs().Length);

        lease!.Dispose();
        Assert.Equal(4, VersionDirs().Length);
        await _service.RemoveOldVersionsWhenIdleAsync(_agent.Key, _ct);

        Assert.Equal(2, VersionDirs().Length);
        var status = await StatusAsync();
        Assert.Equal("4.0.0", status.InstalledVersion);
        Assert.Equal("3.0.0", status.PreviousVersion);
    }

    [Fact]
    public async Task The_install_rolled_back_from_goes_at_the_first_idle_cleanup_and_not_while_the_agent_is_busy()
    {
        await InstallAsync("1.0.0", "2.0.0");
        await _service.RollbackAsync(_agent.Key, ct: _ct);
        Assert.Equal(2, VersionDirs().Length);

        using (_usage.Acquire(_agent.Key))
        {
            await _service.RemoveOldVersionsWhenIdleAsync(_agent.Key, _ct);
            Assert.Equal(2, VersionDirs().Length);
        }

        await _service.RemoveOldVersionsWhenIdleAsync(_agent.Key, _ct);
        Assert.Single(VersionDirs());
        Assert.Equal("1.0.0", (await StatusAsync()).InstalledVersion);
    }

    [Fact]
    public async Task Cleanup_with_nothing_to_delete_or_nothing_installed_does_nothing()
    {
        await _service.RemoveOldVersionsWhenIdleAsync(_agent.Key, _ct);

        await InstallAsync("1.0.0");
        await _service.RemoveOldVersionsWhenIdleAsync(_agent.Key, _ct);

        Assert.Single(VersionDirs());
        Assert.NotNull(ManagedAgentInstall.CurrentBinaryPath(_dataRoot, _agent));
    }

    [Fact]
    public async Task Cleanup_leaves_everything_alone_when_the_pointer_cannot_be_read()
    {
        await InstallAsync("1.0.0", "2.0.0");
        File.Delete(ManagedAgentInstall.PointerFile(_dataRoot, _agent));

        await _service.RemoveOldVersionsWhenIdleAsync(_agent.Key, _ct);

        Assert.Equal(2, VersionDirs().Length);
    }

    [Fact]
    public async Task A_previous_install_that_reports_no_version_is_kept_but_cannot_be_rolled_back_to()
    {
        await InstallAsync("1.0.0");
        var first = Active()!;
        await InstallAsync("2.0.0");
        File.Delete(Path.Combine(ManagedAgentInstall.VersionDir(_dataRoot, _agent, first), "node_modules", _agent.NpmPackage, "package.json"));

        await _service.RemoveOldVersionsWhenIdleAsync(_agent.Key, _ct);

        Assert.True(Directory.Exists(ManagedAgentInstall.VersionDir(_dataRoot, _agent, first)));
        Assert.Null((await StatusAsync()).PreviousVersion);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.RollbackAsync(_agent.Key, ct: _ct));
    }

    [Fact]
    public async Task Failing_to_note_the_rollback_target_does_not_make_the_update_fail()
    {
        await InstallAsync("1.0.0");
        // A directory where the marker file goes makes writing it fail.
        Directory.CreateDirectory(ManagedAgentInstall.PreviousFile(_dataRoot, _agent));

        await InstallAsync("2.0.0");

        Assert.Equal("2.0.0", (await StatusAsync()).InstalledVersion);
    }

    [Fact]
    public async Task A_marker_naming_the_active_install_is_not_a_rollback_target()
    {
        await InstallAsync("1.0.0");
        File.WriteAllText(ManagedAgentInstall.PreviousFile(_dataRoot, _agent), "{\"VersionId\":\"" + Active() + "\"}");

        Assert.Null((await StatusAsync()).PreviousVersion);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.RollbackAsync(_agent.Key, ct: _ct));
        Assert.Single(VersionDirs());
    }
}
