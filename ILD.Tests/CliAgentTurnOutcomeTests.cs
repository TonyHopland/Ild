using ILD.Core.Services.Implementations.Adapters;
using ILD.Core.Services.Implementations.ModelGateway;
using ILD.Core.Services.Interfaces;
using ILD.Core.Services.RunHosts;
using ILD.Data.DTOs;
using ILD.Data.Entities;
using ILD.Tests.Fakes;
using Microsoft.Extensions.DependencyInjection;

namespace ILD.Tests;

/// <summary>
/// What a finished agent turn is classified as, the same for every CLI: driven through a scripted turn that says what its stream
/// parsed to, over the exit codes and stderr a fake host reports.
/// </summary>
public class CliAgentTurnOutcomeTests
{
    private const string Worktree = "/fake-worktrees/run";

    private static StreamResult Events(string text, string? error = null, string? sessionId = null, string? truncation = null, string raw = "{}")
        => new(text, sessionId, error, SawJsonEvents: true, RawStdout: raw, Truncation: truncation);

    private static async Task<(NodeExecutionResult Result, ScriptedAdapter Adapter)> RunAsync(
        StreamResult stream, int exitCode = 0, string stderr = "", string? incomingSessionId = null, string? sessionId = null,
        Action<ScriptedAdapter>? configure = null)
    {
        var host = new FakeRunHost().WithDirectory(Worktree);
        host.Script = _ => FakeCommandScript.Prints("ignored", stderr, exitCode);
        var gateway = new CountingGateway();
        var services = new ServiceCollection();
        services.AddSingleton<IRunHost>(host);
        services.AddSingleton<IModelGatewayRegistry>(gateway);
        var adapter = new ScriptedAdapter(services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(), gateway)
        {
            ReadAs = _ => stream,
        };
        configure?.Invoke(adapter);
        var result = await adapter.ExecuteAsync(new AgentExecutionContext(
            Provider: new AiProvider { Name = "p", Type = "scripted", BaseUrl = string.Empty, Model = string.Empty },
            Prompt: "the prompt",
            RunContext: new LoopRunContext(Guid.NewGuid(), "wi", "t", "d", Worktree, "main", new List<string>(), null),
            ExecutionCount: 1,
            Cancel: TestContext.Current.CancellationToken,
            SessionId: sessionId,
            IncomingSessionId: incomingSessionId));
        return (result, adapter);
    }

    [Fact]
    public async Task A_clean_exit_with_text_succeeds_with_the_prompt_the_session_and_what_the_turn_used()
    {
        var usage = new TokenUsage(10, 5, null);

        var (result, adapter) = await RunAsync(
            Events("Hello, world.", sessionId: "sess-stream"), incomingSessionId: "sess-in", sessionId: "sess-asked",
            configure: a => a.Usage = usage);

        Assert.True(result.Success, result.Error);
        Assert.Equal("Hello, world.", result.Output);
        Assert.Equal("the prompt", result.ResolvedPrompt);
        Assert.Equal("sess-stream", result.SessionId);
        Assert.Equal("sess-in", result.IncomingSessionId);
        Assert.Equal(usage, result.Usage);
        Assert.Contains("usage", adapter.Journal);
    }

    [Fact]
    public async Task A_turn_that_named_no_session_ends_on_the_one_it_resumed()
    {
        var (result, _) = await RunAsync(Events("hi"), sessionId: "sess-asked");

        Assert.Equal("sess-asked", result.SessionId);
    }

    [Fact]
    public async Task A_cli_without_session_resume_hands_on_no_incoming_session()
    {
        var (result, _) = await RunAsync(Events("hi"), incomingSessionId: "sess-in", configure: a => a.SupportsSessionResume = false);

        Assert.True(result.Success, result.Error);
        Assert.Null(result.IncomingSessionId);
        Assert.Null(result.SessionId);
    }

    [Fact]
    public async Task Output_that_was_not_an_event_stream_is_the_answer_without_its_trailing_newline()
    {
        var (result, _) = await RunAsync(new StreamResult("", null, null, SawJsonEvents: false, RawStdout: "plain output\n"));

        Assert.True(result.Success, result.Error);
        Assert.Equal("plain output", result.Output);
    }

    [Fact]
    public async Task Blank_output_that_was_no_event_stream_is_passed_on_as_the_answer()
    {
        var (result, _) = await RunAsync(new StreamResult("", null, null, SawJsonEvents: false, RawStdout: "  \n"));

        Assert.True(result.Success, result.Error);
        Assert.Equal("", result.Output);
    }

    [Fact]
    public async Task Blank_output_that_was_no_event_stream_fails_with_a_diagnostic_for_a_cli_that_fails_on_it()
    {
        var (result, _) = await RunAsync(
            new StreamResult("", null, null, SawJsonEvents: false, RawStdout: "  \n"), stderr: "it died", configure: a => a.BlankOutputFails = true);

        Assert.False(result.Success);
        Assert.Equal("[scripted] no assistant text response. stderr: it died", result.Error);
    }

