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

    /// <summary>The error every adapter writes for a non-zero exit with empty stderr.</summary>
    private const string BareExit = "exit=1 stderr=";

    [Theory]
    // A provider cutting a turn off in-band writes the notice into the agent's
    // output, so these must be recognised there.
    [InlineData(SessionLimitNotice)]
    [InlineData("Claude usage limit reached. Your limit will reset at 3pm.")]
    [InlineData("You have exceeded your weekly limit for this model")]
    [InlineData("API Error: 429 {\"type\":\"error\",\"error\":{\"type\":\"rate_limit_error\"}}")]
    [InlineData("API Error: 500 {\"type\":\"error\",\"error\":{\"type\":\"overloaded_error\",\"message\":\"Overloaded\"}}")]
    public void Provider_notices_in_the_output_classify_as_interrupted(string output)
        => Assert.Equal(FailureKind.Interrupted, AiFailureClassifier.Classify(output, BareExit));

    [Theory]
    // Transport and capacity failures reach the adapter as stderr or as a
    // structured provider message — machine text, never the agent's own prose.
    [InlineData("429 Too Many Requests")]
    [InlineData("rate limit exceeded, please retry later")]
    [InlineData("upstream returned HTTP 529 - service unavailable")]
    [InlineData("Provider error: status code 503")]
    [InlineData("read ECONNRESET")]
    [InlineData("stream disconnected before completion")]
    [InlineData("socket hang up")]
    public void Provider_interruptions_in_the_error_classify_as_interrupted(string error)
        => Assert.Equal(FailureKind.Interrupted, AiFailureClassifier.Classify(null, error));

    [Theory]
    // The precision half, and the reason the output is not matched against the
    // ambiguous vocabulary: on the non-zero-exit path the output IS the agent's
    // narration, and a coding agent narrates about status codes, dropped
    // connections and file:line citations constantly. Parking any of these would
    // break the loop's on_failure handling for a node that genuinely failed.
    [InlineData("Fixed the null deref at LoopEngine.cs:429 and re-ran; 3 tests still fail.")]
    [InlineData("The endpoint returns 500 Internal Server Error for a malformed body; I added a guard but the assertion still fails.")]
    [InlineData("Reproduced the connection reset by killing the upstream mid-request. Could not get the retry path to pass.")]
    [InlineData("Added a handler for HTTP 503 responses, then ran the suite: 2 failures remain in RetryPolicyTests.")]
    [InlineData("Wrapped the fetch so a fetch failed error surfaces as a typed result. The build is still red.")]
    [InlineData("Renamed ECONNRESET handling to reconnect(); tests fail on the new name.")]
    public void Agent_narration_is_not_mistaken_for_an_interruption(string output)
        => Assert.NotEqual(FailureKind.Interrupted, AiFailureClassifier.Classify(output, BareExit));

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
    {
        // Neither slot may park them — the error text gets the full rule set, so
        // it is the stricter of the two places to assert this.
        Assert.NotEqual(FailureKind.Interrupted, AiFailureClassifier.Classify(text, BareExit));
        Assert.NotEqual(FailureKind.Interrupted, AiFailureClassifier.Classify(null, text));
    }

    [Theory]
    // Context exhaustion speaks the language of a limit but must NOT park:
    // resuming the same session walks straight back into the same wall, so
    // parking would only relocate the dead end.
    [InlineData("prompt is too long: 210000 tokens > 200000 maximum")]
    [InlineData("This model's maximum context length is 200000 tokens")]
    [InlineData("context_length_exceeded")]
    [InlineData("Error: context window exceeded — compact the conversation")]
    public void Context_window_exhaustion_is_a_genuine_failure(string text)
    {
        Assert.Equal(FailureKind.Failed, AiFailureClassifier.Classify(text, BareExit));
        Assert.Equal(FailureKind.Failed, AiFailureClassifier.Classify(null, text));
    }

    [Fact]
    public void Output_is_classified_before_error()
    {
        // The real WI-50 shape: all of the signal is in the output, none of it in
        // the error. A classifier reading only Error would ship a dead feature.
        Assert.Equal(
            FailureKind.Interrupted,
            AiFailureClassifier.Classify(SessionLimitNotice, BareExit));
    }

    [Fact]
    public void Error_is_classified_when_the_output_says_nothing()
    {
        Assert.Equal(
            FailureKind.Interrupted,
            AiFailureClassifier.Classify("I'll start by reading the test file.", "opencode session error: 429 too many requests"));
    }

    [Fact]
    public void A_context_window_failure_in_the_output_beats_an_interruption_in_the_error()
    {
        // Order matters both ways: the genuine-failure rules run first within
        // each text, and the output is read before the error.
        Assert.Equal(
            FailureKind.Failed,
            AiFailureClassifier.Classify("prompt is too long: 210000 tokens > 200000 maximum", "exit=1 stderr=socket hang up"));
    }

    [Fact]
    public void A_structured_provider_message_gets_the_full_rule_set()
    {
        // What an adapter lifts out of its own error event is the provider's
        // text, not the agent's, so the ambiguous vocabulary is trusted there.
        Assert.Equal(
            FailureKind.Interrupted,
            AiFailureClassifier.ClassifyProviderMessage("upstream connect error or disconnect/reset before headers"));
    }

    [Fact]
    public void Nothing_recognised_stays_unknown()
        => Assert.Equal(FailureKind.Unknown, AiFailureClassifier.Classify(BareExit, null));

    [Fact]
    public void No_text_at_all_stays_unknown()
        => Assert.Equal(FailureKind.Unknown, AiFailureClassifier.Classify(null, "   "));

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
