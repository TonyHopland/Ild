using ILD.Core.Services.RunHosts.Local;
using System.Runtime.CompilerServices;
using ILD.Core.Services.Implementations;
using ILD.Data.Security;

namespace ILD.Tests;

/// <summary>
/// Pins the environment ILD reads its roots and secret list from to a baseline of the
/// test run's own before any test runs, so the suite's result does not depend on what
/// the shell that launched it happened to export. The scratch root falls back to
/// <c>TMPDIR</c>, so clearing it is enough; the orchestrator-private root falls back
/// to a fixed <c>TMPDIR/ild-orchestrator-private</c> another ILD on the machine may
/// own, so it is pointed at a directory of this run's.
/// </summary>
internal static class TestEnvironmentBaseline
{
    [ModuleInitializer]
    internal static void Apply()
    {
        Environment.SetEnvironmentVariable(AgentIsolation.ScratchRootEnvVar, null);

        // Not merely defensive: SecretEnvironmentKeys reads this, so a deployment
        // that sets it widens the scrub set and the tests asserting that set --
        // including the baseline guard below -- start depending on the ambient
        // shell after all, which is the exact failure this class exists to stop.
        Environment.SetEnvironmentVariable(AgentIsolation.SecretEnvDenylistEnvVar, null);

        // Same reasoning for the session-token pepper: SessionTokenHasher reads it at
        // type-init, so a shell that exports it would silently re-key every stored hash
        // and the tests asserting the unkeyed default would fail on the ambient
        // environment. Tests that want a pepper set one through Configure and restore it.
        Environment.SetEnvironmentVariable(SessionTokenHasher.PepperEnvVar, null);
        SessionTokenHasher.Configure(null);

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
