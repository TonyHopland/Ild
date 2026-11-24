using System.Text.Json;
using ILD.Core.Services.Implementations.Adapters;
using ILD.Core.Services.Implementations.ModelGateway;
using ILD.Data.DTOs;
using ILD.Data.Entities;

namespace ILD.Tests;

public class AgyAdapterTests
{
    private const string Conversation = "31fe3f09-6406-48c6-862e-cf5eb36655c8";

    [Fact]
    public void The_adapter_declares_its_type_model_support_and_session_resume()
    {
        var adapter = new AgyAdapter();

        Assert.Equal("Agy", adapter.Name);
        Assert.Equal(["agy"], adapter.SupportedProviderTypes);
        Assert.Equal(AdapterModelSupport.Optional, adapter.ModelSupport);
        Assert.True(adapter.Traits.SupportsSessionResume);
        Assert.False(adapter.Traits.SupportsNoTools);
        Assert.Equal(["customMcpServersJson", "extraArgs"], adapter.ConfigSchema.Select(f => f.Name).Order());
    }

    [Fact]
    public void A_headless_launch_skips_permissions_streams_json_and_ends_with_the_prompt()
    {
        var psi = AgyAdapter.BuildRunProcessStartInfo("agy", "/work", "do the thing", null);

        Assert.Equal("agy", psi.FileName);
        Assert.Equal("/work", psi.WorkingDirectory);
        Assert.Equal(
            ["--dangerously-skip-permissions", "--disable-slash-commands", "--output-format", "stream-json", "-p", "do the thing"],
            psi.ArgumentList);
    }

    [Fact]
    public void A_prompt_that_looks_like_a_flag_or_a_slash_command_is_still_only_the_prompt()
    {
        var psi = AgyAdapter.BuildRunProcessStartInfo("agy", "/work", "--model x /help", null);

        Assert.Equal("--model x /help", psi.ArgumentList[^1]);
        Assert.Equal("-p", psi.ArgumentList[^2]);
        Assert.Contains("--disable-slash-commands", psi.ArgumentList);
    }

    [Fact]
    public void The_model_and_the_conversation_are_passed_when_set_and_the_extra_arguments_come_before_the_prompt()
    {
        var psi = AgyAdapter.BuildRunProcessStartInfo("agy", "/work", "hi", Conversation, model: "gemini-3-flash-preview", extraArgs: ["--effort", "high"]);

        var arguments = psi.ArgumentList.ToList();
        Assert.Equal("gemini-3-flash-preview", arguments[arguments.IndexOf("--model") + 1]);
        Assert.Equal(Conversation, arguments[arguments.IndexOf("--conversation") + 1]);
        Assert.True(arguments.IndexOf("--effort") < arguments.IndexOf("-p"));
    }

    [Fact]
    public void Without_a_model_or_a_conversation_neither_flag_is_passed()
    {
        var psi = AgyAdapter.BuildRunProcessStartInfo("agy", "/work", "hi", " ", model: " ");

        Assert.DoesNotContain("--model", psi.ArgumentList);
        Assert.DoesNotContain("--conversation", psi.ArgumentList);
    }

    [Fact]
    public void Each_extra_directory_is_granted_except_the_worktree_itself()
    {
        var psi = AgyAdapter.BuildRunProcessStartInfo("agy", "/work", "hi", null, ["/work", "/other", " "]);

        Assert.Equal(["--add-dir", "/other"], psi.ArgumentList.SkipWhile(a => a != "--add-dir").Take(2));
        Assert.Single(psi.ArgumentList, a => a == "--add-dir");
    }

    [Theory]
    [InlineData("--conversation x")]
    [InlineData("--dangerously-skip-permissions")]
    [InlineData("--output-format json")]
    [InlineData("--settings {}")]
    [InlineData("-p hi")]
    public void The_flags_ild_sets_cannot_be_repeated_as_extra_arguments(string extra)
        => Assert.NotNull(ExtraCliArgs.Validate(AgyAdapter.TypeTraits.ExtraCliArgs, extra));

    [Fact]
    public void Extra_arguments_such_as_the_reasoning_effort_are_accepted()
        => Assert.Null(ExtraCliArgs.Validate(AgyAdapter.TypeTraits.ExtraCliArgs, "--effort high"));

    [Fact]
    public void A_conversation_is_one_file_under_the_home_and_only_a_guid_names_one()
    {
        Assert.Equal(
            $"/home/run/.gemini/antigravity-cli/conversations/{Conversation}.db",
            AgyAdapter.ConversationPath("/home/run", Conversation));
        Assert.Equal("/home/run/.gemini/config/mcp_config.json", AgyAdapter.McpConfigPath("/home/run"));
    }

