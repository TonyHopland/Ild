using System.Diagnostics;
using System.Text.Json;
using ILD.Core.Services.Implementations;
using ILD.Core.Services.Implementations.Adapters;
using ILD.Data.DTOs;
using ILD.Data.Entities;

namespace ILD.Tests;

/// <summary>
/// Before ILD tools came from the MCP server, pi's ILD extension was written to
/// <c>&lt;agent dir&gt;/extensions/ild.ts</c>, and that agent dir is reused by later
/// turns of the same run or chat. pi loads it before any <c>-e</c> path and keeps
/// the first tool of a name, so a leftover would shadow the MCP tools. The agent
/// dir is agent-writable scratch, so whatever else sits there must neither be
/// followed nor block the launch.
/// </summary>
public sealed class PiAdapterLegacyExtensionTests : IDisposable
{
    private readonly Guid _runId = Guid.NewGuid();
    private readonly string _worktree = Directory.CreateTempSubdirectory("ild-pi-legacy-ext-").FullName;

    private string AgentDirectory => Path.Combine(AgentIsolation.ScratchRoot, "ild-pi-agent", _runId.ToString("N"));

    private string LegacyExtension => Path.Combine(AgentDirectory, "extensions", "ild.ts");

    public void Dispose()
    {
        foreach (var segment in new[] { "ild-pi-ext", "ild-pi-agent", "ild-pi-sessions" })
        {
            var dir = Path.Combine(AgentIsolation.ScratchRoot, segment, _runId.ToString("N"));
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { /* best effort */ }
        }
        try { Directory.Delete(_worktree, true); } catch { /* best effort */ }
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
    public async Task A_directory_in_place_of_the_old_extension_does_not_block_the_launch()
    {
        Directory.CreateDirectory(LegacyExtension);

        await RunAsync();

        Assert.True(Directory.Exists(LegacyExtension));
    }

    [Fact]
    public async Task A_symlinked_extensions_folder_is_not_followed()
    {
        var victim = Directory.CreateDirectory(Path.Combine(_worktree, "orchestrator-owned")).FullName;
        File.WriteAllText(Path.Combine(victim, "ild.ts"), "keep");
        Directory.CreateDirectory(AgentDirectory);
        Directory.CreateSymbolicLink(Path.Combine(AgentDirectory, "extensions"), victim);

        await RunAsync();

        Assert.True(File.Exists(Path.Combine(victim, "ild.ts")), "the cleanup deleted through the planted link");
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

    /// <summary>A stand-in pi that records whether the old extension was there when it started.</summary>
    private string WriteRecordingPi()
    {
        var script = Path.Combine(_worktree, "pi.sh");
        File.WriteAllText(script,
            "#!/bin/sh\n" +
            "if [ -f \"$PI_CODING_AGENT_DIR/extensions/ild.ts\" ]; then state=present; else state=absent; fi\n" +
            $"echo \"$state\" > '{_worktree}/legacy-at-launch.txt'\n" +
            "cat >/dev/null\n" +
            "echo '{\"type\":\"session\",\"version\":3,\"id\":\"pi-session-legacy\",\"cwd\":\"/w\"}'\n" +
            "echo '{\"type\":\"message_end\",\"message\":{\"role\":\"assistant\",\"content\":[{\"text\":\"ok\"}]}}'\n");
        var chmod = new ProcessStartInfo("chmod") { UseShellExecute = false };
        chmod.ArgumentList.Add("+x");
        chmod.ArgumentList.Add(script);
        using var process = Process.Start(chmod)!;
        process.WaitForExit();
        return script;
    }
}
