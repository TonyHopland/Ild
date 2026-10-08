using System.Diagnostics;
using System.Text.Json;
using ILD.Core.Services.Implementations.Adapters;
using ILD.Data.DTOs;
using ILD.Data.Entities;

namespace ILD.Tests;

/// <summary>
/// Copilot reaches ILD through the same ILD MCP server as the other adapters,
/// handed to the CLI as <c>--additional-mcp-config @&lt;file&gt;</c>. The file is
/// the only place the server's credentials travel, so it must never be inline in
/// argv and must be gone once the run ends.
/// </summary>
public class CopilotAdapterMcpInjectionTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _fakeDll;
    private readonly TestProcessEnvironment _environment;

    public CopilotAdapterMcpInjectionTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "ild-copilot-mcp-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _fakeDll = Path.Combine(_tempDir, "ild-mcp-server.dll");
        File.WriteAllText(_fakeDll, "");

        _environment = new TestProcessEnvironment
        {
            { "ILD_MCP_SERVER_DLL", _fakeDll },
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
    public void TryWriteMcpConfig_for_a_run_writes_ild_in_copilot_shape_scoped_to_the_run()
    {
        var runId = Guid.NewGuid();

        using var doc = ReadAndDelete(CopilotAdapter.TryWriteMcpConfig(Provider(), RunContext(runId), allowlist: null, environment: _environment));

        var ild = doc.RootElement.GetProperty("mcpServers").GetProperty("ild");
        Assert.Equal("local", ild.GetProperty("type").GetString());
        Assert.Equal(new[] { "*" }, ild.GetProperty("tools").EnumerateArray().Select(t => t.GetString()).ToArray());
        Assert.Equal("dotnet", ild.GetProperty("command").GetString());
        Assert.Equal(new[] { Path.GetFullPath(_fakeDll) }, ild.GetProperty("args").EnumerateArray().Select(a => a.GetString()).ToArray());

        var env = ild.GetProperty("env");
        Assert.Equal("http://api.invalid:1234", env.GetProperty("ILD_API_URL").GetString());
        Assert.Equal("test-token", env.GetProperty("ILD_API_TOKEN").GetString());
        Assert.Equal(runId.ToString(), env.GetProperty("ILD_LOOP_RUN_ID").GetString());
        Assert.False(env.TryGetProperty("ILD_CHAT_SESSION_ID", out _));
    }

    [Fact]
    public void TryWriteMcpConfig_for_a_chat_turn_scopes_the_server_to_the_chat_session()
    {
        var chatSessionId = Guid.NewGuid();

        using var doc = ReadAndDelete(CopilotAdapter.TryWriteMcpConfig(
            Provider(), RunContext(chatSessionId), allowlist: new[] { "ild" }, chatSessionId, environment: _environment));

        var env = doc.RootElement.GetProperty("mcpServers").GetProperty("ild").GetProperty("env");
        Assert.Equal(chatSessionId.ToString(), env.GetProperty("ILD_CHAT_SESSION_ID").GetString());
        Assert.False(env.TryGetProperty("ILD_LOOP_RUN_ID", out _));
        Assert.Equal("http://api.invalid:1234", env.GetProperty("ILD_API_URL").GetString());
    }

    [Fact]
    public void TryWriteMcpConfig_omits_the_token_when_none_is_configured()
    {
        _environment.Set("ILD_API_TOKEN", null);

        using var doc = ReadAndDelete(CopilotAdapter.TryWriteMcpConfig(Provider(), RunContext(Guid.NewGuid()), allowlist: null, environment: _environment));

        var env = doc.RootElement.GetProperty("mcpServers").GetProperty("ild").GetProperty("env");
        Assert.False(env.TryGetProperty("ILD_API_TOKEN", out _));
        Assert.True(env.TryGetProperty("ILD_API_URL", out _));
    }

    [Fact]
    public void TryWriteMcpConfig_merges_custom_servers_in_copilot_shape_and_keeps_ild_reserved()
    {
        var provider = Provider(customMcpServersJson: """
            {
              "chrome-devtools": { "command": ["npx", "-y", "chrome-devtools-mcp@latest"] },
              "ild": { "command": ["evil"] }
            }
            """);

        using var doc = ReadAndDelete(CopilotAdapter.TryWriteMcpConfig(provider, RunContext(Guid.NewGuid()), allowlist: new[] { "ild" }, environment: _environment));

        var servers = doc.RootElement.GetProperty("mcpServers");
        Assert.Equal("dotnet", servers.GetProperty("ild").GetProperty("command").GetString());

        var chrome = servers.GetProperty("chrome-devtools");
        Assert.Equal("npx", chrome.GetProperty("command").GetString());
        Assert.Equal(new[] { "-y", "chrome-devtools-mcp@latest" }, chrome.GetProperty("args").EnumerateArray().Select(a => a.GetString()).ToArray());
        Assert.Equal("local", chrome.GetProperty("type").GetString());
        Assert.Equal(new[] { "*" }, chrome.GetProperty("tools").EnumerateArray().Select(t => t.GetString()).ToArray());
    }

    [Fact]
    public void TryWriteMcpConfig_ignores_malformed_custom_json_and_still_writes_ild()
    {
        var provider = Provider(customMcpServersJson: "{ not valid json");

        using var doc = ReadAndDelete(CopilotAdapter.TryWriteMcpConfig(provider, RunContext(Guid.NewGuid()), allowlist: null, environment: _environment));

        var servers = doc.RootElement.GetProperty("mcpServers");
        Assert.Equal(new[] { "ild" }, servers.EnumerateObject().Select(p => p.Name).ToArray());
    }

    /// <summary>For Copilot an explicit empty list, or one with no "ild", means ILD off.</summary>
    private static readonly string[][] IldOffSelections = [[], ["read"]];

    [Fact]
    public void TryWriteMcpConfig_with_ild_off_writes_custom_servers_only()
    {
        var provider = Provider(customMcpServersJson: """
            { "chrome-devtools": { "command": ["npx", "chrome-devtools-mcp@latest"] } }
            """);

        foreach (var allowlist in IldOffSelections)
        {
            using var doc = ReadAndDelete(CopilotAdapter.TryWriteMcpConfig(provider, RunContext(Guid.NewGuid()), allowlist, environment: _environment));

            var servers = doc.RootElement.GetProperty("mcpServers");
            Assert.Equal(new[] { "chrome-devtools" }, servers.EnumerateObject().Select(p => p.Name).ToArray());
        }
    }

    [Fact]
    public void TryWriteMcpConfig_with_ild_off_and_no_custom_servers_writes_nothing()
    {
        foreach (var allowlist in IldOffSelections)
            Assert.Null(CopilotAdapter.TryWriteMcpConfig(Provider(), RunContext(Guid.NewGuid()), allowlist, environment: _environment));
    }

    [Fact]
    public void BuildRunProcessStartInfo_puts_the_mcp_config_after_the_dirs_and_before_the_prompt()
    {
        var psi = CopilotAdapter.BuildRunProcessStartInfo(
            binaryPath: "copilot",
            worktreePath: "/tmp/wt",
            prompt: "fix it",
            additionalAllowedDirectories: new[] { "/data/worktrees/wi-99" },
            mcpConfigPath: "/tmp/ild-copilot-mcp.json");

        Assert.Equal(new[]
        {
            "--allow-all-tools",
            "--no-color",
            "--add-dir",
            "/tmp/wt",
            "--add-dir",
            "/data/worktrees/wi-99",
            "--additional-mcp-config",
            "@/tmp/ild-copilot-mcp.json",
            "-p",
            "fix it",
        }, psi.ArgumentList);
    }

    [Fact]
    public async Task ExecuteAsync_hands_copilot_the_run_scoped_config_file_and_deletes_it_afterwards()
    {
        var runId = Guid.NewGuid();
        var worktree = CreateWorktree();
        var script = WriteRecordingCopilot(worktree, exitCode: 0);

        var result = await new CopilotAdapter(environment: _environment).ExecuteAsync(Context(script, worktree, runId));

        Assert.True(result.Success, result.Error);
        var argv = File.ReadAllLines(Path.Combine(worktree, "argv.txt"));
        var flag = Array.IndexOf(argv, "--additional-mcp-config");
        Assert.True(flag >= 0, "copilot was not given --additional-mcp-config");
        var configArg = argv[flag + 1];
        Assert.StartsWith("@", configArg);
        Assert.True(flag < Array.IndexOf(argv, "-p"));
        Assert.Equal("fix it", argv[^1]);
        Assert.DoesNotContain(argv, a => a.Contains("test-token"));

        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(worktree, "mcp-config.json")));
        var env = doc.RootElement.GetProperty("mcpServers").GetProperty("ild").GetProperty("env");
        Assert.Equal(runId.ToString(), env.GetProperty("ILD_LOOP_RUN_ID").GetString());
        Assert.False(env.TryGetProperty("ILD_CHAT_SESSION_ID", out _));

        Assert.False(File.Exists(configArg[1..]), $"the MCP config {configArg[1..]} was left behind");
    }

    [Fact]
    public async Task ExecuteAsync_for_a_chat_turn_scopes_the_config_to_the_chat_session()
    {
        var chatSessionId = Guid.NewGuid();
        var worktree = CreateWorktree();
        var script = WriteRecordingCopilot(worktree, exitCode: 0);

        var result = await new CopilotAdapter(environment: _environment).ExecuteAsync(
            Context(script, worktree, chatSessionId) with { ChatSessionId = chatSessionId });

        Assert.True(result.Success, result.Error);
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(worktree, "mcp-config.json")));
        var env = doc.RootElement.GetProperty("mcpServers").GetProperty("ild").GetProperty("env");
        Assert.Equal(chatSessionId.ToString(), env.GetProperty("ILD_CHAT_SESSION_ID").GetString());
        Assert.False(env.TryGetProperty("ILD_LOOP_RUN_ID", out _));
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
        var worktree = CreateWorktree();
        var script = WriteRecordingCopilot(worktree, exitCode: 0);
        var adapter = new CopilotAdapter(environment: _environment);

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

    [Fact]
    public async Task ExecuteAsync_deletes_the_config_file_when_copilot_fails()
    {
        var worktree = CreateWorktree();
        var script = WriteRecordingCopilot(worktree, exitCode: 1);

        var result = await new CopilotAdapter(environment: _environment).ExecuteAsync(Context(script, worktree, Guid.NewGuid()));

        Assert.False(result.Success);
        var argv = File.ReadAllLines(Path.Combine(worktree, "argv.txt"));
        var configArg = argv[Array.IndexOf(argv, "--additional-mcp-config") + 1];
        Assert.False(File.Exists(configArg[1..]), $"the MCP config {configArg[1..]} was left behind");
    }

    [Fact]
    public async Task ExecuteAsync_with_ild_off_and_no_custom_servers_passes_no_mcp_flag()
    {
        var worktree = CreateWorktree();
        var script = WriteRecordingCopilot(worktree, exitCode: 0);

        var result = await new CopilotAdapter(environment: _environment).ExecuteAsync(
            Context(script, worktree, Guid.NewGuid()) with { ToolAllowlist = Array.Empty<string>() });

        Assert.True(result.Success, result.Error);
        var argv = File.ReadAllLines(Path.Combine(worktree, "argv.txt"));
        Assert.DoesNotContain("--additional-mcp-config", argv);
        Assert.DoesNotContain(argv, a => a.StartsWith("@"));
    }

    private static AiProvider Provider(string? customMcpServersJson = null, string? binaryPath = null)
    {
        var config = new Dictionary<string, object?>();
        if (binaryPath is not null) config["binaryPath"] = binaryPath;
        if (customMcpServersJson is not null) config["customMcpServersJson"] = customMcpServersJson;

        return new AiProvider
        {
            Name = "copilot-test",
            Type = "copilot",
            BaseUrl = string.Empty,
            ApiKey = null,
            Model = string.Empty,
            Config = config.Count == 0 ? null : JsonSerializer.Serialize(config),
        };
    }

    private static LoopRunContext RunContext(Guid runId, string worktreePath = "/tmp")
        => new(runId, "wi", "t", "d", worktreePath, "main", new List<string>(), null);

    private static AgentExecutionContext Context(string binaryPath, string worktreePath, Guid runId)
        => new(
            Provider: Provider(binaryPath: binaryPath),
            Prompt: "fix it",
            RunContext: RunContext(runId, worktreePath),
            ExecutionCount: 1,
            Cancel: CancellationToken.None);

    private static AgentExecutionContext ChatTurnContext(
        string binaryPath, string worktreePath, Guid runId, Guid? chatSessionId, Guid? chatTurnId)
        => new(
            Provider: Provider(binaryPath: binaryPath),
            Prompt: "fix it",
            RunContext: RunContext(runId, worktreePath),
            ExecutionCount: 1,
            Cancel: CancellationToken.None,
            ChatSessionId: chatSessionId,
            ChatTurnId: chatTurnId);

    private static async Task<JsonElement> IldServerEnvironmentAsync(
        CopilotAdapter adapter, string worktree, AgentExecutionContext context)
    {
        var captured = Path.Combine(worktree, "mcp-config.json");
        File.Delete(captured);

        var result = await adapter.ExecuteAsync(context);

        Assert.True(result.Success, result.Error);
        Assert.True(File.Exists(captured), "copilot was not given --additional-mcp-config");
        using var doc = JsonDocument.Parse(File.ReadAllText(captured));
        return doc.RootElement.GetProperty("mcpServers").GetProperty("ild").GetProperty("env").Clone();
    }

    private static JsonDocument ReadAndDelete(string? path)
    {
        Assert.NotNull(path);
        try
        {
            return JsonDocument.Parse(File.ReadAllText(path!));
        }
        finally
        {
            try { File.Delete(path!); } catch { /* best effort */ }
        }
    }

    private string CreateWorktree()
    {
        var path = Path.Combine(_tempDir, "wt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>
    /// A stand-in copilot that records its argv (one per line) and copies the
    /// <c>@file</c> it was handed, since the adapter deletes the original.
    /// </summary>
    private static string WriteRecordingCopilot(string worktree, int exitCode)
    {
        var script = Path.Combine(worktree, "fake-copilot.sh");
        File.WriteAllText(script,
            "#!/bin/sh\n" +
            $"printf '%s\\n' \"$@\" > '{worktree}/argv.txt'\n" +
            "for a in \"$@\"; do\n" +
            "  case \"$a\" in\n" +
            $"    @*) cp \"${{a#@}}\" '{worktree}/mcp-config.json' ;;\n" +
            "  esac\n" +
            "done\n" +
            "echo 'done'\n" +
            $"exit {exitCode}\n");
        var psi = new ProcessStartInfo("chmod") { UseShellExecute = false };
        psi.ArgumentList.Add("+x");
        psi.ArgumentList.Add(script);
        using var chmod = Process.Start(psi)!;
        chmod.WaitForExit();
        return script;
    }
}
