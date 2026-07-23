using System.Diagnostics;
using ILD.Core.Services.Interfaces;
using Microsoft.Extensions.Logging;

namespace ILD.Core.Services.Implementations;

/// <summary>
/// Runs orchestrator-side subprocesses (git, npm agent installs) as the
/// orchestrator's own uid. This is deliberately <em>not</em> routed through
/// <see cref="Adapters.AgentUserLauncher"/> (ADR-0014): those are trusted
/// operations that need to write the private <c>/data</c> tree (the repo store,
/// agent installs) the agent uid cannot touch. Only the coding-agent CLI launch
/// crosses to the lower-trust agent user.
/// </summary>
public sealed class ProcessRunner : IProcessRunner
{
    private readonly ILogger<ProcessRunner>? _logger;

    public ProcessRunner(ILogger<ProcessRunner>? logger = null) { _logger = logger; }

    public async Task<ProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> args,
        string? workingDirectory = null,
        CancellationToken ct = default,
        IReadOnlyDictionary<string, string?>? environmentVariables = null)
    {
        var psi = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        if (!string.IsNullOrEmpty(workingDirectory)) psi.WorkingDirectory = workingDirectory;
        foreach (var a in args) psi.ArgumentList.Add(a);
        if (environmentVariables != null)
        {
            foreach (var entry in environmentVariables)
            {
                if (entry.Value == null)
                    psi.Environment.Remove(entry.Key);
                else
                    psi.Environment[entry.Key] = entry.Value;
            }
        }

        _logger?.LogDebug("exec {File} {Args} cwd={Cwd}", fileName, string.Join(' ', args), workingDirectory);

        // Runs as the orchestrator, but git/npm act on agent-writable input
        // (package.json, /data/repos/*/.git/config and hooks), so it must not
        // inherit the orchestrator's ambient capabilities — see ADR-0014.
        using var proc = Process.Start(Adapters.AgentUserLauncher.DropInheritedCapabilities(psi))!;
        var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = proc.StandardError.ReadToEndAsync(ct);
        try
        {
            await proc.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { /* already exited */ }
            catch (Exception ex) { _logger?.LogWarning(ex, "kill failed for {File}", fileName); }
            throw;
        }

        string stdout, stderr;
        try
        {
            stdout = await stdoutTask;
            stderr = await stderrTask;
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* ignore */ }
            throw;
        }
        _logger?.LogDebug("exec {File} exit={Code}", fileName, proc.ExitCode);
        return new ProcessResult(proc.ExitCode, stdout, stderr);
    }
}
