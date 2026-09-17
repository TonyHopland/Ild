using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using ILD.Api.Configuration;
using ILD.Core.Services.Implementations;
using ILD.Core.Services.Implementations.Adapters;
using ILD.Core.Services.Interfaces;
using ILD.Data.DTOs;
using ILD.Data.Entities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ILD.Tests;

/// <summary>
/// When the MCP config handed to the Copilot or Claude Code CLI cannot be
/// written, the run carries on without its MCP servers, and the application log
/// says so, naming where it was writing and why that failed. A run with nothing
/// to write stays silent. The agent read root is pointed at a temporary one
/// whose <c>ild-mcp-config</c> directory this user cannot write into.
/// </summary>
[Collection("EnvironmentPath")]
public sealed class McpConfigWriteFailureTests : IDisposable
{
    private const string Token = "write-failure-test-token";
    private static readonly string[] IldOff = ["read"];

    private readonly string _root = Directory.CreateTempSubdirectory("ild-mcp-write-failure-root-").FullName;
    private readonly string _workDir = Directory.CreateTempSubdirectory("ild-mcp-write-failure-work-").FullName;
    private readonly string _configDir;
    private readonly string? _previousReadRoot;
    private readonly string? _previousDllOverride;
    private readonly string? _previousApiToken;

    public McpConfigWriteFailureTests()
    {
        _configDir = Path.Combine(_root, "ild-mcp-config");
        var fakeDll = Path.Combine(_workDir, "ild-mcp-server.dll");
        File.WriteAllText(fakeDll, "");

        _previousReadRoot = Environment.GetEnvironmentVariable(AgentIsolation.AgentReadRootEnvVar);
        _previousDllOverride = Environment.GetEnvironmentVariable("ILD_MCP_SERVER_DLL");
        _previousApiToken = Environment.GetEnvironmentVariable("ILD_API_TOKEN");
        Environment.SetEnvironmentVariable(AgentIsolation.AgentReadRootEnvVar, _root);
        Environment.SetEnvironmentVariable("ILD_MCP_SERVER_DLL", fakeDll);
        Environment.SetEnvironmentVariable("ILD_API_TOKEN", Token);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(AgentIsolation.AgentReadRootEnvVar, _previousReadRoot);
        Environment.SetEnvironmentVariable("ILD_MCP_SERVER_DLL", _previousDllOverride);
        Environment.SetEnvironmentVariable("ILD_API_TOKEN", _previousApiToken);
        if (OperatingSystem.IsLinux() && Directory.Exists(_configDir))
        {
            try { File.SetUnixFileMode(_configDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); } catch { /* best effort */ }
        }
        foreach (var dir in new[] { _root, _workDir })
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    // root can write into a read-only directory, so the failure cannot be staged there.
    private static bool CanStageWriteFailure => OperatingSystem.IsLinux() && Environment.UserName != "root";

    [Theory]
    [InlineData("copilot")]
    [InlineData("claude-code")]
    public void A_failed_write_of_custom_servers_is_logged_once_with_the_path_and_the_reason(string type)
    {
        if (!CanStageWriteFailure) return;
        StageUnwritableConfigDirectory();
        var logger = new RecordingLogger();

        var path = WriteConfig(type, Provider(type, withCustomServer: true), IldOff, logger);

        Assert.Null(path);
        AssertSingleWriteFailureWarning(logger.Warnings);
    }

    [Theory]
    [InlineData("copilot")]
    [InlineData("claude-code")]
    public void A_failed_write_of_the_ild_entry_alone_is_logged_once_with_the_path_and_the_reason(string type)
    {
        if (!CanStageWriteFailure) return;
        StageUnwritableConfigDirectory();
        var logger = new RecordingLogger();

        var path = WriteConfig(type, Provider(type, withCustomServer: false), allowlist: null, logger);

        Assert.Null(path);
        AssertSingleWriteFailureWarning(logger.Warnings);
    }

    [Theory]
    [InlineData("copilot")]
    [InlineData("claude-code")]
    public void A_run_with_ild_off_and_no_custom_servers_writes_nothing_and_logs_nothing(string type)
    {
        if (!CanStageWriteFailure) return;
        StageUnwritableConfigDirectory();
        var logger = new RecordingLogger();

        var path = WriteConfig(type, Provider(type, withCustomServer: false), IldOff, logger);

        Assert.Null(path);
        Assert.Empty(logger.Warnings);
    }

    [Theory]
    [InlineData("copilot")]
    [InlineData("claude-code")]
    public void A_successful_write_logs_nothing(string type)
    {
        var logger = new RecordingLogger();

        var path = WriteConfig(type, Provider(type, withCustomServer: true), allowlist: null, logger);

        Assert.NotNull(path);
        try
        {
            Assert.Equal(_configDir, Path.GetDirectoryName(path));
            using var doc = JsonDocument.Parse(File.ReadAllText(path!));
            var servers = doc.RootElement.GetProperty("mcpServers");
            Assert.True(servers.TryGetProperty("ild", out _));
            Assert.True(servers.TryGetProperty("docs", out _));
            Assert.Empty(logger.Warnings);
        }
        finally
        {
            File.Delete(path!);
        }
    }

    [Theory]
    [InlineData("copilot")]
    [InlineData("claude-code")]
    public void An_untrusted_config_directory_still_throws_and_is_not_turned_into_a_warning(string type)
    {
        if (!OperatingSystem.IsLinux()) return;
        StageGroupWritableConfigDirectory();
        var logger = new RecordingLogger();

        Assert.Throws<InvalidOperationException>(
            () => WriteConfig(type, Provider(type, withCustomServer: true), IldOff, logger));
        Assert.Empty(logger.Warnings);
    }

    [Fact]
    public async Task Copilot_runs_without_its_mcp_config_when_the_write_fails_and_logs_why()
    {
        if (!CanStageWriteFailure) return;
        StageUnwritableConfigDirectory();
        var logger = new RecordingLogger<CopilotAdapter>();
        var worktree = CreateWorktree();

        var result = await new CopilotAdapter(logger).ExecuteAsync(Context("copilot", worktree, WriteRecordingCli(worktree)));

        Assert.True(result.Success, result.Error);
        var argv = File.ReadAllLines(Path.Combine(worktree, "argv.txt"));
        Assert.DoesNotContain("--additional-mcp-config", argv);
        Assert.DoesNotContain(argv, a => a.StartsWith('@'));
        AssertSingleWriteFailureWarning(logger.Warnings);
    }

    [Fact]
    public async Task Claude_Code_runs_without_its_mcp_config_when_the_write_fails_and_logs_why()
    {
        if (!CanStageWriteFailure) return;
        StageUnwritableConfigDirectory();
        var logger = new RecordingLogger<ClaudeCodeAdapter>();
        var worktree = CreateWorktree();

        var result = await new ClaudeCodeAdapter(logger).ExecuteAsync(Context("claude-code", worktree, WriteRecordingCli(worktree)));

        Assert.True(result.Success, result.Error);
        var argv = File.ReadAllLines(Path.Combine(worktree, "argv.txt"));
        Assert.DoesNotContain("--mcp-config", argv);
        AssertSingleWriteFailureWarning(logger.Warnings);
    }

    [Fact]
    public async Task Copilot_with_nothing_to_write_launches_without_a_flag_and_logs_nothing()
    {
        if (!CanStageWriteFailure) return;
        StageUnwritableConfigDirectory();
        var logger = new RecordingLogger<CopilotAdapter>();
        var worktree = CreateWorktree();

        var result = await new CopilotAdapter(logger).ExecuteAsync(
            Context("copilot", worktree, WriteRecordingCli(worktree), withCustomServer: false) with { ToolAllowlist = IldOff });

        Assert.True(result.Success, result.Error);
        Assert.DoesNotContain("--additional-mcp-config", File.ReadAllLines(Path.Combine(worktree, "argv.txt")));
        Assert.Empty(logger.Warnings);
    }

    [Fact]
    public async Task Claude_Code_with_nothing_to_write_launches_without_a_flag_and_logs_nothing()
    {
        if (!CanStageWriteFailure) return;
        StageUnwritableConfigDirectory();
        var logger = new RecordingLogger<ClaudeCodeAdapter>();
        var worktree = CreateWorktree();

        var result = await new ClaudeCodeAdapter(logger).ExecuteAsync(
            Context("claude-code", worktree, WriteRecordingCli(worktree), withCustomServer: false) with { ToolAllowlist = IldOff });

        Assert.True(result.Success, result.Error);
        Assert.DoesNotContain("--mcp-config", File.ReadAllLines(Path.Combine(worktree, "argv.txt")));
        Assert.Empty(logger.Warnings);
    }

    [Theory]
    [InlineData("copilot")]
    [InlineData("claude-code")]
    public async Task An_untrusted_config_directory_still_fails_the_run_without_launching_the_cli(string type)
    {
        if (!OperatingSystem.IsLinux()) return;
        StageGroupWritableConfigDirectory();
        var worktree = CreateWorktree();
        IAgentAdapter adapter = type == "copilot"
            ? new CopilotAdapter(new RecordingLogger<CopilotAdapter>())
            : new ClaudeCodeAdapter(new RecordingLogger<ClaudeCodeAdapter>());

        var result = await adapter.ExecuteAsync(Context(type, worktree, WriteRecordingCli(worktree)));

        Assert.False(result.Success);
        Assert.Contains(_configDir, result.Error);
        Assert.False(File.Exists(Path.Combine(worktree, "argv.txt")), "the CLI was launched");
    }

    [Fact]
    public async Task The_registered_adapters_log_a_failed_write_to_the_application_log()
    {
        if (!CanStageWriteFailure) return;
        StageUnwritableConfigDirectory();
        var provider = new RecordingLoggerProvider();
        IServiceCollection services = new ServiceCollection();
        services.AddLogging(logging => logging.AddProvider(provider));
        foreach (var descriptor in new ServiceCollection().AddIldServices().Where(d =>
                     d.ServiceType == typeof(IAgentAdapter)
                     && (d.ImplementationType == typeof(CopilotAdapter) || d.ImplementationType == typeof(ClaudeCodeAdapter))))
            services.Add(descriptor);
        using var serviceProvider = services.BuildServiceProvider();

        var adapters = serviceProvider.GetServices<IAgentAdapter>().ToList();
        Assert.Equal(2, adapters.Count);
        foreach (var adapter in adapters)
        {
            var type = adapter is CopilotAdapter ? "copilot" : "claude-code";
            var worktree = CreateWorktree();
            provider.Warnings.Clear();

            var result = await adapter.ExecuteAsync(Context(type, worktree, WriteRecordingCli(worktree)));

            Assert.True(result.Success, result.Error);
            AssertSingleWriteFailureWarning(provider.Warnings.ToList());
        }
    }

    private void AssertSingleWriteFailureWarning(IReadOnlyCollection<string> warnings)
    {
        var warning = Assert.Single(warnings);
        Assert.Contains(_configDir, warning);
        // The UnauthorizedAccessException .NET raises for the denied temp-file create.
        Assert.Matches($"Access to the path '{Regex.Escape(_configDir)}/[^']+' is denied\\.", warning);
        Assert.DoesNotContain(Token, warning);
    }

    private static string? WriteConfig(string type, AiProvider provider, IReadOnlyList<string>? allowlist, ILogger logger)
    {
        var runContext = new LoopRunContext(Guid.NewGuid(), "wi", "t", "d", "/tmp", "main", new List<string>(), null);
        return type == "copilot"
            ? CopilotAdapter.TryWriteMcpConfig(provider, runContext, allowlist, logger: logger)
            : ClaudeCodeAdapter.TryWriteIldMcpConfig(provider, runContext, allowlist, logger: logger);
    }

    private void StageUnwritableConfigDirectory()
    {
        Directory.CreateDirectory(_configDir);
        File.SetUnixFileMode(_configDir, UnixFileMode.UserRead | UnixFileMode.UserExecute);
    }

    private void StageGroupWritableConfigDirectory()
    {
        Directory.CreateDirectory(_configDir);
        File.SetUnixFileMode(_configDir,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            | UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute);
    }

    private static AiProvider Provider(string type, bool withCustomServer, string? binaryPath = null)
    {
        var config = new Dictionary<string, object?>();
        if (binaryPath is not null) config["binaryPath"] = binaryPath;
        if (withCustomServer) config["customMcpServersJson"] = """{ "docs": { "command": "npx" } }""";
        return new AiProvider
        {
            Name = $"{type}-test",
            Type = type,
            BaseUrl = string.Empty,
            Model = string.Empty,
            Config = config.Count == 0 ? null : JsonSerializer.Serialize(config),
        };
    }

    private static AgentExecutionContext Context(string type, string worktree, string binaryPath, bool withCustomServer = true)
        => new(
            Provider: Provider(type, withCustomServer, binaryPath),
            Prompt: "fix it",
            RunContext: new LoopRunContext(Guid.NewGuid(), "wi", "t", "d", worktree, "main", new List<string>(), null),
            ExecutionCount: 1,
            Cancel: CancellationToken.None);

    private string CreateWorktree()
        => Directory.CreateDirectory(Path.Combine(_workDir, "wt-" + Guid.NewGuid().ToString("N"))).FullName;

    /// <summary>A stand-in CLI that records its argv, one per line, and exits 0.</summary>
    private static string WriteRecordingCli(string worktree)
    {
        var script = Path.Combine(worktree, "fake-cli.sh");
        File.WriteAllText(script,
            "#!/bin/sh\n" +
            $"printf '%s\\n' \"$@\" > '{worktree}/argv.txt'\n" +
            "echo 'done'\n");
        var psi = new ProcessStartInfo("chmod") { UseShellExecute = false };
        psi.ArgumentList.Add("+x");
        psi.ArgumentList.Add(script);
        using var chmod = Process.Start(psi)!;
        chmod.WaitForExit();
        return script;
    }

    private class RecordingLogger : ILogger
    {
        public List<string> Warnings { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
                Warnings.Add(formatter(state, exception));
        }
    }

    private sealed class RecordingLogger<T> : RecordingLogger, ILogger<T>
    {
    }

    private sealed class RecordingLoggerProvider : ILoggerProvider
    {
        public ConcurrentQueue<string> Warnings { get; } = new();

        public ILogger CreateLogger(string categoryName) => new Forwarding(this);

        public void Dispose()
        {
        }

        private sealed class Forwarding(RecordingLoggerProvider owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (logLevel == LogLevel.Warning)
                    owner.Warnings.Enqueue(formatter(state, exception));
            }
        }
    }
}
