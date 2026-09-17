using ILD.Core.Services.Implementations;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ILD.Tests;

/// <summary>
/// The startup sweep covers every token-bearing file ILD writes for an agent:
/// MCP configs a killed process left, the pi extensions of runs and chats that
/// are no longer active, and the legacy <c>extensions/ild.ts</c> in pi's agent
/// directories. Temporary roots stand in for the process-wide ones, which other
/// tests write in parallel.
/// </summary>
public sealed class AgentRunFilesTests : IDisposable
{
    private readonly string _readRoot = Directory.CreateTempSubdirectory("ild-run-files-read-").FullName;
    private readonly string _scratchRoot = Directory.CreateTempSubdirectory("ild-run-files-scratch-").FullName;

    public void Dispose()
    {
        foreach (var dir in new[] { _readRoot, _scratchRoot })
        {
            if (OperatingSystem.IsLinux())
            {
                foreach (var nested in Directory.GetDirectories(dir, "*", SearchOption.AllDirectories))
                {
                    try { File.SetUnixFileMode(nested, File.GetUnixFileMode(nested) | UnixFileMode.UserWrite); } catch { /* best effort */ }
                }
            }
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task The_startup_sweep_clears_every_token_bearing_path_a_killed_process_left()
    {
        var started = DateTime.UtcNow;
        var active = Guid.NewGuid();
        var finished = Guid.NewGuid();

        var configs = Directory.CreateDirectory(Path.Combine(_readRoot, "ild-mcp-config")).FullName;
        var leftConfig = Path.Combine(configs, $"ild-copilot-mcp-{finished:N}-1.json");
        var liveConfig = Path.Combine(configs, $"ild-claude-mcp-{active:N}-2.json");
        File.WriteAllText(leftConfig, "{\"token\":\"t\"}");
        File.WriteAllText(liveConfig, "{\"token\":\"t\"}");
        File.SetLastWriteTimeUtc(leftConfig, started.AddMinutes(-5));
        File.SetLastWriteTimeUtc(liveConfig, started.AddSeconds(1));

        var activeExtension = Directory.CreateDirectory(Path.Combine(_readRoot, "ild-pi-ext", active.ToString("N"))).FullName;
        var finishedExtension = Directory.CreateDirectory(Path.Combine(_readRoot, "ild-pi-ext", finished.ToString("N"))).FullName;
        File.WriteAllText(Path.Combine(activeExtension, "ild.ts"), "token");
        File.WriteAllText(Path.Combine(finishedExtension, "ild.ts"), "token");

        var legacy = Directory.CreateDirectory(Path.Combine(_scratchRoot, "ild-pi-agent", active.ToString("N"), "extensions")).FullName;
        File.WriteAllText(Path.Combine(legacy, "ild.ts"), "const API_TOKEN = \"old-token\";");

        Assert.True(await AgentRunFiles.SweepAtStartupAsync(
            new HashSet<Guid> { active }, _readRoot, _scratchRoot, started, NullLogger.Instance, CancellationToken.None));

        Assert.Equal(new[] { liveConfig }, Directory.GetFiles(configs));
        Assert.True(Directory.Exists(activeExtension), "an active run's extension was swept");
        Assert.False(Directory.Exists(finishedExtension), "a finished run's extension, with its token, was left");
        Assert.False(File.Exists(Path.Combine(legacy, "ild.ts")));
    }

    [Fact]
    public async Task A_file_the_sweep_cannot_remove_is_logged_and_everything_after_it_is_still_swept()
    {
        // root can delete a read-only tree, so the failure cannot be staged there.
        if (!OperatingSystem.IsLinux() || Environment.UserName == "root") return;

        var started = DateTime.UtcNow;
        var configs = Directory.CreateDirectory(Path.Combine(_readRoot, "ild-mcp-config")).FullName;
        var stuckConfig = Path.Combine(configs, $"ild-copilot-mcp-{Guid.NewGuid():N}-1.json");
        File.WriteAllText(stuckConfig, "{\"token\":\"t\"}");
        File.SetLastWriteTimeUtc(stuckConfig, started.AddMinutes(-5));
        File.SetUnixFileMode(configs, UnixFileMode.UserRead | UnixFileMode.UserExecute);

        var stuckExtension = Directory.CreateDirectory(Path.Combine(_readRoot, "ild-pi-ext", Guid.NewGuid().ToString("N"))).FullName;
        var locked = Directory.CreateDirectory(Path.Combine(stuckExtension, "locked")).FullName;
        File.WriteAllText(Path.Combine(locked, "ild.ts"), "token");
        File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        var sweptExtension = Directory.CreateDirectory(Path.Combine(_readRoot, "ild-pi-ext", Guid.NewGuid().ToString("N"))).FullName;
        File.WriteAllText(Path.Combine(sweptExtension, "ild.ts"), "token");

        var legacy = Directory.CreateDirectory(Path.Combine(_scratchRoot, "ild-pi-agent", Guid.NewGuid().ToString("N"), "extensions")).FullName;
        File.WriteAllText(Path.Combine(legacy, "ild.ts"), "const API_TOKEN = \"old-token\";");

        var logger = new RecordingLogger();
        Assert.False(await AgentRunFiles.SweepAtStartupAsync(
            new HashSet<Guid>(), _readRoot, _scratchRoot, started, logger, CancellationToken.None));

        Assert.False(Directory.Exists(sweptExtension), "a file the sweep could not remove kept the next extension");
        Assert.False(File.Exists(Path.Combine(legacy, "ild.ts")), "a file the sweep could not remove kept the legacy extension");
        Assert.Contains(logger.Warnings, w => w.Contains(stuckConfig));
        Assert.Contains(logger.Warnings, w => w.Contains(stuckExtension));
    }

    [Fact]
    public async Task The_startup_sweep_is_fine_with_nothing_to_sweep()
    {
        Assert.True(await AgentRunFiles.SweepAtStartupAsync(
            new HashSet<Guid>(), _readRoot, _scratchRoot, DateTime.UtcNow, NullLogger.Instance, CancellationToken.None));
    }

    private sealed class RecordingLogger : ILogger
    {
        public List<string> Warnings { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
                Warnings.Add(formatter(state, exception));
        }
    }
}
