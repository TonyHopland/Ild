using System.Diagnostics;
using System.Text.Json;
using ILD.Core.Services.Implementations;
using ILD.Core.Services.Implementations.Adapters;
using ILD.Data.DTOs;
using ILD.Data.Entities;

namespace ILD.Tests;

/// <summary>
/// pi's <c>--tools</c> allowlist also filters extension tools, so it has to name
/// every ILD tool the server declares. PiAdapterTests only checks for an
/// <c>ild_</c> prefix; this checks the whole list against what
/// <see cref="IldMcpToolNames"/> reads from the server DLL the adapter launches.
/// </summary>
[Collection("EnvironmentPath")]
public sealed class PiAdapterToolListTests : IDisposable
{
    private readonly Guid _runId = Guid.NewGuid();
    private readonly string _worktree = Directory.CreateTempSubdirectory("ild-pi-tool-list-").FullName;

    public void Dispose()
    {
        foreach (var dir in new[]
        {
            Path.Combine(AgentIsolation.AgentReadRoot, "ild-pi-ext", _runId.ToString("N")),
            Path.Combine(AgentIsolation.ScratchRoot, "ild-pi-sessions", _runId.ToString("N")),
            _worktree,
        })
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task Tools_are_exactly_the_built_ins_plus_every_name_the_server_dll_declares()
    {
        var declared = IldMcpToolNames.Read(IldMcpServer.ResolveServerDll()!);
        Assert.NotEmpty(declared);

        var result = await new PiAdapter().ExecuteAsync(new AgentExecutionContext(
            Provider: new AiProvider
            {
                Name = "pi-test",
                Type = "pi",
                BaseUrl = string.Empty,
                Model = "openai/gpt-5",
                Config = JsonSerializer.Serialize(new { binaryPath = WriteRecordingPi() }),
            },
            Prompt: "test prompt",
            RunContext: new LoopRunContext(_runId, "wi", "t", "d", _worktree, "main", new List<string>(), null),
            ExecutionCount: 1,
            Cancel: CancellationToken.None));

        Assert.True(result.Success, result.Error);
        var argv = File.ReadAllLines(Path.Combine(_worktree, "argv.txt"));
        var tools = argv[Array.IndexOf(argv, "--tools") + 1].Split(',');

        var expected = new[] { "read", "grep", "find", "ls", "edit", "write", "bash" }
            .Concat(declared.Select(name => "ild_" + name));
        Assert.Equal(expected.OrderBy(n => n, StringComparer.Ordinal), tools.OrderBy(n => n, StringComparer.Ordinal));
    }

    /// <summary>A stand-in pi that records its argv and completes a turn.</summary>
    private string WriteRecordingPi()
    {
        var script = Path.Combine(_worktree, "pi.sh");
        File.WriteAllText(script,
            "#!/bin/sh\n" +
            $"printf '%s\\n' \"$@\" > '{_worktree}/argv.txt'\n" +
            "cat >/dev/null\n" +
            "echo '{\"type\":\"session\",\"version\":3,\"id\":\"pi-session-tools\",\"cwd\":\"/w\"}'\n" +
            "echo '{\"type\":\"message_end\",\"message\":{\"role\":\"assistant\",\"content\":[{\"text\":\"ok\"}]}}'\n");
        var chmod = new ProcessStartInfo("chmod") { UseShellExecute = false };
        chmod.ArgumentList.Add("+x");
        chmod.ArgumentList.Add(script);
        using var process = Process.Start(chmod)!;
        process.WaitForExit();
        return script;
    }
}
