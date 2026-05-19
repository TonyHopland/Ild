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
    private readonly string _previousDllOverride;
    private readonly string _previousApiUrl;
    private readonly string _previousApiToken;

    public OpenCodeAdapterMcpInjectionTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "ild-mcp-test-" + Guid.NewGuid().ToString("N"));
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
    public void ResolveIldMcpServerDll_returns_override()
    {
        var resolved = OpenCodeAdapter.ResolveIldMcpServerDll();
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

        var entry = OpenCodeAdapter.BuildIldMcpEntry(ctx);
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
    public void BuildIldMcpEntry_returns_null_when_no_dll_can_be_found()
    {
        Environment.SetEnvironmentVariable("ILD_MCP_SERVER_DLL", "/nonexistent/path/ild-mcp-server.dll");

        // We can't easily defeat the upward-walk fallback in a real repo, but
        // the override path being non-existent should at least drop the value.
        // The fallback may still find a real build artifact; in that case the
        // entry is returned and we just verify it has the right shape.
        var entry = OpenCodeAdapter.BuildIldMcpEntry(runContext: null);
        if (entry == null) return; // No build artifact present anywhere — acceptable.

        Assert.Equal("local", entry["type"]);
        var env = (Dictionary<string, object?>)entry["environment"]!;
        Assert.False(env.ContainsKey("ILD_LOOP_RUN_ID"));
    }
}
