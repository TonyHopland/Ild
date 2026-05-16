namespace ILD.Core.Services.Interfaces;

/// <summary>
/// Spawns external processes. Centralises argument escaping, timeout handling,
/// and process-tree teardown so executors and repository helpers don't each
/// re-implement <see cref="System.Diagnostics.Process"/> plumbing.
/// </summary>
public interface IProcessRunner
{
    /// <summary>
    /// Run <paramref name="fileName"/> with <paramref name="args"/> and capture
    /// stdout / stderr. If the operation is cancelled (e.g. via timeout) the
    /// process tree is killed and <see cref="OperationCanceledException"/> is
    /// thrown to the caller.
    /// </summary>
    Task<ProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> args,
        string? workingDirectory = null,
        CancellationToken ct = default,
        IReadOnlyDictionary<string, string?>? environmentVariables = null);
}

public readonly record struct ProcessResult(int ExitCode, string StdOut, string StdErr)
{
    public bool Success => ExitCode == 0;
}
