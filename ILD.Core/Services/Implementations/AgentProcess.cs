using System.Diagnostics;

namespace ILD.Core.Services.Implementations;

/// <summary>What a process run through <see cref="AgentProcess.RunAsync"/> ended with.</summary>
internal sealed record AgentProcessResult(int ExitCode, string Stdout, string Stderr);

/// <summary>
/// Runs a process started as the agent uid to its end. The caller starts it,
/// through a builder that routes it to the agent, so the crossing stays visible
/// where the process is spawned (ADR-0014, AgentIsolationSpawnSiteTests), and
/// hands it over here.
/// </summary>
internal static class AgentProcess
{
    /// <summary>
    /// Feed <paramref name="stdin"/> (or nothing) to <paramref name="process"/>,
    /// read both its streams and wait for it to exit. When
    /// <paramref name="ct"/> is cancelled the whole process tree is killed and
    /// waited for before the cancellation is rethrown, so nothing it started is
    /// still writing once this returns.
    /// </summary>
    public static async Task<AgentProcessResult> RunAsync(Process process, string? stdin, CancellationToken ct)
    {
        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);
        try
        {
            if (stdin is not null)
                await process.StandardInput.WriteAsync(stdin.AsMemory(), ct);
            process.StandardInput.Close();
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { /* already gone */ }
            await process.WaitForExitAsync(CancellationToken.None);
            throw;
        }

        return new AgentProcessResult(process.ExitCode, await stdout, await stderr);
    }
}