    [Fact]
    public void Merging_the_mcp_config_keeps_the_users_servers_and_sets_ilds_over_the_same_names()
    {
        var existing = """{"mcpServers":{"mine":{"command":"x"},"ild":{"command":"old"}},"other":1}""";
        var servers = new Dictionary<string, object?> { ["ild"] = new Dictionary<string, object?> { ["command"] = "dotnet" } };

        var merged = JsonDocument.Parse(AgyAdapter.MergeMcpConfig(existing, servers)).RootElement;

        Assert.Equal("x", merged.GetProperty("mcpServers").GetProperty("mine").GetProperty("command").GetString());
        Assert.Equal("dotnet", merged.GetProperty("mcpServers").GetProperty("ild").GetProperty("command").GetString());
        Assert.Equal(1, merged.GetProperty("other").GetInt32());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[1]")]
    public void A_missing_or_unreadable_mcp_config_is_replaced(string? existing)
    {
        var servers = new Dictionary<string, object?> { ["ild"] = new Dictionary<string, object?> { ["command"] = "dotnet" } };

        var merged = JsonDocument.Parse(AgyAdapter.MergeMcpConfig(existing, servers)).RootElement;

        Assert.Equal("dotnet", merged.GetProperty("mcpServers").GetProperty("ild").GetProperty("command").GetString());
    }

    private static AiProvider ProviderWithCustomServers() => new()
    {
        Name = "agy",
        Type = "agy",
        Config = """{"customMcpServersJson":"{\"docs\":{\"command\":\"npx\",\"args\":[\"-y\",\"docs-mcp\"],\"env\":{\"K\":\"v\"}}}"}""",
    };

    private static LoopRunContext RunContext() => new(Guid.NewGuid(), "WI-1", "t", "d", "/work", "main", [], "");

    [Fact]
    public void Custom_servers_are_given_even_when_the_ild_tool_is_not_selected()
    {
        var servers = AgyAdapter.BuildMcpServers(ProviderWithCustomServers(), RunContext(), [], null, new TestProcessEnvironment(), noTools: false);

        var docs = Assert.IsType<Dictionary<string, object?>>(Assert.Single(servers).Value);
        Assert.Equal("docs", Assert.Single(servers.Keys));
        Assert.Equal("npx", docs["command"]);
        Assert.Equal(["-y", "docs-mcp"], (string[])docs["args"]!);
    }

    [Fact]
    public void A_call_that_must_have_no_tools_is_given_no_servers()
        => Assert.Empty(AgyAdapter.BuildMcpServers(ProviderWithCustomServers(), RunContext(), null, null, new TestProcessEnvironment(), noTools: true));

    // -- A whole turn against a stand-in CLI ----------------------------------------

    private const string SuccessTurn =
        "echo '{\"event\":\"init\",\"conversation_id\":\"31fe3f09-6406-48c6-862e-cf5eb36655c8\",\"init\":{}}'\n"
        + "echo '{\"event\":\"step_update\",\"step_update\":{\"step_index\":1,\"step_type\":\"agent_response\",\"text_delta\":\"ok\",\"usage\":{\"input_tokens\":11,\"output_tokens\":3}}}'\n"
        + "echo '{\"event\":\"result\",\"result\":{\"conversation_id\":\"31fe3f09-6406-48c6-862e-cf5eb36655c8\",\"status\":\"SUCCESS\",\"response\":\"ok\"}}'\n";

    private static AgentExecutionContext Context(PromptCapturingCli cli, string? sessionId = null)
        => new(
            Provider: new AiProvider
            {
                Name = "agy-test",
                Type = "agy",
                Model = "gemini-3-flash-preview",
                Config = $"{{\"binaryPath\":\"{cli.BinaryPath}\"}}",
            },
            Prompt: "hello",
            RunContext: new LoopRunContext(Guid.NewGuid(), "WI-1", "t", "d", cli.WorkDir, "main", [], ""),
            ExecutionCount: 1,
            Cancel: CancellationToken.None,
            SessionId: sessionId);

    [Fact]
    public async Task A_turn_returns_the_answer_the_conversation_id_and_the_usage()
    {
        using var cli = new PromptCapturingCli(turn: SuccessTurn);

        var result = await new AgyAdapter(environment: new TestProcessEnvironment()).ExecuteAsync(Context(cli));

        Assert.True(result.Success, result.Error);
        Assert.Equal("ok", result.Output);
        Assert.Equal(Conversation, result.SessionId);
        Assert.Equal(11, result.Usage!.InputTokens);
        Assert.Equal(3, result.Usage.OutputTokens);
        Assert.Equal("hello", cli.CapturedPrompt);
        Assert.Contains("gemini-3-flash-preview", cli.ArgvFor(1));
    }

    [Fact]
    public async Task A_turn_given_a_conversation_resumes_it()
    {
        using var cli = new PromptCapturingCli(turn: SuccessTurn);

        var result = await new AgyAdapter(environment: new TestProcessEnvironment()).ExecuteAsync(Context(cli, Conversation));

        Assert.True(result.Success, result.Error);
        var argv = cli.ArgvFor(1).ToList();
        Assert.Equal(Conversation, argv[argv.IndexOf("--conversation") + 1]);
    }

    [Fact]
    public async Task Without_a_key_the_turn_runs_on_the_stored_google_sign_in_and_keeps_the_token_in_a_file()
    {
        using var cli = new PromptCapturingCli(turn: "printf '%s' \"$AGY_CLI_FORCE_FILE_TOKEN_STORAGE\" > env-seen.txt\n" + SuccessTurn);
        var home = Directory.CreateTempSubdirectory("ild-agy-home-").FullName;
        try
        {
            var token = Path.Combine(home, AgyLogin.TokenFile);
            Directory.CreateDirectory(Path.GetDirectoryName(token)!);
            File.WriteAllText(token, "token");
            var environment = new TestProcessEnvironment { { "HOME", home } };

            var result = await new AgyAdapter(environment: environment).ExecuteAsync(Context(cli));

            Assert.True(result.Success, result.Error);
            Assert.Equal("1", File.ReadAllText(Path.Combine(cli.WorkDir, "env-seen.txt")));
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }

    [Fact]
    public async Task An_account_file_without_a_token_is_not_a_sign_in()
    {
        using var cli = new PromptCapturingCli(turn: "printf '%s' \"$AGY_CLI_FORCE_FILE_TOKEN_STORAGE\" > env-seen.txt\n" + SuccessTurn);
        var home = Directory.CreateTempSubdirectory("ild-agy-home-").FullName;
        try
        {
            var scope = Path.Combine(home, AgyLogin.ScopeFile);
            Directory.CreateDirectory(Path.GetDirectoryName(scope)!);
            File.WriteAllText(scope, "account");

            await new AgyAdapter(environment: new TestProcessEnvironment { { "HOME", home } }).ExecuteAsync(Context(cli));

            Assert.Equal("", File.ReadAllText(Path.Combine(cli.WorkDir, "env-seen.txt")));
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }

    // -- A home of its own, and a home shared ---------------------------------------------------------------------------

    /// <summary>The local run host, except that the CLIs of a run work in <c>runHome</c>, as in a container or pod.</summary>
    private class HostWithRunHome : System.Reflection.DispatchProxy
    {
        public string RunHome { get; set; } = "";

        protected override object? Invoke(System.Reflection.MethodInfo? targetMethod, object?[]? args)
            => targetMethod!.Name == nameof(ILD.Core.Services.RunHosts.IRunWorkspace.RunHomeFor)
                ? RunHome
                : targetMethod.Invoke(ILD.Core.Services.RunHosts.Local.LocalRunHost.Shared, args);
    }

    private static Microsoft.Extensions.DependencyInjection.IServiceScopeFactory ScopesWithRunHome(string runHome)
    {
        var host = System.Reflection.DispatchProxy.Create<ILD.Core.Services.RunHosts.IRunWorkspace, HostWithRunHome>();
        ((HostWithRunHome)(object)host).RunHome = runHome;
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddSingleton(services, host);
        return Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<Microsoft.Extensions.DependencyInjection.IServiceScopeFactory>(
            Microsoft.Extensions.DependencyInjection.ServiceCollectionContainerBuilderExtensions.BuildServiceProvider(services));
    }

    [Fact]
    public async Task A_run_in_a_home_of_its_own_is_given_the_sign_in_for_the_turn_and_it_is_gone_when_the_turn_ends()
    {
        var store = Directory.CreateTempSubdirectory("ild-agy-store-").FullName;
        var runHome = Directory.CreateTempSubdirectory("ild-agy-run-").FullName;
        try
        {
            foreach (var file in AgyLogin.Files)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(store, file))!);
                File.WriteAllText(Path.Combine(store, file), "secret-" + Path.GetFileName(file));
            }
            using var cli = new PromptCapturingCli(turn:
                $"cat '{runHome}/{AgyLogin.TokenFile}' > seen-token.txt\nprintf '%s' \"$AGY_CLI_FORCE_FILE_TOKEN_STORAGE\" > env-seen.txt\n" + SuccessTurn);
            var adapter = new AgyAdapter(ScopesWithRunHome(runHome), environment: new TestProcessEnvironment { { "HOME", store } });

            var result = await adapter.ExecuteAsync(Context(cli));

            Assert.True(result.Success, result.Error);
            Assert.Equal("secret-antigravity-oauth-token", File.ReadAllText(Path.Combine(cli.WorkDir, "seen-token.txt")));
            Assert.Equal("1", File.ReadAllText(Path.Combine(cli.WorkDir, "env-seen.txt")));
            foreach (var file in AgyLogin.Files)
            {
                Assert.False(File.Exists(Path.Combine(runHome, file)), file);
                Assert.True(File.Exists(Path.Combine(store, file)), file);
            }
        }
        finally
        {
            Directory.Delete(store, recursive: true);
            Directory.Delete(runHome, recursive: true);
        }
    }

