using ILD.Core.Services.Implementations;
using ILD.Core.Services.Implementations.Adapters;
using ILD.Core.Services.Interfaces;
using ILD.Data.DTOs;
using ILD.Data.Entities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ILD.Tests;

/// <summary>
/// The registry builds a fresh adapter for every run through
/// <c>ActivatorUtilities</c>, not the container, so the adapter it builds must
/// still get the application logger for a failed MCP config write to be seen.
/// </summary>
[Collection("EnvironmentPath")]
public sealed class AgentAdapterRegistryLoggingTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("ild-registry-logging-root-").FullName;
    private readonly string _worktree = Directory.CreateTempSubdirectory("ild-registry-logging-wt-").FullName;
    private readonly string _configDir;
    private readonly string? _previousReadRoot;

    public AgentAdapterRegistryLoggingTests()
    {
        _configDir = Path.Combine(_root, "ild-mcp-config");
        _previousReadRoot = Environment.GetEnvironmentVariable(AgentIsolation.AgentReadRootEnvVar);
        Environment.SetEnvironmentVariable(AgentIsolation.AgentReadRootEnvVar, _root);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(AgentIsolation.AgentReadRootEnvVar, _previousReadRoot);
        if (OperatingSystem.IsLinux() && Directory.Exists(_configDir))
            File.SetUnixFileMode(_configDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        Directory.Delete(_root, recursive: true);
        Directory.Delete(_worktree, recursive: true);
    }

    [Theory]
    [InlineData("copilot")]
    [InlineData("claude-code")]
    public async Task An_adapter_the_registry_builds_for_a_run_logs_a_failed_mcp_config_write(string type)
    {
        // root can write into a read-only directory, so the failure cannot be staged there.
        if (!OperatingSystem.IsLinux() || Environment.UserName == "root") return;
        Directory.CreateDirectory(_configDir);
        File.SetUnixFileMode(_configDir, UnixFileMode.UserRead | UnixFileMode.UserExecute);

        var warnings = new WarningCollector();
        var services = new ServiceCollection();
        services.AddLogging(logging => logging.AddProvider(warnings));
        services.AddSingleton<IAgentAdapter, CopilotAdapter>();
        services.AddSingleton<IAgentAdapter, ClaudeCodeAdapter>();
        services.AddSingleton<IAgentAdapterRegistry, AgentAdapterRegistry>();
        using var serviceProvider = services.BuildServiceProvider();

        var provider = new AiProvider
        {
            Name = $"{type}-test",
            Type = type,
            BaseUrl = string.Empty,
            Model = string.Empty,
            // A binary that does not exist: the run fails after the config write, which is all this needs.
            Config = $$"""{ "binaryPath": "{{Path.Combine(_worktree, "missing-cli")}}", "customMcpServersJson": "{ \"docs\": { \"command\": \"npx\" } }" }""",
        };
        var adapter = serviceProvider.GetRequiredService<IAgentAdapterRegistry>().ResolveForProvider(provider)();

        await adapter.ExecuteAsync(new AgentExecutionContext(
            Provider: provider,
            Prompt: "fix it",
            RunContext: new LoopRunContext(Guid.NewGuid(), "wi", "t", "d", _worktree, "main", new List<string>(), null),
            ExecutionCount: 1,
            Cancel: CancellationToken.None));

        var warning = Assert.Single(warnings.Messages);
        Assert.Contains(_configDir, warning);
    }

    private sealed class WarningCollector : ILoggerProvider, ILogger
    {
        public List<string> Messages { get; } = new();

        public ILogger CreateLogger(string categoryName) => this;

        public void Dispose()
        {
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
                lock (Messages)
                    Messages.Add(formatter(state, exception));
        }
    }
}