    [Fact]
    public async Task A_clean_exit_with_events_but_no_text_fails_and_does_not_return_the_raw_stream()
    {
        var (result, adapter) = await RunAsync(Events("", raw: "{\"type\":\"system\",\"session_id\":\"sess-abc\"}"));

        Assert.False(result.Success);
        Assert.Equal("[scripted] no assistant text response from model", result.Error);
        Assert.DoesNotContain("session_id", result.Error);
        Assert.DoesNotContain("usage", adapter.Journal);
    }

    [Fact]
    public async Task A_clean_exit_with_no_text_names_what_stderr_said()
    {
        var (result, _) = await RunAsync(Events("   "), stderr: "provider dropped it\n");

        Assert.False(result.Success);
        Assert.Equal("[scripted] no assistant text response. stderr: provider dropped it", result.Error);
    }

    [Fact]
    public async Task A_clean_exit_with_a_provider_error_fails_with_it_even_beside_text()
    {
        var (result, _) = await RunAsync(Events("some text", error: "upstream broke"));

        Assert.False(result.Success);
        Assert.Equal("scripted session error: upstream broke", result.Error);
        Assert.Equal("some text", result.Output);
    }

    [Fact]
    public async Task A_provider_error_with_no_text_is_the_diagnostic_output()
    {
        var (result, _) = await RunAsync(Events("", error: "upstream broke"));

        Assert.Equal("[scripted-error] upstream broke", result.Output);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task A_usage_limit_in_the_providers_error_is_an_interruption_at_any_exit_code(int exitCode)
    {
        var (result, _) = await RunAsync(
            Events("", error: "Claude usage limit reached. Your limit will reset at 9:40am (UTC)."), exitCode);

        Assert.False(result.Success);
        Assert.Equal(FailureKind.Interrupted, result.Failure);
    }

    [Fact]
    public async Task Without_a_provider_error_nothing_is_classified_at_the_adapter()
    {
        var (result, _) = await RunAsync(Events("the test got 503 Service Unavailable from staging"), exitCode: 1);

        Assert.Equal(FailureKind.Unknown, result.Failure);
    }

    [Fact]
    public async Task A_stream_that_ended_before_its_turn_fails_as_truncated_keeping_the_partial_text()
    {
        var (result, _) = await RunAsync(Events("partial response", truncation: "scripted stream ended early"));

        Assert.False(result.Success);
        Assert.Equal("scripted stream ended early", result.Error);
        Assert.Equal("partial response", result.Output);
    }

    [Fact]
    public async Task A_non_zero_exit_fails_with_the_exit_code_and_stderr_and_keeps_the_text()
    {
        var (result, _) = await RunAsync(Events("partial", truncation: "scripted stream ended early"), exitCode: 2, stderr: "boom");

        Assert.False(result.Success);
        Assert.Equal("exit=2 stderr=boom", result.Error);
        Assert.Equal("partial", result.Output);
    }

    [Fact]
    public async Task A_non_zero_exit_names_the_providers_error_as_its_reason()
    {
        var (result, _) = await RunAsync(Events("", error: "Credit balance is too low"), exitCode: 1);

        Assert.Equal("exit=1 stderr= result=Credit balance is too low", result.Error);
        Assert.NotEqual(FailureKind.Interrupted, result.Failure);
    }

    [Fact]
    public async Task A_non_zero_exit_with_empty_stderr_ends_the_error_with_the_text_else_the_raw_output()
    {
        var (withText, _) = await RunAsync(Events("what it said", raw: "raw one"), exitCode: 1);
        var (withoutText, _) = await RunAsync(Events("", raw: "the cli printed this and died"), exitCode: 1);

        Assert.Equal("exit=1 stderr= stdout=what it said", withText.Error);
        Assert.Equal("exit=1 stderr= stdout=the cli printed this and died", withoutText.Error);
        Assert.Equal("[scripted] no assistant text response from model", withoutText.Output);
    }

    [Fact]
    public async Task A_non_zero_exit_of_output_that_was_no_event_stream_carries_it_in_the_error()
    {
        var (result, _) = await RunAsync(new StreamResult("", null, null, SawJsonEvents: false, RawStdout: "boom from the cli"), exitCode: 1);

        Assert.Contains("boom from the cli", result.Error);
        Assert.Equal("boom from the cli", result.Output);
    }

    [Fact]
    public async Task The_character_a_text_column_cannot_store_is_stripped_from_everything_the_result_carries()
    {
        var (result, _) = await RunAsync(
            Events("an\0swer", error: "bro\0ke", raw: "ra\0w"), exitCode: 1, stderr: "std\0err");

        Assert.Equal("exit=1 stderr=stderr result=broke", result.Error);
        Assert.Equal("answer", result.Output);
    }

    [Fact]
    public async Task A_turn_that_fails_is_not_asked_what_it_used()
    {
        var (_, adapter) = await RunAsync(Events("text"), exitCode: 1);

        Assert.DoesNotContain("usage", adapter.Journal);
    }
}
