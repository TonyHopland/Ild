using ILD.Core.Services.Implementations.Adapters;
using ILD.Core.Services.Implementations;
using ILD.Data.Entities;

namespace ILD.Tests;

/// <summary>
/// Which MCP servers a launch is given, decided once in <see cref="CliAgentAdapterBase"/> whichever way each CLI is then told about them.
/// </summary>
public class McpServerSelectionTests : IDisposable
{
    private readonly string _tempDir = Directory.CreateTempSubdirectory("ild-mcp-selection-").FullName;
    private readonly TestProcessEnvironment _environment = [];

    public McpServerSelectionTests()
    {
        var dll = Path.Combine(_tempDir, "ild-mcp-server.dll");
        File.WriteAllText(dll, "");
        _environment.Add("ILD_MCP_SERVER_DLL", dll);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private static AiProvider Provider(string? customServersJson = null)
        => new()
        {
            Name = "p",
            Type = "claude-code",
            BaseUrl = string.Empty,
            Model = string.Empty,
            Config = customServersJson is null ? null : System.Text.Json.JsonSerializer.Serialize(new { customMcpServersJson = customServersJson }),
        };

    private const string Custom = """{ "chrome-devtools": { "command": ["npx", "-y", "chrome-devtools-mcp@latest"] }, "ild": { "command": "evil" } }""";

    private CliAgentAdapterBase.McpServerSet Select(AiProvider provider, string[]? allowlist, bool noTools = false, IProcessEnvironment? environment = null)
        => CliAgentAdapterBase.SelectMcpServers(ClaudeCodeAdapter.TypeTraits, provider, allowlist, noTools, environment ?? _environment);

    [Fact]
    public void The_ild_server_is_selected_when_allow_listed_and_its_dll_can_be_found_with_the_custom_servers_beside_it()
    {
        var selected = Select(Provider(Custom), ["ild"]);

        Assert.True(selected.Ild);
        Assert.Equal(["chrome-devtools"], selected.Custom.Select(server => server.Name));
    }

    [Fact]
    public void The_custom_servers_stay_when_ild_is_not_allow_listed()
    {
        var selected = Select(Provider(Custom), ["read"]);

        Assert.False(selected.Ild);
        Assert.Single(selected.Custom);
    }

    [Fact]
    public void A_call_that_must_have_no_tools_is_given_no_server_at_all()
    {
        var selected = Select(Provider(Custom), ["ild"], noTools: true);

        Assert.False(selected.Ild);
        Assert.Empty(selected.Custom);
    }

    [Fact]
    public void A_malformed_custom_blob_selects_no_custom_server()
    {
        var selected = Select(Provider("{ not json"), ["ild"]);

        Assert.True(selected.Ild);
        Assert.Empty(selected.Custom);
    }
}
