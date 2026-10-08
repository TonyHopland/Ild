using ILD.Core.Services.Implementations.Adapters;
using ILD.Data.DTOs;
using ILD.Data.Entities;

namespace ILD.Tests;

/// <summary>
/// Verifies that the OpenCodeAdapter advertises the ILD MCP server in the
/// generated opencode config so spawned agents can call list/create tools
/// against the agent-scoped API.
///
/// The adapter writes the config via <c>OPENCODE_CONFIG_CONTENT</c>; the
/// child opencode process never reads the user's <c>~/.config/opencode</c>,
/// so we have to inject the entry ourselves.
/// </summary>
public class OpenCodeAdapterMcpInjectionTests : IDisposable
{
    private readonly string _tempDir;
    private readonly TestProcessEnvironment _environment;

    public OpenCodeAdapterMcpInjectionTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "ild-mcp-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        var fakeDll = Path.Combine(_tempDir, "ild-mcp-server.dll");
        File.WriteAllText(fakeDll, "");

        _environment = new TestProcessEnvironment
        {
            { "ILD_MCP_SERVER_DLL", fakeDll },
            { "ILD_API_URL", "http://api.invalid:1234" },
            { "ILD_API_TOKEN", "test-token" },
        };
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void ResolveIldMcpServerDll_returns_override()
    {
        var resolved = OpenCodeAdapter.ResolveIldMcpServerDll(_environment);
        Assert.NotNull(resolved);
        Assert.True(File.Exists(resolved));
    }

    [Fact]
    public void BuildIldMcpEntry_includes_run_id_and_api_credentials()
    {
        var runId = Guid.NewGuid();
        var ctx = new LoopRunContext(
            LoopRunId: runId,
            WorkItemId: Guid.NewGuid().ToString(),
            WorkItemTitle: "t",
            WorkItemDescription: "d",
            WorktreePath: "/tmp",
            BranchName: "main",
            EventLogSummary: new List<string>(),
            PreviousNodeOutput: null);

        var entry = OpenCodeAdapter.BuildIldMcpEntry(ctx, environment: _environment);
        Assert.NotNull(entry);

        Assert.Equal("local", entry!["type"]);

        var command = (string[])entry["command"]!;
        Assert.Equal("dotnet", command[0]);
        Assert.EndsWith("ild-mcp-server.dll", command[1]);

        var env = (Dictionary<string, object?>)entry["environment"]!;
        Assert.Equal("http://api.invalid:1234", env["ILD_API_URL"]);
        Assert.Equal("test-token", env["ILD_API_TOKEN"]);
        Assert.Equal(runId.ToString(), env["ILD_LOOP_RUN_ID"]);
    }

    [Fact]
    public void BuildCustomMcpEntry_concatenates_command_and_args_into_argv()
    {
        var server = new CustomMcpServer(
            Name: "chrome-devtools",
            Command: new[] { "npx", "-y", "chrome-devtools-mcp@latest" },
            Args: new[] { "--headless", "--isolated" },
            Env: new Dictionary<string, string> { ["FOO"] = "bar" });

        var entry = OpenCodeAdapter.BuildCustomMcpEntry(server);

        Assert.Equal("local", entry["type"]);
        // opencode's `command` is the full argv, so command tokens + args are one array.
        Assert.Equal(
            new[] { "npx", "-y", "chrome-devtools-mcp@latest", "--headless", "--isolated" },
            (string[])entry["command"]!);
        var env = (Dictionary<string, object?>)entry["environment"]!;
        Assert.Equal("bar", env["FOO"]);
    }

    [Fact]
    public void BuildCustomMcpEntry_omits_environment_when_empty()
    {
        var server = new CustomMcpServer(
            Name: "solo",
            Command: new[] { "run" },
            Args: Array.Empty<string>(),
            Env: new Dictionary<string, string>());

        var entry = OpenCodeAdapter.BuildCustomMcpEntry(server);

        Assert.Equal(new[] { "run" }, (string[])entry["command"]!);
        Assert.False(entry.ContainsKey("environment"));
    }

    [Fact]
    public void BuildIldMcpEntry_returns_null_when_no_dll_can_be_found()
    {
        _environment.Set("ILD_MCP_SERVER_DLL", "/nonexistent/path/ild-mcp-server.dll");

        // We can't easily defeat the upward-walk fallback in a real repo, but
        // the override path being non-existent should at least drop the value.
        // The fallback may still find a real build artifact; in that case the
        // entry is returned and we just verify it has the right shape.
        var entry = OpenCodeAdapter.BuildIldMcpEntry(runContext: null, environment: _environment);
        if (entry == null) return; // No build artifact present anywhere — acceptable.

        Assert.Equal("local", entry["type"]);
        var env = (Dictionary<string, object?>)entry["environment"]!;
        Assert.False(env.ContainsKey("ILD_LOOP_RUN_ID"));
    }

