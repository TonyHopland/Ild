using System.Diagnostics;
using System.Text.Json;
using ILD.Core.Services.Interfaces;
using ILD.Data.DTOs;

namespace ILD.Tests;

/// <summary>
/// A stand-in coding-agent CLI that records the prompt argv it is launched with
/// and then replays a minimal successful claude-code turn.
///
/// The prompt the agent process actually receives is the only place the whole
/// prompt pipeline is observable from outside, so the templating tests assert
/// against that rather than against any internal render hook — the assertions
/// stay meaningful no matter which layer the rendering lives in.
/// </summary>
internal sealed class PromptCapturingCli : IDisposable
{
    private readonly string _capturePath;

    /// <summary>A real directory usable as the run's worktree.</summary>
    public string WorkDir { get; }

    public string BinaryPath { get; }

    public PromptCapturingCli()
    {
        WorkDir = Path.Combine(Path.GetTempPath(), $"ild-prompt-capture-{Guid.NewGuid():N}");
        Directory.CreateDirectory(WorkDir);
        _capturePath = Path.Combine(WorkDir, "captured-prompt.txt");
        BinaryPath = Path.Combine(WorkDir, "fake-agent.sh");

        File.WriteAllText(BinaryPath,
            "#!/bin/sh\n"
            // Every CLI adapter puts the prompt last on the command line.
            + "for a in \"$@\"; do last=\"$a\"; done\n"
            + $"printf '%s' \"$last\" > '{_capturePath}'\n"
            + "echo '{\"type\":\"system\",\"subtype\":\"init\",\"session_id\":\"sess-cap\"}'\n"
            + "echo '{\"type\":\"assistant\",\"session_id\":\"sess-cap\",\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"ok\"}]}}'\n"
            + "echo '{\"type\":\"result\",\"session_id\":\"sess-cap\",\"is_error\":false,\"result\":\"ok\"}'\n");
        Process.Start("chmod", "+x " + BinaryPath)!.WaitForExit();
    }

    /// <summary>The prompt the CLI process was actually handed.</summary>
    public string CapturedPrompt
        => File.Exists(_capturePath)
            ? File.ReadAllText(_capturePath)
            : throw new InvalidOperationException(
                "the fake agent CLI was never invoked — the adapter failed before launching it");

    /// <summary>An AiProvider.Config JSON pinning the adapter to this fake CLI.</summary>
    public string ProviderConfigJson
        => JsonSerializer.Serialize(new Dictionary<string, string> { ["binaryPath"] = BinaryPath });

    public void Dispose()
    {
        try { Directory.Delete(WorkDir, true); } catch { /* best effort */ }
    }
}

/// <summary>
/// Wraps a real adapter so a test can see the prompt the caller handed it while
/// still exercising the adapter end to end. What the wrapper recorded and what
/// the CLI received must be the same string.
/// </summary>
internal sealed class RecordingAdapter(IAgentAdapter inner) : IAgentAdapter
{
    public AgentExecutionContext? LastContext { get; private set; }

    public string Name => inner.Name;
    public string[] SupportedProviderTypes => inner.SupportedProviderTypes;
    public ConfigFieldDescriptor[] ConfigSchema => inner.ConfigSchema;

    public Task<NodeExecutionResult> ExecuteAsync(AgentExecutionContext context)
    {
        LastContext = context;
        return inner.ExecuteAsync(context);
    }
}
