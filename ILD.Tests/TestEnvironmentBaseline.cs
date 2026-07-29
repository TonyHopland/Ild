using System.Runtime.CompilerServices;
using ILD.Core.Services.Implementations;

namespace ILD.Tests;

/// <summary>
/// Pins the ADR-0014 uid-isolation environment to its unit-test baseline before
/// any test runs, so the suite's result does not depend on what the shell that
/// launched it happened to export.
///
/// <para>
/// The ADR states the intent — "routing is a no-op unless <c>ILD_AGENT_USER</c> is
/// set, so unit tests keep the pre-isolation behavior unchanged" — and the tests
/// are written to it: the ones that care about isolation drive it through the
/// explicit-parameter overloads (<see cref="AgentIsolation.Route(System.Diagnostics.ProcessStartInfo, string?, string?, string?)"/>
/// and friends) rather than the process-global variables. Nothing enforced the
/// "unless" half, though, so the guarantee only held where the variables happened
/// to be absent. In a dev worktree <em>inside</em> the running container they are
/// already set, and 77 tests fail for reasons that have nothing to do with the
/// code under test — <c>setpriv --reuid=agent</c> failing with "initgroups failed"
/// for a uid holding no <c>CAP_SETUID</c>, and roots the live orchestrator
/// provisioned for its own uid.
/// </para>
///
/// <para>
/// The two roots need opposite treatment, which is the whole subtlety here.
/// Clearing <c>ILD_AGENT_SCRATCH_ROOT</c> is enough because its fallback is
/// <c>TMPDIR</c>, which the test process can write. Clearing
/// <c>ILD_ORCHESTRATOR_PRIVATE_ROOT</c> is not, because its fallback is a fixed
/// <c>TMPDIR/ild-orchestrator-private</c> — the very directory the entrypoint has
/// already created <c>0700</c> under the orchestrator's uid — so it has to be
/// pointed somewhere this process owns.
/// </para>
/// </summary>
internal static class TestEnvironmentBaseline
{
    [ModuleInitializer]
    internal static void Apply()
    {
        // Isolation off: this process is a single uid with no privilege to drop
        // from, which is the deployment shape the ADR calls "local development,
        // unit tests, any single-uid deployment".
        Environment.SetEnvironmentVariable(AgentIsolation.AgentUserEnvVar, null);
        Environment.SetEnvironmentVariable(AgentIsolation.AgentGroupEnvVar, null);
        Environment.SetEnvironmentVariable(AgentIsolation.AgentHomeEnvVar, null);
        Environment.SetEnvironmentVariable(AgentIsolation.ScratchRootEnvVar, null);

        // Not merely defensive: SecretEnvironmentKeys reads this, so a deployment
        // that sets it widens the scrub set and the tests asserting that set --
        // including the baseline guard below -- start depending on the ambient
        // shell after all, which is the exact failure this class exists to stop.
        Environment.SetEnvironmentVariable(AgentIsolation.SecretEnvDenylistEnvVar, null);

        var privateRoot = Path.Combine(Path.GetTempPath(), RootPrefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(privateRoot);
        Environment.SetEnvironmentVariable(AgentIsolation.PrivateRootEnvVar, privateRoot);

        // Per-run, so concurrent runs on one machine cannot race the fixed-name
        // files that land at the root of it (the git askpass helper). That makes
        // cleanup this class's problem: the preview service caches an npm tree
        // under here, so it is worth removing rather than leaving in TMPDIR.
        AppDomain.CurrentDomain.ProcessExit += (_, _) => TryDelete(privateRoot);
        SweepStaleRoots();
    }

    private const string RootPrefix = "ild-tests-private-";

    /// <summary>
    /// Discard roots abandoned by earlier runs — a killed test host never reaches
    /// <c>ProcessExit</c>, and deleting a large npm cache can outlast the
    /// runtime's exit budget even when it does. Age-gated because a concurrent run
    /// owns a sibling root, and deleting that out from under it would be a far
    /// worse failure than the litter this collects: a run lasts minutes, so a
    /// day-old root belongs to nobody.
    /// </summary>
    private static void SweepStaleRoots()
    {
        var cutoff = DateTime.UtcNow - TimeSpan.FromDays(1);
        try
        {
            foreach (var stale in Directory.EnumerateDirectories(Path.GetTempPath(), RootPrefix + "*"))
            {
                if (Directory.GetLastWriteTimeUtc(stale) < cutoff)
                    TryDelete(stale);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    // A shared temp directory holds other users' roots too, and cleanup must never
    // be the reason a test run fails.
    private static void TryDelete(string path)
    {
        try { Directory.Delete(path, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
