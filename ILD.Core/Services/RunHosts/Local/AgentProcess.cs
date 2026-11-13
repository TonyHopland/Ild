using System.Text;
using ILD.Core.Services.RunHosts;

namespace ILD.Core.Services.RunHosts.Local;

/// <summary>What a process run through <see cref="AgentProcess.RunAsync"/> ended with.</summary>
internal sealed record AgentProcessResult(int ExitCode, string Stdout, string Stderr);

/// <summary>Runs a process its caller started to its end.</summary>
internal static class AgentProcess
{
    /// <summary>What of a process's stderr is kept: it is read by a person, so past this it is cut with a marker.</summary>
    private const int StderrKeptCharacters = 64 * 1024;

    /// <summary>
    /// Feed <paramref name="stdin"/> (or nothing) to <paramref name="process"/>,
    /// read both its streams and wait for it to exit. When
    /// <paramref name="ct"/> is cancelled the whole process tree is killed and
    /// waited for before the cancellation is rethrown, so nothing it started is
    /// still writing once this returns. Stdout is parsed by the caller, so one
    /// longer than <see cref="RunReadLimits.MaxOutputBytes"/> kills the process
    /// and throws <see cref="ReadLimitExceededException"/>; stderr is cut instead.
    /// </summary>
    public static async Task<AgentProcessResult> RunAsync(IRunProcess process, string? stdin, CancellationToken ct, RunReadLimits? limits = null)
    {
        limits ??= RunReadLimits.FromEnvironment();
        var stdout = BoundedOutput.ReadAllAsync(process.StandardOutput, limits.MaxOutputBytes, ct);
        var errors = new StringBuilder();
        var stderr = BoundedOutput.PumpAsync(process.StandardError, StderrKeptCharacters, text => { lock (errors) errors.Append(text); return Task.CompletedTask; }, ct);
        try
        {
            if (stdin is not null)
                await process.StandardInput.WriteAsync(stdin.AsMemory(), ct);
            process.StandardInput.Close();
            var exited = process.WaitForExitAsync(ct);
            await Task.WhenAny(exited, stdout);
            if (stdout.IsFaulted) await stdout;
            await exited;
        }
        catch (Exception ex) when (ex is OperationCanceledException or ReadLimitExceededException)
        {
            process.TryKill();
            await process.WaitForExitAsync(CancellationToken.None);
            throw;
        }

        var output = await stdout;
        await stderr;
        lock (errors) return new AgentProcessResult(process.ExitCode, output, errors.ToString());
    }
}
