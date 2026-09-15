using System.Diagnostics;
using System.Text;

namespace ILD.Core.Services.Implementations;

/// <summary>
/// File operations on paths the agent can write, such as pi's agent and session
/// directories in shared scratch. They run as the agent uid (ADR-0014): a link
/// the agent planted then only leads where the agent could already go, and
/// anything it planted there, read-only or not, can be cleared, because the agent
/// owns it. The orchestrator itself never opens, writes or deletes through these
/// paths. With uid isolation off the commands run as the orchestrator, which is
/// then the only uid there is.
/// </summary>
public static class AgentWritableFiles
{
    // Clears whatever is at "$p": a file, a link (unlinked, never followed) or a
    // directory tree, making the agent's own read-only folders writable first.
    private const string Remove =
        """rm -rf -- "$p" 2>/dev/null || { chmod -R u+w -- "$p" 2>/dev/null; rm -rf -- "$p"; }""";

    public static Task CreateDirectoryAsync(string path, CancellationToken ct = default)
        => RunCheckedAsync("""mkdir -p -- "$1" """, [path], stdin: null, ct);

    /// <summary>
    /// Replace whatever is at <paramref name="path"/> with a new file holding
    /// <paramref name="content"/>. The file is created exclusively, so it is never
    /// written through a link planted after the old entry was cleared.
    /// </summary>
    public static Task WriteFileAsync(string path, string content, CancellationToken ct = default)
        => RunCheckedAsync($"""p="$1"; {Remove} && set -C && cat > "$p" """, [path], content, ct);

    /// <summary>Whether a regular file, not a link, is at <paramref name="path"/>.</summary>
    public static async Task<bool> FileExistsAsync(string path, CancellationToken ct = default)
        => (await RunAsync("""[ -f "$1" ] && [ ! -L "$1" ]""", [path], stdin: null, ct)).ExitCode == 0;

    /// <summary>The content of the regular file at <paramref name="path"/>, or null when there is none; a link is never read through.</summary>
    public static async Task<string?> ReadFileAsync(string path, CancellationToken ct = default)
    {
        var result = await RunAsync("""[ -f "$1" ] && [ ! -L "$1" ] && exec cat -- "$1" """, [path], stdin: null, ct);
        return result.ExitCode == 0 ? result.Stdout : null;
    }

    /// <summary>
    /// Every regular file below <paramref name="directory"/> whose name matches
    /// <paramref name="namePattern"/>, with its first line. Links are not followed.
    /// </summary>
    public static async Task<IReadOnlyList<(string Path, string FirstLine)>> ListFilesAsync(
        string directory, string namePattern, CancellationToken ct = default)
    {
        const string script =
            """find "$1" -type f -name "$2" -exec sh -c 'for f; do printf "%s\0" "$f"; head -n 1 -- "$f" | tr -d "\000"; printf "\0"; done' sh {} + 2>/dev/null; exit 0""";
        var result = await RunAsync(script, [directory, namePattern], stdin: null, ct);

        var fields = result.Stdout.Split('\0');
        var files = new List<(string, string)>();
        for (var i = 0; i + 1 < fields.Length; i += 2)
            files.Add((fields[i], fields[i + 1].TrimEnd('\n', '\r')));
        return files;
    }

    /// <summary>
    /// Delete each of <paramref name="paths"/>, whatever it is. Never throws for
    /// what it finds there; returns whether every path is gone.
    /// </summary>
    public static async Task<bool> DeleteAsync(IEnumerable<string> paths, CancellationToken ct = default)
    {
        var arguments = paths.ToList();
        if (arguments.Count == 0) return true;

        var result = await RunAsync($$"""status=0; for p; do { {{Remove}}; } || status=1; done; exit $status""", arguments, stdin: null, ct);
        return result.ExitCode == 0;
    }

    /// <summary>
    /// Delete <paramref name="relativePath"/> inside every directory directly below
    /// <paramref name="directory"/>, whatever it is. Never throws for what it finds
    /// there; returns whether every one is gone.
    /// </summary>
    public static async Task<bool> DeleteInSubdirectoriesAsync(string directory, string relativePath, CancellationToken ct = default)
    {
        var result = await RunAsync(
            $$"""status=0; for d in "$1"/*/; do p="$d$2"; { {{Remove}}; } || status=1; done; exit $status""",
            [directory, relativePath.TrimStart('/')],
            stdin: null,
            ct);
        return result.ExitCode == 0;
    }

    private static async Task RunCheckedAsync(string script, IReadOnlyList<string> arguments, string? stdin, CancellationToken ct)
    {
        var result = await RunAsync(script, arguments, stdin, ct);
        if (result.ExitCode != 0)
            throw new IOException($"'{arguments[0]}': {result.Stderr.Trim()} (exit {result.ExitCode})");
    }

    /// <summary>
    /// The shell running <paramref name="script"/>, crossed to the agent uid.
    /// Explicit parameters so the crossing is testable without setting the
    /// process-global <c>ILD_AGENT_*</c> variables.
    /// </summary>
    internal static ProcessStartInfo BuildStartInfo(
        string script, IReadOnlyList<string> arguments, string? agentUser, string? agentGroup, string? agentHome)
    {
        var psi = new ProcessStartInfo("/bin/sh")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            UseShellExecute = false,
            WorkingDirectory = "/",
        };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(script);
        psi.ArgumentList.Add("ild-agent-files");
        foreach (var argument in arguments)
            psi.ArgumentList.Add(argument);

        return AgentIsolation.Route(psi, agentUser, agentGroup, agentHome);
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(
        string script, IReadOnlyList<string> arguments, string? stdin, CancellationToken ct)
    {
        using var process = Process.Start(BuildStartInfo(
                script, arguments, AgentIsolation.AgentUser, AgentIsolation.AgentGroup, AgentIsolation.AgentHome))
            ?? throw new IOException("could not start /bin/sh");
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
            throw;
        }

        return (process.ExitCode, await stdout, await stderr);
    }
}