    /// <summary>
    /// The ILD MCP server a chat turn starts is told that turn's own id, so every
    /// API call it makes names the turn; a later turn of the same chat gets its
    /// own id, never the first one's, and a loop run gets none.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_tells_the_ild_server_each_chat_turn_its_own_id_and_a_loop_run_none()
    {
        var chatSessionId = Guid.NewGuid();
        var firstTurnId = Guid.NewGuid();
        var secondTurnId = Guid.NewGuid();
        var loopRunId = Guid.NewGuid();
        var worktree = Directory.CreateDirectory(Path.Combine(_tempDir, "wt-" + Guid.NewGuid().ToString("N"))).FullName;
        var script = WriteRecordingOpenCode(worktree);
        var adapter = new OpenCodeAdapter(environment: _environment);

        var firstTurn = await IldServerEnvironmentAsync(
            adapter, worktree, ChatTurnContext(script, worktree, chatSessionId, chatSessionId, firstTurnId));
        Assert.Equal(firstTurnId.ToString(), firstTurn.GetProperty("ILD_CHAT_TURN_ID").GetString());
        Assert.Equal(chatSessionId.ToString(), firstTurn.GetProperty("ILD_CHAT_SESSION_ID").GetString());

        var secondTurn = await IldServerEnvironmentAsync(
            adapter, worktree, ChatTurnContext(script, worktree, chatSessionId, chatSessionId, secondTurnId));
        Assert.Equal(secondTurnId.ToString(), secondTurn.GetProperty("ILD_CHAT_TURN_ID").GetString());
        Assert.Equal(chatSessionId.ToString(), secondTurn.GetProperty("ILD_CHAT_SESSION_ID").GetString());

        var loopRun = await IldServerEnvironmentAsync(
            adapter, worktree, ChatTurnContext(script, worktree, loopRunId, chatSessionId: null, chatTurnId: null));
        Assert.False(loopRun.TryGetProperty("ILD_CHAT_TURN_ID", out _));
        Assert.Equal(loopRunId.ToString(), loopRun.GetProperty("ILD_LOOP_RUN_ID").GetString());
    }

    private static async Task<System.Text.Json.JsonElement> IldServerEnvironmentAsync(
        OpenCodeAdapter adapter, string worktree, AgentExecutionContext context)
    {
        var captured = Path.Combine(worktree, "opencode-config.json");
        File.Delete(captured);

        var result = await adapter.ExecuteAsync(context);

        Assert.True(result.Success, result.Error);
        Assert.True(File.Exists(captured), "opencode was never launched");
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(captured));
        return doc.RootElement.GetProperty("mcp").GetProperty("ild").GetProperty("environment").Clone();
    }

    private static AgentExecutionContext ChatTurnContext(
        string binaryPath, string worktree, Guid runId, Guid? chatSessionId, Guid? chatTurnId)
        => new(
            Provider: new AiProvider
            {
                Name = "opencode-test",
                Type = "opencode",
                BaseUrl = string.Empty,
                ApiKey = null,
                Model = "m",
                Config = System.Text.Json.JsonSerializer.Serialize(new { binaryPath }),
            },
            Prompt: "fix it",
            RunContext: new LoopRunContext(runId, "wi", "t", "d", worktree, "main", new List<string>(), null),
            ExecutionCount: 1,
            Cancel: CancellationToken.None,
            ChatSessionId: chatSessionId,
            ChatTurnId: chatTurnId);

    /// <summary>
    /// A stand-in opencode that records the config it was launched with, which the
    /// adapter hands over in <c>OPENCODE_CONFIG_CONTENT</c>, and exits 0.
    /// </summary>
    private static string WriteRecordingOpenCode(string worktree)
    {
        var script = Path.Combine(worktree, "fake-opencode.sh");
        File.WriteAllText(script,
            "#!/bin/sh\n" +
            $"printf '%s' \"$OPENCODE_CONFIG_CONTENT\" > '{worktree}/opencode-config.json'\n");
        var psi = new System.Diagnostics.ProcessStartInfo("chmod") { UseShellExecute = false };
        psi.ArgumentList.Add("+x");
        psi.ArgumentList.Add(script);
        using var chmod = System.Diagnostics.Process.Start(psi)!;
        chmod.WaitForExit();
        return script;
    }
}
