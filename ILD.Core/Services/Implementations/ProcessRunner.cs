using System.Diagnostics;
using ILD.Core.Services.Interfaces;
using Microsoft.Extensions.Logging;

namespace ILD.Core.Services.Implementations;

public sealed class ProcessRunner : IProcessRunner
{
    private readonly ILogger<ProcessRunner>? _logger;

    public ProcessRunner(ILogger<ProcessRunner>? logger = null) { _logger = logger; }

    public async Task<ProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> args,
        string? workingDirectory = null,
        CancellationToken ct = default)
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

        _logger?.LogDebug("exec {File} {Args} cwd={Cwd}", fileName, string.Join(' ', args), workingDirectory);

        using var proc = Process.Start(psi)!;
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
