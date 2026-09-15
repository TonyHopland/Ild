using System.Diagnostics;
using System.Text.Json;
using ILD.Core.Services.Implementations;
using ILD.Core.Services.Implementations.Adapters;
using ILD.Data.DTOs;
using ILD.Data.Entities;

namespace ILD.Tests;

/// <summary>
/// Pi's agent and session directories sit in shared scratch, where the agent can
/// leave anything: a file or a dangling link where a directory belongs, or a
/// directory where the old <c>extensions/ild.ts</c> was. Before ILD tools came from
/// the MCP server, that file was an HTTP-calling extension carrying the token of
/// its day, and pi loads it before any <c>-e</c> path, keeping the first tool of a
/// name. Nothing left there may fail a turn, the old extension must go, and the
/// ones of runs that never launch again are swept. One test briefly replaces the
/// shared scratch segments with links, so this runs with every class that uses
/// them in the non-parallel environment collection.
/// </summary>
[Collection("EnvironmentPath")]
public sealed class PiAdapterAgentDirectoryTests : IDisposable
{
    private readonly Guid _runId = Guid.NewGuid();
    private readonly string _worktree = Directory.CreateTempSubdirectory("ild-pi-agent-dir-").FullName;

    private string AgentDirectory => Path.Combine(AgentIsolation.ScratchRoot, "ild-pi-agent", _runId.ToString("N"));

    private string SessionDirectory => Path.Combine(AgentIsolation.ScratchRoot, "ild-pi-sessions", _runId.ToString("N"));

    private string LegacyExtension => Path.Combine(AgentDirectory, "extensions", "ild.ts");

    public void Dispose()
    {
        foreach (var path in new[]
        {
            Path.Combine(AgentIsolation.AgentReadRoot, "ild-pi-ext", _runId.ToString("N")),
            AgentDirectory,
            SessionDirectory,
            _worktree,
        })
        {
            try
            {
                if (new FileInfo(path).LinkTarget is not null || File.Exists(path)) File.Delete(path);
                else if (Directory.Exists(path)) Directory.Delete(path, true);
            }
            catch { /* best effort */ }
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_file_or_dangling_link_where_a_pi_directory_belongs_never_fails_the_turn(bool linkAtAgentDirectory)
    {
        var (link, file) = linkAtAgentDirectory ? (AgentDirectory, SessionDirectory) : (SessionDirectory, AgentDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        File.CreateSymbolicLink(link, Path.Combine(_worktree, "nowhere"));
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "planted");

        await RunAsync();

        Assert.True(Directory.Exists(AgentDirectory));
        Assert.True(Directory.Exists(SessionDirectory));
        Assert.True(File.Exists(Path.Combine(AgentDirectory, "models.json")));
    }

    [Fact]
    public async Task A_link_to_a_directory_the_agent_cannot_write_at_the_pi_directories_never_fails_the_turn()
    {
        // /usr stands in for a directory the agent cannot write; root could write it.
        if (!OperatingSystem.IsLinux() || Environment.UserName == "root") return;

        foreach (var directory in new[] { AgentDirectory, SessionDirectory })
        {
            Directory.CreateDirectory(Path.GetDirectoryName(directory)!);
            Directory.CreateSymbolicLink(directory, "/usr");
        }

        await RunAsync();

        Assert.Null(new DirectoryInfo(AgentDirectory).LinkTarget);
        Assert.Null(new DirectoryInfo(SessionDirectory).LinkTarget);
        Assert.True(File.Exists(Path.Combine(AgentDirectory, "models.json")));
    }

    [Fact]
    public async Task A_link_to_a_directory_the_agent_cannot_write_at_the_shared_segments_never_fails_the_turn()
    {
        if (!OperatingSystem.IsLinux() || Environment.UserName == "root") return;

        var segments = new[] { Path.GetDirectoryName(AgentDirectory)!, Path.GetDirectoryName(SessionDirectory)! };
        var aside = segments.ToDictionary(s => s, s => $"{s}.aside-{Guid.NewGuid():N}");
        foreach (var segment in segments)
        {
            if (Directory.Exists(segment)) Directory.Move(segment, aside[segment]);
            Directory.CreateSymbolicLink(segment, "/usr");
        }

        try
        {
            await RunAsync();

            foreach (var segment in segments)
                Assert.Null(new DirectoryInfo(segment).LinkTarget);
            Assert.True(File.Exists(Path.Combine(AgentDirectory, "models.json")));
        }
        finally
        {
            foreach (var segment in segments)
            {
                if (new DirectoryInfo(segment).LinkTarget is not null) File.Delete(segment);
                else if (Directory.Exists(segment)) Directory.Delete(segment, recursive: true);
                if (Directory.Exists(aside[segment])) Directory.Move(aside[segment], segment);
            }
        }
    }

    [Fact]
    public async Task A_resumed_run_no_longer_loads_the_old_http_extension_from_its_agent_dir()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(LegacyExtension)!);
        File.WriteAllText(LegacyExtension, "const API_TOKEN = \"old-token\";");

        await RunAsync();

        Assert.Equal("absent", File.ReadAllText(Path.Combine(_worktree, "legacy-at-launch.txt")).Trim());
        Assert.False(File.Exists(LegacyExtension), "the old ild.ts, with its token, was left in the agent dir");
        Assert.True(File.Exists(Path.Combine(AgentDirectory, "models.json")), "the agent dir itself must stay in use");
    }

