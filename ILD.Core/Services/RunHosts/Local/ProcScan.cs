using System.Diagnostics;
using System.Text;

namespace ILD.Core.Services.RunHosts.Local;

/// <summary>Finds, checks and ends the processes of an agent launch on this machine by reading <c>/proc</c> (Linux; elsewhere there are none).</summary>
internal static class ProcScan
{
    public static IReadOnlyList<RunChildProcess> List(string launchId, IReadOnlyList<string> ignore)
    {
        var found = new List<RunChildProcess>();
        if (!Directory.Exists("/proc")) return found;
        var marker = $"{AgentChildren.LaunchVariable}={launchId}";
        foreach (var dir in Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(dir), out var pid) || pid == Environment.ProcessId) continue;
            try
            {
                if (!EnvironmentOf(dir).Contains(marker, StringComparer.Ordinal) || IsZombie(dir)) continue;
                var command = CommandLine(dir);
                if (string.IsNullOrWhiteSpace(command) || ignore.Any(part => command.Contains(part, StringComparison.Ordinal))) continue;
                found.Add(new RunChildProcess(pid, command, OutputFile(dir)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
        return found;
    }

    public static IReadOnlyList<RunChildProcess> StillRunning(IReadOnlyList<RunChildProcess> processes)
        => processes.Where(Same).ToList();

    private static bool Same(RunChildProcess process)
    {
        var dir = $"/proc/{process.Pid}";
        try { return Directory.Exists(dir) && !IsZombie(dir) && CommandLine(dir) == process.CommandLine; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    public static async Task StopAsync(IReadOnlyList<RunChildProcess> processes, CancellationToken ct)
    {
        foreach (var process in StillRunning(processes))
        {
            try
            {
                using var live = Process.GetProcessById(process.Pid);
                live.Kill(entireProcessTree: true);
                using var grace = CancellationTokenSource.CreateLinkedTokenSource(ct);
                grace.CancelAfter(TimeSpan.FromSeconds(5));
                await live.WaitForExitAsync(grace.Token);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or OperationCanceledException or System.ComponentModel.Win32Exception)
            {
            }
        }
    }

    private static string[] EnvironmentOf(string dir)
        => Encoding.UTF8.GetString(File.ReadAllBytes($"{dir}/environ")).Split('\0');

    private static string CommandLine(string dir)
        => Encoding.UTF8.GetString(File.ReadAllBytes($"{dir}/cmdline")).Replace('\0', ' ').Replace('\n', ' ').Replace('\t', ' ').TrimEnd();

    private static bool IsZombie(string dir)
        => File.ReadLines($"{dir}/status").FirstOrDefault(line => line.StartsWith("State:", StringComparison.Ordinal))?.Contains('Z') == true;

    private static string? OutputFile(string dir)
    {
        try
        {
            var target = new FileInfo($"{dir}/fd/1").LinkTarget;
            return target is not null && File.Exists(target) ? target : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }
}
