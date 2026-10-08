using ILD.Core.Services.Implementations.Adapters;
using ILD.Data.DTOs;
using ILD.Data.Entities;

namespace ILD.Tests;

/// <summary>
/// Verifies that the ClaudeCodeAdapter advertises the ILD MCP server via
/// <c>--mcp-config</c> so the headless <c>claude</c> CLI can call list/create
/// tools against the agent-scoped API. Without this, Claude has no way to
/// discover the ILD MCP — the user's <c>~/.claude</c> config is irrelevant
/// when the agent runs inside an isolated worktree without prior setup.
/// </summary>
public class ClaudeCodeAdapterMcpInjectionTests : IDisposable
{
    private readonly string _tempDir;
    private readonly TestProcessEnvironment _environment;

    public ClaudeCodeAdapterMcpInjectionTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "ild-claude-mcp-test-" + Guid.NewGuid().ToString("N"));
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
    public void BuildIldMcpEntry_emits_claude_shape_with_run_id_and_credentials()
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

        var entry = ClaudeCodeAdapter.BuildIldMcpEntry(ctx, environment: _environment);
        Assert.NotNull(entry);

        // Claude's --mcp-config uses the standard MCP shape:
        // { "command": ..., "args": [...], "env": {...} } — distinct from
        // opencode's { "type": "local", "command": [...], "environment": {...} }.
        Assert.Equal("dotnet", entry!["command"]);
        var args = (string[])entry["args"]!;
        Assert.Single(args);
        Assert.EndsWith("ild-mcp-server.dll", args[0]);

        var env = (Dictionary<string, object?>)entry["env"]!;
        Assert.Equal("http://api.invalid:1234", env["ILD_API_URL"]);
        Assert.Equal("test-token", env["ILD_API_TOKEN"]);
        Assert.Equal(runId.ToString(), env["ILD_LOOP_RUN_ID"]);
    }

    [Fact]
    public void BuildIldMcpEntry_tells_the_server_the_chat_session_id_for_a_chat_turn()
    {
        var chatSessionId = Guid.NewGuid();
        var ctx = new LoopRunContext(
            LoopRunId: chatSessionId,
            WorkItemId: string.Empty,
            WorkItemTitle: string.Empty,
            WorkItemDescription: string.Empty,
            WorktreePath: "/tmp",
            BranchName: string.Empty,
            EventLogSummary: new List<string>(),
            PreviousNodeOutput: null);

        var entry = ClaudeCodeAdapter.BuildIldMcpEntry(ctx, chatSessionId, _environment);
        Assert.NotNull(entry);

        var env = (Dictionary<string, object?>)entry!["env"]!;
        // A chat turn stamps created work items with the chat session id, so the
        // server must be told that — and NOT a loop-run id.
        Assert.Equal(chatSessionId.ToString(), env["ILD_CHAT_SESSION_ID"]);
        Assert.False(env.ContainsKey("ILD_LOOP_RUN_ID"));
    }

    [Fact]
    public void TryWriteIldMcpConfig_writes_temp_file_with_mcpServers_payload()
    {
        var provider = new AiProvider
        {
            Name = "claude-test",
            Type = "claude-code",
            BaseUrl = string.Empty,
            ApiKey = null,
            Model = string.Empty,
            Config = null,
        };
        var runId = Guid.NewGuid();
        var ctx = new LoopRunContext(runId, "wi", "t", "d", "/tmp", "main", new List<string>(), null);

        var path = ClaudeCodeAdapter.TryWriteIldMcpConfig(provider, ctx, toolAllowlist: null, environment: _environment);
        Assert.NotNull(path);
        try
        {
            Assert.True(File.Exists(path));
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path!));
            var root = doc.RootElement;
            Assert.True(root.TryGetProperty("mcpServers", out var servers));
            Assert.True(servers.TryGetProperty("ild", out var ild));
            Assert.Equal("dotnet", ild.GetProperty("command").GetString());
            Assert.EndsWith("ild-mcp-server.dll", ild.GetProperty("args")[0].GetString());
            Assert.Equal(runId.ToString(), ild.GetProperty("env").GetProperty("ILD_LOOP_RUN_ID").GetString());
        }
        finally
        {
            try { File.Delete(path!); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void TryWriteIldMcpConfig_returns_null_when_ild_tool_not_in_allowlist()
    {
        var provider = new AiProvider
        {
            Name = "claude-test",
            Type = "claude-code",
            BaseUrl = string.Empty,
            ApiKey = null,
            Model = string.Empty,
            Config = null,
        };
        var ctx = new LoopRunContext(Guid.NewGuid(), "wi", "t", "d", "/tmp", "main", new List<string>(), null);

        // Explicit allowlist without "ild" — the MCP config must be skipped so
        // we don't expose the work-item API to nodes that opted out.
        var path = ClaudeCodeAdapter.TryWriteIldMcpConfig(provider, ctx, toolAllowlist: new[] { "read" }, environment: _environment);
        Assert.Null(path);
    }

    [Fact]
    public void BuildCustomMcpEntry_splits_command_head_from_args()
    {
        var server = new CustomMcpServer(
            Name: "chrome-devtools",
            Command: new[] { "npx", "-y", "chrome-devtools-mcp@latest", "--headless" },
            Args: new[] { "--isolated" },
            Env: new Dictionary<string, string> { ["FOO"] = "bar" });

        var entry = ClaudeCodeAdapter.BuildCustomMcpEntry(server);

        // Claude wants a command string plus a separate args array; the remaining
        // command tokens are prepended to the explicit args.
        Assert.Equal("npx", entry["command"]);
        Assert.Equal(new[] { "-y", "chrome-devtools-mcp@latest", "--headless", "--isolated" }, (string[])entry["args"]!);
        var env = (Dictionary<string, object?>)entry["env"]!;
        Assert.Equal("bar", env["FOO"]);
    }

    [Fact]
    public void BuildCustomMcpEntry_omits_empty_args_and_env()
    {
        var server = new CustomMcpServer(
            Name: "solo",
            Command: new[] { "run" },
            Args: Array.Empty<string>(),
            Env: new Dictionary<string, string>());

        var entry = ClaudeCodeAdapter.BuildCustomMcpEntry(server);

        Assert.Equal("run", entry["command"]);
        Assert.False(entry.ContainsKey("args"));
        Assert.False(entry.ContainsKey("env"));
    }

    [Fact]
    public void TryWriteIldMcpConfig_merges_custom_servers_alongside_ild()
    {
        var provider = new AiProvider
        {
            Name = "claude-test",
            Type = "claude-code",
            BaseUrl = string.Empty,
            ApiKey = null,
            Model = string.Empty,
            Config = System.Text.Json.JsonSerializer.Serialize(new
            {
                customMcpServersJson = """
                { "chrome-devtools": { "command": ["npx", "-y", "chrome-devtools-mcp@latest"] } }
                """,
            }),
        };
        var ctx = new LoopRunContext(Guid.NewGuid(), "wi", "t", "d", "/tmp", "main", new List<string>(), null);

        var path = ClaudeCodeAdapter.TryWriteIldMcpConfig(provider, ctx, toolAllowlist: new[] { "ild" }, environment: _environment);
        Assert.NotNull(path);
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path!));
            var servers = doc.RootElement.GetProperty("mcpServers");
            Assert.True(servers.TryGetProperty("ild", out _));
            Assert.True(servers.TryGetProperty("chrome-devtools", out var chrome));
            Assert.Equal("npx", chrome.GetProperty("command").GetString());
            Assert.Equal("-y", chrome.GetProperty("args")[0].GetString());
        }
        finally
        {
            try { File.Delete(path!); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void TryWriteIldMcpConfig_writes_custom_servers_even_when_ild_disabled()
    {
        var provider = new AiProvider
        {
            Name = "claude-test",
            Type = "claude-code",
            BaseUrl = string.Empty,
            ApiKey = null,
            Model = string.Empty,
            Config = System.Text.Json.JsonSerializer.Serialize(new
            {
                customMcpServersJson = """
                { "chrome-devtools": { "command": ["npx", "chrome-devtools-mcp@latest"] } }
                """,
            }),
        };
        var ctx = new LoopRunContext(Guid.NewGuid(), "wi", "t", "d", "/tmp", "main", new List<string>(), null);

        // "ild" NOT in the allowlist — the ild entry is skipped, but the custom
        // server must still be written so a provider variant can carry it.
        var path = ClaudeCodeAdapter.TryWriteIldMcpConfig(provider, ctx, toolAllowlist: new[] { "read" }, environment: _environment);
        Assert.NotNull(path);
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path!));
            var servers = doc.RootElement.GetProperty("mcpServers");
            Assert.False(servers.TryGetProperty("ild", out _));
            Assert.True(servers.TryGetProperty("chrome-devtools", out _));
        }
        finally
        {
            try { File.Delete(path!); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void TryWriteIldMcpConfig_ignores_malformed_custom_json_and_returns_null_when_nothing_left()
    {
        var provider = new AiProvider
        {
            Name = "claude-test",
            Type = "claude-code",
            BaseUrl = string.Empty,
            ApiKey = null,
            Model = string.Empty,
            Config = System.Text.Json.JsonSerializer.Serialize(new { customMcpServersJson = "{ not valid json" }),
        };
        var ctx = new LoopRunContext(Guid.NewGuid(), "wi", "t", "d", "/tmp", "main", new List<string>(), null);

        // Malformed custom JSON with ild disabled ⇒ nothing to write ⇒ null, and
        // crucially no throw.
        var path = ClaudeCodeAdapter.TryWriteIldMcpConfig(provider, ctx, toolAllowlist: new[] { "read" }, environment: _environment);
        Assert.Null(path);
    }

    [Fact]
    public void BuildRunProcessStartInfo_emits_mcp_config_flag_when_path_supplied()
    {
        var psi = ClaudeCodeAdapter.BuildRunProcessStartInfo(
            binaryPath: "claude",
            worktreePath: "/tmp/wt",
            prompt: "fix it",
            sessionId: null,
            mcpConfigPath: "/tmp/mcp.json");

        Assert.Contains("--mcp-config", psi.ArgumentList);
        var idx = psi.ArgumentList.IndexOf("--mcp-config");
        Assert.Equal("/tmp/mcp.json", psi.ArgumentList[idx + 1]);
    }

    [Fact]
    public void BuildRunProcessStartInfo_separates_prompt_from_variadic_mcp_config()
    {
        // Regression: with an mcp-config and no session, the prompt must not sit
        // directly after the variadic `--mcp-config` flag, or the claude CLI
        // swallows it as another config-file path (ENAMETOOLONG). A `--`
        // terminator must precede the prompt, which must be the final argument.
        var psi = ClaudeCodeAdapter.BuildRunProcessStartInfo(
            binaryPath: "claude",
            worktreePath: "/tmp/wt",
            prompt: "fix it",
            sessionId: null,
            mcpConfigPath: "/tmp/mcp.json");

        var args = psi.ArgumentList;
        Assert.Equal("fix it", args[^1]);
        Assert.Equal("--", args[^2]);

        var mcpIdx = args.IndexOf("--mcp-config");
        // The token immediately after the config path must be the terminator,
        // never the prompt itself.
        Assert.Equal("--", args[mcpIdx + 2]);
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
        var script = WriteRecordingClaude(worktree);
        var adapter = new ClaudeCodeAdapter(environment: _environment);

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
        ClaudeCodeAdapter adapter, string worktree, AgentExecutionContext context)
    {
        var captured = Path.Combine(worktree, "mcp-config.json");
        File.Delete(captured);

        var result = await adapter.ExecuteAsync(context);

        Assert.True(result.Success, result.Error);
        Assert.True(File.Exists(captured), "claude was not given --mcp-config");
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(captured));
        return doc.RootElement.GetProperty("mcpServers").GetProperty("ild").GetProperty("env").Clone();
    }

    private static AgentExecutionContext ChatTurnContext(
        string binaryPath, string worktree, Guid runId, Guid? chatSessionId, Guid? chatTurnId)
        => new(
            Provider: new AiProvider
            {
                Name = "claude-test",
                Type = "claude-code",
                BaseUrl = string.Empty,
                ApiKey = null,
                Model = string.Empty,
                Config = System.Text.Json.JsonSerializer.Serialize(new { binaryPath }),
            },
            Prompt: "fix it",
            RunContext: new LoopRunContext(runId, "wi", "t", "d", worktree, "main", new List<string>(), null),
            ExecutionCount: 1,
            Cancel: CancellationToken.None,
            ChatSessionId: chatSessionId,
            ChatTurnId: chatTurnId);

    /// <summary>
    /// A stand-in claude that copies the file it was handed with <c>--mcp-config</c>,
    /// since the adapter deletes the original, then completes a stream-json turn.
    /// </summary>
    private static string WriteRecordingClaude(string worktree)
    {
        var script = Path.Combine(worktree, "fake-claude.sh");
        File.WriteAllText(script,
            "#!/bin/sh\n" +
            "prev=''\n" +
            "for a in \"$@\"; do\n" +
            $"  if [ \"$prev\" = '--mcp-config' ]; then cp \"$a\" '{worktree}/mcp-config.json'; fi\n" +
            "  prev=\"$a\"\n" +
            "done\n" +
            PromptCapturingCli.ClaudeCodeTurn);
        var psi = new System.Diagnostics.ProcessStartInfo("chmod") { UseShellExecute = false };
        psi.ArgumentList.Add("+x");
        psi.ArgumentList.Add(script);
        using var chmod = System.Diagnostics.Process.Start(psi)!;
        chmod.WaitForExit();
        return script;
    }
}
