using System.Net;
using System.Net.Sockets;
using ILD.Core.Services.Implementations;
using ILD.Core.Services.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ILD.Tests;

/// <summary>
/// WI-19: a service whose environment cross-references another service's port through
/// <c>${PORT:&lt;alias&gt;}</c> must be handed the port the referenced service is
/// <em>actually listening on in the current run</em> — and must not come up at all when
/// that cannot be guaranteed.
///
/// <para>
/// The reported failure is silent in every surface a user checks: each service is
/// individually healthy and <c>get_preview</c> reports plausible ports; only the link
/// between them is dead. So these tests observe the value the referencing service was
/// really launched with — it writes its resolved environment variable to a file on boot —
/// rather than trusting the status payload.
/// </para>
///
/// <para>
/// The referenced service deliberately declares no <c>suggestedPort</c>, so it draws a
/// fresh ephemeral port on every start. That is the case the cross-reference has to
/// follow, and the one an explicit <c>suggestedPort</c> (the WI-19 workaround applied to
/// this repo's own <c>ild.config.json</c>) masks.
/// </para>
/// </summary>
[Collection("EnvironmentPath")]
public class WorktreePreviewServicePortReferenceTests : IDisposable
{
    private readonly string _worktree;
    private WorktreePreviewService? _service;

    /// <summary>Where the consumer service writes the <c>DEP_URL</c> it was launched with.</summary>
    private string ObservedDepUrlPath => Path.Combine(_worktree, "observed-dep-url.txt");

