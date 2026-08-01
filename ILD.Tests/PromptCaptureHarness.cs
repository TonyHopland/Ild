using System.Diagnostics;
using System.Text.Json;
using ILD.Core.Services.Interfaces;
using ILD.Data.DTOs;

namespace ILD.Tests;

/// <summary>How a CLI receives its prompt, and therefore where to record it.</summary>
internal enum PromptCaptureMode
{
    /// <summary>Last on the command line (claude-code, opencode, copilot).</summary>
    LastArgument,

    /// <summary>Written to the process's standard input (pi).</summary>
    StandardInput,
}

/// <summary>
/// A stand-in coding-agent CLI that records the prompt it is launched with and
/// then replays a minimal successful turn in its CLI's own output shape.
///
/// The prompt the agent process actually receives is the only place the whole
/// prompt pipeline is observable from outside, so the templating tests assert
/// against that rather than against any internal render hook — the assertions
/// stay meaningful no matter which layer the rendering lives in. Asserting on
/// an adapter's own <c>ResolvedPrompt</c> instead would not: an adapter sets it
/// from the same field it launches with, so a re-render at the launch site
/// would sail past.
/// </summary>
internal sealed class PromptCapturingCli : IDisposable
{
    /// <summary>Claude Code's `--output-format stream-json` turn.</summary>
    public const string ClaudeCodeTurn =
        "echo '{\"type\":\"system\",\"subtype\":\"init\",\"session_id\":\"sess-cap\"}'\n"
        + "echo '{\"type\":\"assistant\",\"session_id\":\"sess-cap\",\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"ok\"}]}}'\n"
        + "echo '{\"type\":\"result\",\"session_id\":\"sess-cap\",\"is_error\":false,\"result\":\"ok\"}'\n";

    /// <summary>Pi's JSONL session + assistant message.</summary>
    public const string PiTurn =
        "echo '{\"type\":\"session\",\"version\":3,\"id\":\"pi-1\",\"cwd\":\"/tmp\"}'\n"
        + "echo '{\"type\":\"message_end\",\"message\":{\"role\":\"assistant\",\"content\":[{\"text\":\"ok\"}]}}'\n";

    /// <summary>Exit 0 saying nothing — enough for opencode and copilot to succeed.</summary>
    public const string SilentSuccess = "";

    private readonly string _capturePath;
    private readonly string _countPath;
    private readonly PromptCaptureMode _mode;

    /// <summary>A real directory usable as the run's worktree.</summary>
    public string WorkDir { get; }

    public string BinaryPath { get; }

    public PromptCapturingCli(PromptCaptureMode capture = PromptCaptureMode.LastArgument, string turn = ClaudeCodeTurn)
    {
        _mode = capture;
        WorkDir = Path.Combine(Path.GetTempPath(), $"ild-prompt-capture-{Guid.NewGuid():N}");
        Directory.CreateDirectory(WorkDir);
        _capturePath = Path.Combine(WorkDir, "captured-prompt.txt");
        _countPath = Path.Combine(WorkDir, "invocation-count.txt");
        BinaryPath = Path.Combine(WorkDir, "fake-agent.sh");

        var record = capture == PromptCaptureMode.LastArgument
            ? "for a in \"$@\"; do last=\"$a\"; done\n"
                + $"printf '%s' \"$last\" > '{_capturePath}'\n"
            // The adapter closes stdin after writing the prompt, so this reads
            // the whole prompt and terminates.
            : $"cat > '{_capturePath}'\n";

        // A multi-turn chat launches this CLI once per turn, and the whole point
        // of such a test is what the SECOND launch was handed — which a
        // last-write-wins capture file has already overwritten. So each launch
        // also lands in its own numbered file: the full argv, NUL-separated
        // because a prompt argument legitimately contains newlines.
        var recordPerInvocation =
            $"n=$(cat '{_countPath}' 2>/dev/null || echo 0)\n"
            + "n=$((n+1))\n"
            + $"printf '%s' \"$n\" > '{_countPath}'\n"
            + $"printf '%s\\0' \"$@\" > '{WorkDir}/argv-'\"$n\"'.bin'\n"
            + (capture == PromptCaptureMode.StandardInput
                ? $"cp '{_capturePath}' '{WorkDir}/stdin-'\"$n\"'.txt'\n"
                : string.Empty);

        File.WriteAllText(BinaryPath, "#!/bin/sh\n" + record + recordPerInvocation + turn);
        Process.Start("chmod", "+x " + BinaryPath)!.WaitForExit();
    }

    /// <summary>The prompt the CLI process was actually handed.</summary>
    public string CapturedPrompt
        => File.Exists(_capturePath)
            ? File.ReadAllText(_capturePath)
            : throw new InvalidOperationException(
                "the fake agent CLI was never invoked — the adapter failed before launching it");

    /// <summary>How many times the adapter launched the CLI.</summary>
    public int InvocationCount
        => File.Exists(_countPath) ? int.Parse(File.ReadAllText(_countPath).Trim()) : 0;

    /// <summary>
    /// The arguments of the <paramref name="turn"/>'th launch, 1-based. This is the
    /// whole of what the CLI was told: the prompt, the <c>--resume</c> flag, every
    /// <c>--add-dir</c> grant. argv[0] is not among them and never was — the capture
    /// writes <c>"$@"</c>, which is the positional parameters only.
    /// </summary>
    public string[] ArgvFor(int turn)
    {
        var path = Path.Combine(WorkDir, $"argv-{turn}.bin");
        if (!File.Exists(path))
            throw new InvalidOperationException(
                $"the fake agent CLI was launched {InvocationCount} time(s); there is no turn {turn}");

        // `printf '%s\0'` emits a trailing NUL, so the split leaves one empty tail
        // element to drop — and only that one. Dropping a leading element too would
        // silently eat the CLI's first real flag, which is exactly what an argv
        // assertion is here to see.
        return File.ReadAllText(path).Split('\0')[..^1];
    }

    /// <summary>The positional prompt argument of the <paramref name="turn"/>'th launch (1-based).</summary>
    public string PromptFor(int turn)
        => _mode == PromptCaptureMode.StandardInput
            ? File.ReadAllText(Path.Combine(WorkDir, $"stdin-{turn}.txt"))
            : ArgvFor(turn)[^1];

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
