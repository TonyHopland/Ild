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
/// code under test:
/// </para>
///
/// <list type="bullet">
///   <item><c>ILD_AGENT_USER</c> is set, so every spawn is wrapped in
///   <c>setpriv --reuid=agent</c>, which fails with "initgroups failed" because
///   the test process is already an unprivileged uid holding no
///   <c>CAP_SETUID</c> — the adapter, preview and repository tests.</item>
///   <item><c>ILD_ORCHESTRATOR_PRIVATE_ROOT</c> (and <c>ILD_AGENT_SCRATCH_ROOT</c>)
///   point at directories the live orchestrator provisioned for <em>its</em> uid,
///   which the test process cannot write.</item>
/// </list>
///
/// <para>
/// So the two roots are redirected rather than merely cleared: clearing them falls
/// back to a fixed path under <c>TMPDIR</c> (<c>/tmp/ild-orchestrator-private</c>),
/// which is shared, and which the orchestrator has usually already created
/// <c>0700</c> under a different owner. A per-run directory is also what keeps two
/// concurrent runs on one machine from colliding.
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

        // A deployment-specific denylist would silently widen the set of names
        // Route/RouteCommand scrub, so the scrubbing tests assert against a
        // baseline the environment cannot extend.
        Environment.SetEnvironmentVariable(AgentIsolation.SecretEnvDenylistEnvVar, null);

        SweepStaleRoots();

        var root = Path.Combine(Path.GetTempPath(), RootPrefix + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable(AgentIsolation.ScratchRootEnvVar, CreateDirectory(root, "scratch"));
        Environment.SetEnvironmentVariable(AgentIsolation.PrivateRootEnvVar, CreateDirectory(root, "private"));

        // The fast path, and the only one that runs for a normal exit. It is not
        // sufficient on its own: a killed test host never gets here, and the full
        // suite leaves a preview npm-cache big enough that deleting it can outlast
        // the runtime's ProcessExit budget. SweepStaleRoots above is what makes
        // that bounded rather than cumulative.
        AppDomain.CurrentDomain.ProcessExit += (_, _) => TryDelete(root);
    }

    private const string RootPrefix = "ild-tests-";

    /// <summary>
    /// Discard roots abandoned by earlier runs. Age-gated because a concurrent run
    /// on the same machine owns a sibling root, and deleting it out from under that
    /// run would be a far worse failure than the litter this collects — a test run
    /// lasts minutes, so a day-old root belongs to nobody.
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

    private static string CreateDirectory(string root, string name)
    {
        var path = Path.Combine(root, name);
        Directory.CreateDirectory(path);
        return path;
    }
}
