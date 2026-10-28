using System.Text.Json;
using ILD.Core.Services.Implementations;
using ILD.Core.Services.Implementations.Adapters;
using ILD.Data.DTOs;
using Microsoft.Extensions.Logging.Abstractions;

namespace ILD.Tests;

/// <summary>
/// What the ILD MCP server entry is built from, taken from an environment the
/// caller supplies: the API URL and token handed to the server, the DLL it runs,
/// and the agent read root its config file is written to and deleted from.
/// </summary>
public sealed class IldMcpServerEnvironmentTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("ild-mcp-env-root-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private static LoopRunContext Run(Guid runId)
        => new(runId, "wi", "t", "d", "/tmp", "main", new List<string>(), null);

    [Fact]
    public void The_server_is_told_the_api_url_and_token_of_the_supplied_environment()
    {
        var runId = Guid.NewGuid();
        var environment = new TestProcessEnvironment
        {
            { "ILD_API_URL", "http://api.invalid:1234" },
            { "ILD_API_TOKEN", "supplied-token" },
        };

        var env = IldMcpServer.BuildEnvironment(Run(runId), environment: environment);

        Assert.Equal("http://api.invalid:1234", env["ILD_API_URL"]);
        Assert.Equal("supplied-token", env["ILD_API_TOKEN"]);
        Assert.Equal(runId.ToString(), env["ILD_LOOP_RUN_ID"]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Without_a_url_or_token_the_server_gets_the_local_default_and_no_token(string? token)
    {
        var environment = new TestProcessEnvironment { { "ILD_API_TOKEN", token } };

        var env = IldMcpServer.BuildEnvironment(Run(Guid.NewGuid()), environment: environment);

        Assert.Equal("http://localhost:5000", env["ILD_API_URL"]);
        Assert.False(env.ContainsKey("ILD_API_TOKEN"));
    }

    [Fact]
    public void The_dll_named_by_the_supplied_environment_is_the_one_run()
    {
        var dll = Path.Combine(_root, "ild-mcp-server.dll");
        File.WriteAllText(dll, "");

        var resolved = IldMcpServer.ResolveServerDll(environment: new TestProcessEnvironment { { "ILD_MCP_SERVER_DLL", dll } });

        Assert.Equal(Path.GetFullPath(dll), resolved);
    }

    [Fact]
    public void A_config_file_is_written_under_the_supplied_read_root_and_deleted_from_there()
    {
        var runId = Guid.NewGuid();
        var otherRunId = Guid.NewGuid();
        var environment = new TestProcessEnvironment { { AgentIsolation.AgentReadRootEnvVar, _root } };

        var path = IldMcpServer.TryWriteConfigFile("test", runId, new { hello = "world" }, NullLogger.Instance, environment: environment);
        var other = IldMcpServer.TryWriteConfigFile("test", otherRunId, new { hello = "other" }, NullLogger.Instance, environment: environment);

        Assert.NotNull(path);
        Assert.NotNull(other);
        Assert.StartsWith(_root + Path.DirectorySeparatorChar, path);
        using (var doc = JsonDocument.Parse(File.ReadAllText(path!)))
            Assert.Equal("world", doc.RootElement.GetProperty("hello").GetString());

        IldMcpServer.DeleteConfigFiles(runId, environment: environment);

        Assert.False(File.Exists(path));
        Assert.True(File.Exists(other));
    }

    [Fact]
    public void A_supplied_agent_user_without_a_read_root_refuses_the_default_root()
    {
        var environment = new TestProcessEnvironment { { AgentIsolation.AgentUserEnvVar, "agent" } };

        Assert.Throws<InvalidOperationException>(() =>
            IldMcpServer.TryWriteConfigFile("test", Guid.NewGuid(), new { }, NullLogger.Instance, environment: environment));
    }
}
