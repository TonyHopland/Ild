using ILD.Core.Services.Implementations;
using ILD.Core.Services.Implementations.Adapters;
using ILD.Core.Services.Interfaces;
using ILD.Data.DTOs;
using ILD.Data.Entities;

namespace ILD.Tests;

/// <summary>
/// The classification half of the throttle park: telling "the provider stopped
/// us" from "the work failed". Covers the shared text classifier directly, and
/// then pins the assumption it rests on — that every CLI adapter puts the
/// provider's notice in the result's <c>Output</c>, not its <c>Error</c> — by
/// running all four adapters against a stub binary that throttles.
/// </summary>
[Collection("EnvironmentPath")]
public class AiFailureClassifierTests
{
    /// <summary>
    /// The literal text a real run was throttled with (WI-50). It arrived as the
    /// agent's output while the error said only "exit=1 stderr=", which is why
    /// the classifier reads output first.
    /// </summary>
    private const string SessionLimitNotice = "You've hit your session limit · resets 9:40am (UTC)";

    [Theory]
    [InlineData(SessionLimitNotice)]
    [InlineData("Claude usage limit reached. Your limit will reset at 3pm.")]
    [InlineData("You have exceeded your weekly limit for this model")]
    [InlineData("API Error: 429 {\"type\":\"error\",\"error\":{\"type\":\"rate_limit_error\"}}")]
    [InlineData("Too Many Requests")]
    [InlineData("rate limit exceeded, please retry later")]
    [InlineData("{\"type\":\"overloaded_error\",\"message\":\"Overloaded\"}")]
    [InlineData("upstream returned HTTP 529 - service unavailable")]
    [InlineData("Provider error: status code 503")]
    [InlineData("read ECONNRESET")]
    [InlineData("stream disconnected before completion")]
    [InlineData("socket hang up")]
    public void Provider_interruptions_classify_as_interrupted(string text)
        => Assert.Equal(FailureKind.Interrupted, AiFailureClassifier.Classify(text));

    [Theory]
    // Auth / credentials, misconfiguration, validation, a crashed process and an
    // agent that reported failing work: all genuine failures, all left Unknown so
    // the executor routes them onto on_failure exactly as it did before.
    [InlineData("ProviderAuthError: invalid api key")]
    [InlineData("Invalid API key · Please run /login")]
    [InlineData("model \"gpt-5-turbo\" not found for provider anthropic")]
    [InlineData("prompt validation failed: messages must not be empty")]
    [InlineData("exit=127 stderr=opencode: command not found")]
    [InlineData("Tests failed: 3 of 41 assertions did not pass")]
    public void Genuine_failures_are_not_classified_as_interruptions(string text)
        => Assert.NotEqual(FailureKind.Interrupted, AiFailureClassifier.Classify(text));

    [Theory]
    // Context exhaustion speaks the language of a limit but must NOT park:
    // resuming the same session walks straight back into the same wall, so
    // parking would only relocate the dead end.
    [InlineData("prompt is too long: 210000 tokens > 200000 maximum")]
    [InlineData("This model's maximum context length is 200000 tokens")]
    [InlineData("context_length_exceeded")]
    [InlineData("Error: context window exceeded — compact the conversation")]
    public void Context_window_exhaustion_is_a_genuine_failure(string text)
        => Assert.Equal(FailureKind.Failed, AiFailureClassifier.Classify(text));

    [Fact]
    public void Output_is_classified_before_error()
    {
        // The real WI-50 shape: all of the signal is in the output, none of it in
        // the error. A classifier reading only Error would ship a dead feature.
        Assert.Equal(
            FailureKind.Interrupted,
            AiFailureClassifier.Classify(SessionLimitNotice, "exit=1 stderr="));
    }

    [Fact]
    public void Error_is_classified_when_the_output_says_nothing()
    {
        Assert.Equal(
            FailureKind.Interrupted,
            AiFailureClassifier.Classify("I'll start by reading the test file.", "opencode session error: 429 too many requests"));
    }

    [Fact]
    public void Nothing_recognised_stays_unknown()
        => Assert.Equal(FailureKind.Unknown, AiFailureClassifier.Classify("exit=1 stderr=", null));

    [Fact]
    public void No_text_at_all_stays_unknown()
        => Assert.Equal(FailureKind.Unknown, AiFailureClassifier.Classify(null, "", "   "));

    // ---- adapter parity: the shape the classifier is fed in production ----

    public static TheoryData<string, IAgentAdapter> ThrottlingAdapters() => new()
    {
        { "opencode", new OpenCodeAdapter() },
        { "claude-code", new ClaudeCodeAdapter() },
        { "pi", new PiAdapter() },
        { "copilot", new CopilotAdapter() },
    };

    [Theory]
    [MemberData(nameof(ThrottlingAdapters))]
    public async Task Every_adapter_reports_a_throttle_as_output_the_classifier_recognises(
        string providerType, IAgentAdapter adapter)
    {
        // A CLI that prints the provider's notice and exits non-zero — the shape
        // all four adapters share: Fail($"exit={code} stderr={stderr}", response),
        // where the response (arg 2) is the Output.
        var worktreeDir = Path.Combine(Path.GetTempPath(), $"ild-throttle-{providerType}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(worktreeDir);
        var scriptPath = Path.Combine(worktreeDir, "throttle.sh");
        await File.WriteAllTextAsync(scriptPath, $"#!/bin/sh\necho \"{SessionLimitNotice}\"\nexit 1\n");
        File.SetUnixFileMode(scriptPath,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
            | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

        try
        {
            var result = await adapter.ExecuteAsync(new AgentExecutionContext(
                Provider: new AiProvider
                {
                    Name = $"{providerType}-test",
                    Type = providerType,
                    BaseUrl = string.Empty,
                    Model = string.Empty,
                    Config = $"{{\"binaryPath\":\"{scriptPath}\"}}",
                },
                Prompt: "do the work",
                RunContext: new LoopRunContext(
                    Guid.NewGuid(), Guid.NewGuid().ToString(), "Test Task", "Test description",
                    worktreeDir, "main", new List<string>(), null),
                ExecutionCount: 1,
                Cancel: CancellationToken.None));

            Assert.False(result.Success);
            // The notice rides on Output; Error carries only the exit status.
            Assert.Contains("session limit", result.Output ?? string.Empty);
            Assert.DoesNotContain("session limit", result.Error ?? string.Empty);
            Assert.Equal(
                FailureKind.Interrupted,
                AiFailureClassifier.Classify(result.Output, result.Error));
        }
        finally
        {
            Directory.Delete(worktreeDir, true);
        }
    }
}