    public WorktreePreviewServicePortReferenceTests()
    {
        _worktree = Path.Combine(Path.GetTempPath(), "ild-portref-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_worktree);
    }

    public void Dispose()
    {
        try { _service?.StopAsync(_worktree).GetAwaiter().GetResult(); } catch { /* best effort */ }
        try { _service?.Dispose(); } catch { /* best effort */ }
        KillOrphanedService();
        try { Directory.Delete(_worktree, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// The service's own stop paths cannot reach a process a failed start orphaned — that
    /// is the defect
    /// <see cref="A_start_that_fails_partway_does_not_leave_already_launched_services_running"/>
    /// pins. Until it is fixed, the test harness has to reap it itself, or the leaked node
    /// server outlives the test run and keeps its port.
    /// </summary>
    private void KillOrphanedService()
    {
        var pidFile = Path.Combine(_worktree, "first.pid");
        if (!File.Exists(pidFile))
            return;

        try
        {
            using var orphan = System.Diagnostics.Process.GetProcessById(int.Parse(File.ReadAllText(pidFile).Trim()));
            orphan.Kill(entireProcessTree: true);
            orphan.WaitForExit(5_000);
        }
        catch { /* already gone, or never started */ }
    }

    private WorktreePreviewService BuildService()
    {
        // A real HttpClient so the health probe genuinely succeeds against the node
        // one-liner each service runs.
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(() => new HttpClient());
        var configuration = new ConfigurationBuilder().Build();
        _service = new WorktreePreviewService(factory.Object, configuration, PreviewProxyBase.Disabled,
            NullLogger<WorktreePreviewService>.Instance);
        return _service;
    }

    /// <summary>
    /// The shape of this repo's own <c>app</c> profile, reduced to the two services that
    /// matter: <c>dep</c> (the referenced one, no <c>suggestedPort</c> — a fresh ephemeral
    /// port every start, like <c>workitem-server</c>) and <c>consumer</c> (the referencing
    /// one, pinned like <c>api</c>, whose <c>DEP_URL</c> is templated from <c>dep</c>'s
    /// alias). The consumer writes its resolved <c>DEP_URL</c> to disk before it starts
    /// serving, so the test can read back the environment the process was really given.
    /// </summary>
    private void WriteCrossReferencingConfig(int consumerPort)
    {
        var config = $$"""
        {
          "preview": {
            "defaultProfile": "app",
            "profiles": {
              "app": {
                "services": [
                  {
                    "name": "dep",
                    "port": "dep",
                    "command": "PORT=${PORT} node -e \"require('http').createServer((q,r)=>{r.end('ok')}).listen(process.env.PORT)\"",
                    "healthUrl": "http://127.0.0.1:${PORT}/"
                  },
                  {
                    "name": "consumer",
                    "port": "consumer",
                    "suggestedPort": {{consumerPort}},
                    "command": "PORT=${PORT} node -e \"require('fs').writeFileSync(process.env.OBSERVED_FILE, process.env.DEP_URL); require('http').createServer((q,r)=>{r.end('ok')}).listen(process.env.PORT)\"",
                    "healthUrl": "http://127.0.0.1:${PORT}/",
                    "env": {
                      "DEP_URL": "http://127.0.0.1:${PORT:dep}",
                      "OBSERVED_FILE": "${WORKTREE}/observed-dep-url.txt"
                    }
                  }
                ]
              }
            }
          }
        }
        """;
        File.WriteAllText(Path.Combine(_worktree, "ild.config.json"), config);
    }

    /// <summary>
    /// WI-19's core regression, and the exact sequence the work item calls for: start the
    /// profile, stop it, start it AGAIN, and check the referencing service carries the
    /// second run's port. A single start/stop pass cannot show it — there is only ever one
    /// allocation to be right about.
    /// </summary>
    [Fact]
    public async Task Cross_service_port_reference_follows_the_second_runs_ephemeral_port()
    {
        WriteCrossReferencingConfig(FindFreePort());
        var service = BuildService();

        var first = await service.StartAsync(_worktree);
        var firstDepPort = DepPort(first);
        Assert.Equal($"http://127.0.0.1:{firstDepPort}", await ReadObservedDepUrlAsync());

        await service.StopAsync(_worktree);

        // Nothing from the first run may still be holding dep's port: a survivor would
        // both keep serving the dead link and make the assertions below meaningless.
        Assert.True(IsPortFree(firstDepPort),
            $"Something is still listening on dep's first-run port {firstDepPort} after StopAsync.");

        // Hold that port so the second start is forced to draw a different one —
        // otherwise the ephemeral allocator could hand back the same number and this
        // would pass without ever exercising the bug.
        using var holdFirstPort = Listen(firstDepPort);

        // Clear the first run's record so what is read back can only be the second run's.
        File.Delete(ObservedDepUrlPath);

        var second = await service.StartAsync(_worktree);
        var secondDepPort = DepPort(second);
        Assert.NotEqual(firstDepPort, secondDepPort);

        // The consumer must have been launched pointing at the port dep is listening on
        // now — not the dead one from the first run.
        Assert.Equal($"http://127.0.0.1:{secondDepPort}", await ReadObservedDepUrlAsync());
    }

    /// <summary>
    /// The same guarantee across the granular per-service surface the Preview tab drives.
    /// A per-service restart keeps the alias's existing allocation
    /// (<c>EnsureServicePortAllocated</c> returns early when the alias is already
    /// reserved), which is what lets a still-running consumer keep its baked environment
    /// valid — so a fix that reallocates ports on every service start would break the
    /// cross-reference for every service it does not also restart. This pins that.
    /// </summary>
    [Fact]
    public async Task A_per_service_restart_keeps_the_cross_reference_pointing_at_the_live_port()
    {
        WriteCrossReferencingConfig(FindFreePort());
        var service = BuildService();

        var first = await service.StartAsync(_worktree);
        var firstDepPort = DepPort(first);

        await service.StopServiceAsync(_worktree, "dep");
        var restarted = await service.StartServiceAsync(_worktree, "dep");
        var restartedDepPort = DepPort(restarted);

        // The consumer was never restarted, so its environment still says firstDepPort —
        // which stays correct only because the alias kept its allocation.
        Assert.Equal(firstDepPort, restartedDepPort);
        Assert.Equal($"http://127.0.0.1:{restartedDepPort}", await ReadObservedDepUrlAsync());

        // And a consumer relaunched afterwards is handed the same live port.
        await service.StopServiceAsync(_worktree, "consumer");
        File.Delete(ObservedDepUrlPath);
        await service.StartServiceAsync(_worktree, "consumer");

        Assert.Equal($"http://127.0.0.1:{restartedDepPort}", await ReadObservedDepUrlAsync());
    }

    /// <summary>
    /// Acceptance criterion 3: "a reference that cannot be resolved to a live allocation
    /// fails the service start with an actionable message rather than starting it with a
    /// stale value."
    ///
    /// <para>
    /// <b>RED BY DESIGN</b> until WI-19 is implemented — no such validation exists today.
    /// Starting only the referencing service resolves <c>${PORT:dep}</c> against an
    /// allocation nothing is listening on, and the consumer comes up reporting healthy
    /// while the link it needs is dead: exactly the "everything looks fine and the feature
    /// quietly does nothing" property the work item wants removed. The message assertion is
    /// deliberately loose (it must name the alias) so the implementer picks the wording.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Starting_a_service_whose_reference_has_no_live_listener_fails_with_an_actionable_message()
    {
        WriteCrossReferencingConfig(FindFreePort());
        var service = BuildService();

        // dep is never started, so its alias has an allocation but no listener behind it.
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.StartServiceAsync(_worktree, "consumer"));

        Assert.Contains("dep", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A profile whose second service cannot be launched at all — its <c>cwd</c> escapes
    /// the worktree, which <c>ResolveWorkingDirectory</c> rejects — while the first is a
    /// healthy server on a known port. The point is what happens to the first service when
    /// the start as a whole fails.
    /// </summary>
    private void WriteConfigWithAnUnlaunchableSecondService(int firstPort)
    {
        var config = $$"""
        {
          "preview": {
            "defaultProfile": "app",
            "profiles": {
              "app": {
                "services": [
                  {
                    "name": "first",
                    "port": "first",
                    "suggestedPort": {{firstPort}},
                    "command": "PORT=${PORT} node -e \"require('fs').writeFileSync(process.env.PID_FILE, String(process.pid)); require('http').createServer((q,r)=>{r.end('ok')}).listen(process.env.PORT)\"",
                    "healthUrl": "http://127.0.0.1:${PORT}/",
                    "env": { "PID_FILE": "${WORKTREE}/first.pid" }
                  },
                  {
                    "name": "second",
                    "port": "second",
                    "cwd": "/etc",
                    "command": "node -e \"process.exit(0)\"",
                    "healthUrl": "http://127.0.0.1:${PORT}/"
                  }
                ]
              }
            }
          }
        }
        """;
        File.WriteAllText(Path.Combine(_worktree, "ild.config.json"), config);
    }

    /// <summary>
    /// <b>RED BY DESIGN.</b> Not the bug as described, but the mechanism that best explains
    /// the field report — found while reproducing it.
    ///
    /// <para>
    /// <c>StartAsync</c> registers the runtime in <c>_runtimes</c> only <em>after</em> every
    /// service has launched and passed its health probe. A failure anywhere in between
    /// therefore leaves already-launched services running and untracked: <c>StopAsync</c>
    /// finds no runtime, <c>Dispose</c> finds no runtime, and nothing ever kills them. They
    /// keep their environment from that attempt — including a <c>${PORT:alias}</c> value the
    /// next start will not reuse — hold their ports, and keep appending to the same
    /// per-service log file, where their output is indistinguishable from the current run's.
    /// That is precisely the reported shape: a service polling a dead port for as long as it
    /// runs while every surface reports healthy.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_start_that_fails_partway_does_not_leave_already_launched_services_running()
    {
        var firstPort = FindFreePort();
        WriteConfigWithAnUnlaunchableSecondService(firstPort);
        var service = BuildService();

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.StartAsync(_worktree));

        // 'second' is rejected while its step is being resolved, so the throw comes back
        // before 'first' has even finished booting — checking the port straight away would
        // find it free simply because nothing had bound it yet. Wait for the pid 'first'
        // records on startup, then a beat longer for it to reach its listen(). A start that
        // reaped it before it got that far records nothing, waits out the bound, and still
        // meets the assertion below, which is the outcome this is really about.
        await WaitForPidFileAsync();
        await Task.Delay(500);
        Assert.True(IsPortFree(firstPort),
            $"'first' is still listening on {firstPort} after StartAsync failed — the failed "
            + "start orphaned it, so no stop path can ever reach it.");
    }

    private static int DepPort(ILD.Data.DTOs.WorktreePreviewResponse response)
    {
        var dep = response.Services.Single(s => s.Name == "dep");
        Assert.NotNull(dep.Port);
        return dep.Port!.Value;
    }

    /// <summary>
    /// The consumer writes the file on boot and the caller only reads it after the start
    /// has seen the service healthy, so it is there — but the write and the first
    /// successful health response are two separate statements in the same one-liner, so
    /// allow a moment for the file to land rather than racing it.
    /// </summary>
    private async Task<string> ReadObservedDepUrlAsync()
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            if (File.Exists(ObservedDepUrlPath))
            {
                var text = await File.ReadAllTextAsync(ObservedDepUrlPath);
                if (!string.IsNullOrWhiteSpace(text))
                    return text.Trim();
            }

            await Task.Delay(100);
        }

        throw new Xunit.Sdk.XunitException(
            $"The consumer service never recorded the DEP_URL it was launched with at {ObservedDepUrlPath}.");
    }

    private static TcpListener Listen(int port)
    {
        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        return listener;
    }

    /// <summary>
    /// Whether the port can be bound — the same question
    /// <c>WorktreePreviewService.IsPortAvailable</c> asks, so a "free" answer here means the
    /// allocator would also consider it free. A listening socket (on any interface, since
    /// the node servers bind dual-stack) makes this false; a lingering TIME_WAIT does not.
    /// </summary>
    private static bool IsPortFree(int port)
    {
        try
        {
            var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            listener.Stop();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    /// <summary>
    /// Waits (briefly) for the <c>first</c> service to record its pid — which is both how
    /// the test knows it is up and the only handle the harness has for reaping it while the
    /// leak is unfixed (see <see cref="KillOrphanedService"/>). Returns false if it never
    /// does, which is not by itself a failure.
    /// </summary>
    private async Task<bool> WaitForPidFileAsync()
    {
        var pidFile = Path.Combine(_worktree, "first.pid");
        for (var attempt = 0; attempt < 50; attempt++)
        {
            if (File.Exists(pidFile) && !string.IsNullOrWhiteSpace(await File.ReadAllTextAsync(pidFile)))
                return true;
            await Task.Delay(100);
        }

        return false;
    }

    private static int FindFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