    [Fact]
    public async Task A_turn_cancelled_while_it_waits_for_the_mcp_config_does_not_let_the_next_one_in_early()
    {
        var home = Directory.CreateTempSubdirectory("ild-agy-home-").FullName;
        try
        {
            var log = Path.Combine(home, "turns.log");
            string Turn(string name, int seconds) => $"echo 'start {name}' >> '{log}'\nsleep {seconds}\necho 'end {name}' >> '{log}'\n" + SuccessTurn;
            using var holder = new PromptCapturingCli(turn: Turn("a", 3));
            using var cancelled = new PromptCapturingCli(turn: Turn("b", 0));
            using var later = new PromptCapturingCli(turn: Turn("c", 0));
            AgentExecutionContext WithServer(PromptCapturingCli cli, CancellationToken cancel) => Context(cli) with
            {
                Cancel = cancel,
                Provider = new AiProvider
                {
                    Name = "agy-test", Type = "agy",
                    Config = JsonSerializer.Serialize(new Dictionary<string, string>
                    {
                        ["binaryPath"] = cli.BinaryPath,
                        ["customMcpServersJson"] = """{"docs":{"command":"npx"}}""",
                    }),
                },
            };
            var environment = new TestProcessEnvironment { { "HOME", home } };
            using var cancel = new CancellationTokenSource();

            var first = new AgyAdapter(environment: environment).ExecuteAsync(WithServer(holder, CancellationToken.None));
            await Task.Delay(1000, TestContext.Current.CancellationToken);
            var second = new AgyAdapter(environment: environment).ExecuteAsync(WithServer(cancelled, cancel.Token));
            await Task.Delay(500, TestContext.Current.CancellationToken);
            await cancel.CancelAsync();
            await second;
            var third = new AgyAdapter(environment: environment).ExecuteAsync(WithServer(later, CancellationToken.None));
            await Task.WhenAll(first, third);

            Assert.Equal(["start a", "end a", "start c", "end c"], File.ReadAllLines(log));
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }

    [Fact]
    public async Task Turns_in_one_shared_home_that_write_the_mcp_config_take_turns_and_leave_it_as_it_was()
    {
        var home = Directory.CreateTempSubdirectory("ild-agy-home-").FullName;
        try
        {
            var config = AgyAdapter.McpConfigPath(home);
            Directory.CreateDirectory(Path.GetDirectoryName(config)!);
            File.WriteAllText(config, """{"mcpServers":{"mine":{"command":"x"}}}""");
            var log = Path.Combine(home, "turns.log");
            string Turn(string name) => $"echo 'start {name}' >> '{log}'\nsleep 1\necho 'end {name}' >> '{log}'\n" + SuccessTurn;
            using var first = new PromptCapturingCli(turn: Turn("a"));
            using var second = new PromptCapturingCli(turn: Turn("b"));
            AgentExecutionContext WithServer(PromptCapturingCli cli)
            {
                var ctx = Context(cli);
                return ctx with
                {
                    Provider = new AiProvider
                    {
                        Name = "agy-test", Type = "agy",
                        Config = JsonSerializer.Serialize(new Dictionary<string, string>
                        {
                            ["binaryPath"] = cli.BinaryPath,
                            ["customMcpServersJson"] = """{"docs":{"command":"npx","args":["docs-mcp"]}}""",
                        }),
                    },
                };
            }
            var environment = new TestProcessEnvironment { { "HOME", home } };

            var results = await Task.WhenAll(
                new AgyAdapter(environment: environment).ExecuteAsync(WithServer(first)),
                new AgyAdapter(environment: environment).ExecuteAsync(WithServer(second)));

            Assert.All(results, result => Assert.True(result.Success, result.Error));
            var turns = File.ReadAllLines(log).Select(line => line.Split(' ')[0]).ToArray();
            Assert.Equal(["start", "end", "start", "end"], turns);
            Assert.Equal("""{"mcpServers":{"mine":{"command":"x"}}}""", File.ReadAllText(config));
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }
}