    [Fact]
    public async Task A_directory_planted_in_place_of_the_old_extension_is_removed_and_never_fails_the_launch()
    {
        var locked = Path.Combine(LegacyExtension, "locked");
        Directory.CreateDirectory(locked);
        File.WriteAllText(Path.Combine(locked, "file"), "x");
        if (OperatingSystem.IsLinux())
            File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserExecute);

        await RunAsync();

        Assert.Equal("absent", File.ReadAllText(Path.Combine(_worktree, "legacy-at-launch.txt")).Trim());
        Assert.False(Path.Exists(LegacyExtension));
    }

    [Fact]
    public async Task The_startup_sweep_removes_old_extensions_from_every_agent_dir()
    {
        var scratch = Directory.CreateTempSubdirectory("ild-pi-legacy-sweep-").FullName;
        try
        {
            var first = Path.Combine(scratch, "ild-pi-agent", "run-1");
            var second = Path.Combine(scratch, "ild-pi-agent", "run-2");
            Directory.CreateDirectory(Path.Combine(first, "extensions"));
            File.WriteAllText(Path.Combine(first, "extensions", "ild.ts"), "const API_TOKEN = \"old-token\";");
            File.WriteAllText(Path.Combine(first, "models.json"), "{}");
            Directory.CreateDirectory(Path.Combine(second, "extensions", "ild.ts"));

            Assert.True(await PiAdapter.SweepLegacyExtensionsAsync(scratch, CancellationToken.None));

            Assert.False(Path.Exists(Path.Combine(first, "extensions", "ild.ts")));
            Assert.False(Path.Exists(Path.Combine(second, "extensions", "ild.ts")));
            Assert.True(File.Exists(Path.Combine(first, "models.json")));
            Assert.True(await PiAdapter.SweepLegacyExtensionsAsync(Path.Combine(scratch, "missing"), CancellationToken.None));
        }
        finally
        {
            Directory.Delete(scratch, recursive: true);
        }
    }

    private async Task RunAsync()
    {
        var result = await new PiAdapter().ExecuteAsync(new AgentExecutionContext(
            Provider: new AiProvider
            {
                Name = "vllm-provider",
                Type = "pi",
                BaseUrl = "http://localhost:8000/v1",
                Model = "openai/my-model",
                Config = JsonSerializer.Serialize(new { binaryPath = WriteRecordingPi() }),
            },
            Prompt: "test prompt",
            RunContext: new LoopRunContext(_runId, "wi", "t", "d", _worktree, "main", new List<string>(), null),
            ExecutionCount: 2,
            Cancel: CancellationToken.None));

        Assert.True(result.Success, result.Error);
    }

    /// <summary>A stand-in pi that records whether anything sat at the old extension path when it started.</summary>
    private string WriteRecordingPi()
    {
        var script = Path.Combine(_worktree, "pi.sh");
        File.WriteAllText(script,
            "#!/bin/sh\n" +
            "if [ -e \"$PI_CODING_AGENT_DIR/extensions/ild.ts\" ]; then state=present; else state=absent; fi\n" +
            $"echo \"$state\" > '{_worktree}/legacy-at-launch.txt'\n" +
            "cat >/dev/null\n" +
            "echo '{\"type\":\"session\",\"version\":3,\"id\":\"pi-session-agent-dir\",\"cwd\":\"/w\"}'\n" +
            "echo '{\"type\":\"message_end\",\"message\":{\"role\":\"assistant\",\"content\":[{\"text\":\"ok\"}]}}'\n");
        var chmod = new ProcessStartInfo("chmod") { UseShellExecute = false };
        chmod.ArgumentList.Add("+x");
        chmod.ArgumentList.Add(script);
        using var process = Process.Start(chmod)!;
        process.WaitForExit();
        return script;
    }
}
