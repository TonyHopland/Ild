using System.Diagnostics;
using ILD.Core.Services.Interfaces;
using Microsoft.Extensions.Logging;

namespace ILD.Core.Services.Implementations;

/// <summary>
/// Runs subprocesses for the orchestrator: as its own uid by default, as the agent
/// user when the caller asks for it (<see cref="ProcessRunOptions.AsAgent"/>). A
/// child of the orchestrator's own uid never inherits the secrets
/// <see cref="AgentIsolation.SecretEnvironmentKeys"/> names unless the caller opts
/// in, and runs without the orchestrator's ambient capabilities (ADR-0014).
/// </summary>
public sealed class ProcessRunner : IProcessRunner
{
    private static readonly string[] AgentGitEnvironmentKeys =
        ["GIT_ASKPASS", "ILD_GIT_PASSWORD", "ILD_GIT_USERNAME", "GIT_DIR", "GIT_WORK_TREE"];

    private readonly ILogger<ProcessRunner>? _logger;

    public ProcessRunner(ILogger<ProcessRunner>? logger = null) { _logger = logger; }

    /// <summary>
    /// The start info for one child. <paramref name="isolationMarker"/> and
    /// <paramref name="inheritedEnvironment"/> are the ambient inputs (the
    /// configured agent user, the orchestrator's own environment), explicit so the
    /// result is testable without touching process-global state.
    /// </summary>
    internal static ProcessStartInfo BuildStartInfo(
        string fileName,
        IReadOnlyList<string> args,
        string? workingDirectory,
        IReadOnlyDictionary<string, string?>? environmentVariables,
        ProcessRunOptions? options,
        string? isolationMarker,
        IReadOnlyDictionary<string, string>? inheritedEnvironment)
    {
        var psi = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = options?.StandardInputFrom is not null,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        if (!string.IsNullOrEmpty(workingDirectory)) psi.WorkingDirectory = workingDirectory;
        foreach (var a in args) psi.ArgumentList.Add(a);

        if (inheritedEnvironment is not null)
        {
            psi.Environment.Clear();
            foreach (var (key, value) in inheritedEnvironment)
                psi.Environment[key] = value;
        }

        var agent = options?.AsAgent;
        var agentScoped = agent is not null || options?.AgentScoped == true;
        if (options?.CleanEnvironment == true)
            psi.Environment.Clear();
        else if (agentScoped)
        {
            AgentIsolation.StripOrchestratorEnvironment(psi);
            foreach (var key in AgentGitEnvironmentKeys)
                psi.Environment.Remove(key);
        }
        else if (options?.InheritOrchestratorSecrets != true)
        {
            foreach (var key in AgentIsolation.SecretEnvironmentKeys)
                psi.Environment.Remove(key);
        }

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

        // The crossing needs the ambient CAP_SETUID that dropping capabilities
        // would remove before setpriv ran, so an agent child is never wrapped twice.
        if (agentScoped && options?.CleanEnvironment != true)
            return AgentIsolation.Route(psi, agent?.User, agent?.Group, agent?.Home);

        // Runs as the orchestrator, but git/npm act on agent-writable input
        // (package.json, .git/config and hooks), so it must not inherit the
        // orchestrator's ambient capabilities — see ADR-0014.
        return AgentIsolation.DropInheritedCapabilities(psi, isolationMarker);
    }

    public async Task<ProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> args,
        string? workingDirectory = null,
        CancellationToken ct = default,
        IReadOnlyDictionary<string, string?>? environmentVariables = null,
        ProcessRunOptions? options = null)
    {
        _logger?.LogDebug("exec {File} {Args} cwd={Cwd}", fileName, string.Join(' ', args), workingDirectory);

        using var proc = Process.Start(
            BuildStartInfo(fileName, args, workingDirectory, environmentVariables, options, AgentIsolation.AgentUser, null))!;
        var stdoutTask = options?.StandardOutputTo is { } sink
            ? CopyStandardOutputAsync(proc, sink, ct)
            : proc.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = proc.StandardError.ReadToEndAsync(ct);
        var stdinTask = options?.StandardInputFrom is { } source ? FeedStandardInputAsync(proc, source, ct) : Task.CompletedTask;
        try
        {
            await proc.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try
            {
                proc.Kill(entireProcessTree: true);
                // Nothing it started may still be writing once this returns.
                await proc.WaitForExitAsync(CancellationToken.None);
            }
            catch (InvalidOperationException) { /* already exited */ }
            catch (Exception ex) { _logger?.LogWarning(ex, "kill failed for {File}", fileName); }
            throw;
        }

        string stdout, stderr;
        try
        {
            stdout = await stdoutTask;
            stderr = await stderrTask;
            await stdinTask;
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* ignore */ }
            throw;
        }
        _logger?.LogDebug("exec {File} exit={Code}", fileName, proc.ExitCode);
        return new ProcessResult(proc.ExitCode, stdout, stderr);
    }

    // A sink that cannot take more leaves the child blocked writing to a pipe nobody
    // drains, so it is stopped rather than left to wait for a cancellation that may never come.
    private static async Task<string> CopyStandardOutputAsync(Process proc, Stream sink, CancellationToken ct)
    {
        try
        {
            await proc.StandardOutput.BaseStream.CopyToAsync(sink, ct);
            await sink.FlushAsync(ct);
        }
        catch
        {
            try { proc.Kill(entireProcessTree: true); } catch (InvalidOperationException) { /* already exited */ }
            throw;
        }
        return string.Empty;
    }

    // A child that exits without reading all of its input closes the pipe on us; its exit
    // code is the answer, not the broken pipe.
    private static async Task FeedStandardInputAsync(Process proc, Stream source, CancellationToken ct)
    {
        try
        {
            await source.CopyToAsync(proc.StandardInput.BaseStream, ct);
        }
        catch (IOException) { }
        finally
        {
            try { proc.StandardInput.Close(); } catch (IOException) { }
        }
    }
}
