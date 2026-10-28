using System.Text.Json;
using ILD.Core.Services.Implementations.Adapters;
using ILD.Core.Services.Interfaces;
using ILD.Data.DTOs;
using ILD.Data.Entities;

namespace ILD.Tests;

/// <summary>
/// How every CLI adapter reports a run it cannot start: the same three checks across
/// Claude Code, Copilot, OpenCode and Pi, each with its adapter's own error prefix.
/// </summary>
public class CliAdapterLaunchFailureTests
{
    private static IAgentAdapter AdapterFor(string providerType) => providerType switch
    {
        "claude-code" => new ClaudeCodeAdapter(),
        "copilot" => new CopilotAdapter(),
        "opencode" => new OpenCodeAdapter(),
        "pi" => new PiAdapter(),
        _ => throw new ArgumentOutOfRangeException(nameof(providerType)),
    };

    private static string BaseUrlFor(string providerType) => providerType == "opencode" ? "http://localhost:1234/v1" : string.Empty;

    private static string ModelFor(string providerType) => providerType == "pi" ? "openai/gpt-5" : "test-model";

    private static AgentExecutionContext Context(string providerType, string binaryPath, string worktreePath, string? config = null)
        => new(
            Provider: new AiProvider
            {
                Name = $"{providerType}-test",
                Type = providerType,
                BaseUrl = BaseUrlFor(providerType),
                Model = ModelFor(providerType),
                Config = config ?? JsonSerializer.Serialize(new { binaryPath }),
            },
            Prompt: "test prompt",
            RunContext: new LoopRunContext(
                Guid.NewGuid(), Guid.NewGuid().ToString(), "Test Task", "Test description", worktreePath, "main", new List<string>(), null),
            ExecutionCount: 1,
            Cancel: CancellationToken.None);

    [Theory]
    [InlineData("claude-code", "claude-code-error")]
    [InlineData("copilot", "copilot-error")]
    [InlineData("opencode", "opencode-error")]
    [InlineData("pi", "pi-error")]
    public async Task A_binary_that_does_not_exist_fails_with_the_adapters_error_prefix(string providerType, string errorPrefix)
    {
        using var worktree = new TempDirectory();

        var result = await AdapterFor(providerType).ExecuteAsync(Context(providerType, "/nonexistent/cli", worktree.Path));

        Assert.False(result.Success);
        Assert.Contains(errorPrefix, result.Error);
    }

    [Theory]
    [InlineData("claude-code")]
    [InlineData("copilot")]
    public async Task A_run_without_a_worktree_fails_naming_the_valid_worktree_path(string providerType)
    {
        var result = await AdapterFor(providerType).ExecuteAsync(Context(providerType, "/bin/true", "/this/does/not/exist"));

        Assert.False(result.Success);
        Assert.Contains("valid worktree path", result.Error);
    }

    [Theory]
    [InlineData("claude-code")]
    [InlineData("copilot")]
    [InlineData("opencode")]
    public async Task The_binary_path_in_the_providers_config_is_what_runs_whatever_the_base_url_says(string providerType)
    {
        using var worktree = new TempDirectory();
        var ctx = Context(providerType, "/nonexistent/path", worktree.Path, config: "{\"binaryPath\":\"/bin/true\"}");

        var result = await AdapterFor(providerType).ExecuteAsync(ctx);

        Assert.True(result.Success, result.Error);
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("ild-launch-failure-").FullName;
        public void Dispose() => Directory.Delete(Path, true);
    }
}
