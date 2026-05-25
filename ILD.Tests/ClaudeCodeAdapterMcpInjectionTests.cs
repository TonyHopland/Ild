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
    private readonly string _previousDllOverride;
    private readonly string _previousApiUrl;
    private readonly string _previousApiToken;

    public ClaudeCodeAdapterMcpInjectionTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "ild-claude-mcp-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        var fakeDll = Path.Combine(_tempDir, "ild-mcp-server.dll");
        File.WriteAllText(fakeDll, "");

        _previousDllOverride = Environment.GetEnvironmentVariable("ILD_MCP_SERVER_DLL") ?? string.Empty;
        _previousApiUrl = Environment.GetEnvironmentVariable("ILD_API_URL") ?? string.Empty;
        _previousApiToken = Environment.GetEnvironmentVariable("ILD_API_TOKEN") ?? string.Empty;

        Environment.SetEnvironmentVariable("ILD_MCP_SERVER_DLL", fakeDll);
        Environment.SetEnvironmentVariable("ILD_API_URL", "http://api.invalid:1234");
        Environment.SetEnvironmentVariable("ILD_API_TOKEN", "test-token");
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("ILD_MCP_SERVER_DLL", string.IsNullOrEmpty(_previousDllOverride) ? null : _previousDllOverride);
        Environment.SetEnvironmentVariable("ILD_API_URL", string.IsNullOrEmpty(_previousApiUrl) ? null : _previousApiUrl);
        Environment.SetEnvironmentVariable("ILD_API_TOKEN", string.IsNullOrEmpty(_previousApiToken) ? null : _previousApiToken);
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

        var entry = ClaudeCodeAdapter.BuildIldMcpEntry(ctx);
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

        var path = ClaudeCodeAdapter.TryWriteIldMcpConfig(provider, ctx, toolAllowlist: null);
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
        var path = ClaudeCodeAdapter.TryWriteIldMcpConfig(provider, ctx, toolAllowlist: new[] { "read" });
        Assert.Null(path);
    }

    [Fact]
    public void BuildRunProcessStartInfo_emits_mcp_config_flag_when_path_supplied()
    {
        var psi = ClaudeCodeAdapter.BuildRunProcessStartInfo(
            binaryPath: "claude",
            worktreePath: "/tmp/wt",
            renderedPrompt: "fix it",
            sessionId: null,
            mcpConfigPath: "/tmp/mcp.json");

        Assert.Contains("--mcp-config", psi.ArgumentList);
        var idx = psi.ArgumentList.IndexOf("--mcp-config");
        Assert.Equal("/tmp/mcp.json", psi.ArgumentList[idx + 1]);
    }
}
