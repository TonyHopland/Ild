using ILD.Core.Services.Implementations.Adapters;
using ILD.Core.Services.RunHosts;
using ILD.Data.DTOs;
using ILD.Data.Entities;
using ILD.Tests.Fakes;
using Microsoft.Extensions.DependencyInjection;

namespace ILD.Tests;

/// <summary>
/// An adapter tells the run host which files it wrote for a launch, so the host copies exactly those into an
/// isolated run and never searches the launch's text for paths.
/// </summary>
public class AgentLaunchFilesTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "ild-launch-files-" + Guid.NewGuid().ToString("N"));
    private readonly string _readRoot;
    private readonly TestProcessEnvironment _environment;
    private readonly FakeRunHost _host;
    private readonly IServiceScopeFactory _scopes;
    private readonly string _worktree;

    public AgentLaunchFilesTests()
    {
        _readRoot = Path.Combine(_tempDir, "read");
        _worktree = Path.Combine(_tempDir, "work");
        Directory.CreateDirectory(_tempDir);
        var dll = Path.Combine(_tempDir, "ild-mcp-server.dll");
        File.WriteAllText(dll, "");
        _environment = new TestProcessEnvironment
        {
            { "ILD_MCP_SERVER_DLL", dll },
            { "ILD_API_URL", "http://api.invalid:1234" },
            { "ILD_API_TOKEN", "test-token" },
            { "ILD_AGENT_READ_ROOT", _readRoot },
        };
        _host = new FakeRunHost().WithDirectory(_worktree);
        var services = new ServiceCollection();
        services.AddSingleton<IRunHost>(_host);
        _scopes = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private AgentExecutionContext Context(string type, IReadOnlyList<string>? agentFiles = null) => new(
        Provider: new AiProvider
        {
            Name = type + "-test",
            Type = type,
            BaseUrl = string.Empty,
            Model = string.Empty,
            Config = "{\"binaryPath\":\"/bin/agent\"}",
        },
        Prompt: "see " + _readRoot + "/",
        RunContext: new LoopRunContext(Guid.NewGuid(), Guid.NewGuid().ToString(), "t", "d", _worktree, "main", [], null),
        ExecutionCount: 0,
        Cancel: TestContext.Current.CancellationToken,
        AgentFiles: agentFiles);

    [Fact]
    public async Task Claude_declares_the_mcp_config_it_wrote()
    {
        await new ClaudeCodeAdapter(_scopes, environment: _environment).ExecuteAsync(Context("claude-code"));

        var command = Assert.Single(_host.Started);
        var file = Assert.Single(command.Files);
        Assert.StartsWith(_readRoot, file);
        Assert.Contains(file, command.Start.ArgumentList);
    }

    [Fact]
    public async Task Copilot_declares_the_mcp_config_it_wrote()
    {
        await new CopilotAdapter(_scopes, environment: _environment).ExecuteAsync(Context("copilot"));

        var command = Assert.Single(_host.Started);
        var file = Assert.Single(command.Files);
        Assert.StartsWith(_readRoot, file);
        Assert.Contains("@" + file, command.Start.ArgumentList);
    }

    [Fact]
    public async Task Files_the_execution_carries_are_declared_with_the_launchs_own()
    {
        var feed = Path.Combine(_readRoot, "ild-package-feeds", "x.npmrc");

        await new ClaudeCodeAdapter(_scopes, environment: _environment).ExecuteAsync(Context("claude-code", [feed]));

        var command = Assert.Single(_host.Started);
        Assert.Equal(2, command.Files.Count);
        Assert.Equal(feed, command.Files[0]);
    }
}
