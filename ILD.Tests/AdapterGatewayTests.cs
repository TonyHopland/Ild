using ILD.Core.Services.Implementations.Files;
using System.Diagnostics;
using System.Text.Json;
using ILD.Core.Services.Implementations;
using ILD.Core.Services.Implementations.Adapters;
using ILD.Core.Services.Implementations.ModelGateway;
using ILD.Core.Services.RunHosts.Isolated;
using ILD.Data.DTOs;
using ILD.Data.Entities;

namespace ILD.Tests;

/// <summary>
/// With the gateway in use the CLI is launched with a placeholder: the real credential is in no
/// argument, environment variable or file the agent can read (ADR-0031).
/// </summary>
public class AdapterGatewayTests
{
    private const string RealSecret = "REAL-SECRET-0123456789";

    private readonly ModelGatewayRegistry _registry = new();
    private readonly ModelGatewayLease _lease;

    public AdapterGatewayTests()
    {
        _lease = _registry.Open(Guid.NewGuid(), Guid.NewGuid(), "http://127.0.0.1:8080", []);
    }

    private static AiProvider Provider(string type, string? key = RealSecret, string baseUrl = "")
        => new() { Id = Guid.NewGuid(), Name = "p-" + type, Type = type, ApiKey = key, BaseUrl = baseUrl, Model = "m" };

    private static void AssertHoldsNoSecret(ProcessStartInfo psi, IDictionary<string, string?> environment)
    {
        Assert.DoesNotContain(RealSecret, string.Join('\n', psi.ArgumentList));
        foreach (var (name, value) in environment)
            Assert.DoesNotContain(RealSecret, value ?? string.Empty);
    }

    [Fact]
    public void Claude_with_a_subscription_token_gets_the_gateway_url_and_a_placeholder_oauth_token()
    {
        var psi = ClaudeCodeAdapter.BuildRunProcessStartInfo("claude", Path.GetTempPath(), "hi", null);

        var environment = new Dictionary<string, string?>();
        ClaudeCodeAdapter.ApplyGateway(environment, _lease, Provider("claude-code", "sk-ant-oat01-" + RealSecret));

        Assert.Equal("http://127.0.0.1:8080/gateway/anthropic", environment["ANTHROPIC_BASE_URL"]);
        Assert.Equal(_lease.Token, environment["CLAUDE_CODE_OAUTH_TOKEN"]);
        Assert.Null(environment["ANTHROPIC_API_KEY"]);
        Assert.Equal("1", environment["CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC"]);
        AssertHoldsNoSecret(psi, environment);
    }

    [Fact]
    public void Claude_with_an_api_key_gets_a_placeholder_key()
    {
        var psi = ClaudeCodeAdapter.BuildRunProcessStartInfo("claude", Path.GetTempPath(), "hi", null);

        var environment = new Dictionary<string, string?>();
        ClaudeCodeAdapter.ApplyGateway(environment, _lease, Provider("claude-code", "sk-ant-api03-" + RealSecret));

        Assert.Equal(_lease.Token, environment["ANTHROPIC_API_KEY"]);
        Assert.Null(environment["CLAUDE_CODE_OAUTH_TOKEN"]);
        AssertHoldsNoSecret(psi, environment);
    }

    [Fact]
    public void Codex_gets_its_own_http_provider_and_a_home_of_its_own_for_the_run()
    {
        var psi = CodexAdapter.BuildRunProcessStartInfo("codex", Path.GetTempPath(), "hi", "sess-1", mcpServers: []);

        var environment = new Dictionary<string, string?>();
        CodexAdapter.ApplyGateway(psi, environment, _lease, "/scratch/ild-codex-home/run");

        var arguments = psi.ArgumentList.ToList();
        Assert.Equal("exec", arguments[0]);
        Assert.Equal("-c", arguments[1]);
        Assert.Contains("chatgpt_base_url=\"http://127.0.0.1:8080/gateway/chatgpt\"", arguments);
        var provider = arguments.Single(a => a.StartsWith("model_providers.ild=", StringComparison.Ordinal));
        Assert.Contains("base_url=\"http://127.0.0.1:8080/gateway/chatgpt/codex\"", provider);
        Assert.Contains("supports_websockets=false", provider);
        Assert.Contains("requires_openai_auth=true", provider);
        Assert.Contains("model_provider=\"ild\"", arguments);
        Assert.True(arguments.IndexOf("resume") > arguments.LastIndexOf("-c"), "options must come before the resume subcommand");
        Assert.Equal("/scratch/ild-codex-home/run", environment["CODEX_HOME"]);
        Assert.DoesNotContain(_lease.Token, string.Join('\n', arguments));
        AssertHoldsNoSecret(psi, environment);
    }

    [Fact]
    public void The_codex_login_file_holds_only_the_run_token_in_the_shape_codex_checks()
    {
        var now = new DateTimeOffset(2026, 11, 6, 12, 0, 0, TimeSpan.Zero);

        var login = JsonDocument.Parse(CodexAdapter.BuildPlaceholderLogin(now, _lease.Token)).RootElement;

        Assert.Equal("chatgpt", login.GetProperty("auth_mode").GetString());
        var tokens = login.GetProperty("tokens");
        Assert.DoesNotContain(RealSecret, login.ToString());
        Assert.Equal(_lease.Token, tokens.GetProperty("access_token").GetString());
        var parts = tokens.GetProperty("id_token").GetString()!.Split('.');
        Assert.Equal(3, parts.Length);
        var payload = parts[1].Replace('-', '+').Replace('_', '/');
        payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
        var claims = JsonDocument.Parse(Convert.FromBase64String(payload)).RootElement;
        Assert.True(claims.TryGetProperty("https://api.openai.com/auth", out _));
        Assert.Equal(now.UtcDateTime, login.GetProperty("last_refresh").GetDateTimeOffset().UtcDateTime);
    }

    [Fact]
    public void Copilot_gets_the_https_listener_a_github_shaped_placeholder_and_the_certificate_to_trust()
    {
        var psi = CopilotAdapter.BuildRunProcessStartInfo("copilot", Path.GetTempPath(), "hi");
        var environment = new Dictionary<string, string?>();
        Assert.True(CopilotAdapter.TryApplyGateway(psi, environment, _lease, 8442, "/read/ca-copy.pem"));

        Assert.Equal("127.0.0.1:8442", environment["GH_HOST"]);
        Assert.Equal("gho_" + _lease.Token, environment["COPILOT_GITHUB_TOKEN"]);
        Assert.Equal("/read/ca-copy.pem", environment["NODE_EXTRA_CA_CERTS"]);
        Assert.Equal("--no-auto-update", psi.ArgumentList[0]);
        AssertHoldsNoSecret(psi, environment);
    }

    [Fact]
    public void Copilot_cannot_use_the_gateway_without_a_listener()
    {
        var psi = CopilotAdapter.BuildRunProcessStartInfo("copilot", Path.GetTempPath(), "hi");

        var environment = new Dictionary<string, string?>();
        Assert.False(CopilotAdapter.TryApplyGateway(psi, environment, _lease, null, "/read/ca-copy.pem"));
        Assert.False(environment.ContainsKey("GH_HOST"));
    }

    [Fact]
    public void OpenCode_gets_the_gateway_url_and_a_placeholder_key_in_its_config()
    {
        var provider = Provider("opencode", baseUrl: "https://models.example.test/v1");

        var (_, json) = OpenCodeAdapter.BuildOpenCodeConfig(provider, new TestProcessEnvironment(), noTools: true, gateway: _lease);

        Assert.DoesNotContain(RealSecret, json);
        Assert.DoesNotContain("models.example.test", json);
        var options = JsonDocument.Parse(json).RootElement.GetProperty("provider").EnumerateObject().Single().Value.GetProperty("options");
        Assert.Equal("http://127.0.0.1:8080/gateway/upstream", options.GetProperty("baseURL").GetString());
        Assert.Equal(_lease.Token, options.GetProperty("apiKey").GetString());
    }

    [Fact]
    public void OpenCode_without_the_gateway_is_configured_as_before()
    {
        var provider = Provider("opencode", baseUrl: "https://models.example.test/v1");

        var (_, json) = OpenCodeAdapter.BuildOpenCodeConfig(provider, new TestProcessEnvironment(), noTools: true);

        var options = JsonDocument.Parse(json).RootElement.GetProperty("provider").EnumerateObject().Single().Value.GetProperty("options");
        Assert.Equal("https://models.example.test/v1", options.GetProperty("baseURL").GetString());
        Assert.Equal(RealSecret, options.GetProperty("apiKey").GetString());
    }

    [Fact]
    public void Pi_with_a_base_url_gets_the_gateway_url_and_a_placeholder_key_in_models_json_and_environment()
    {
        var provider = Provider("pi", baseUrl: "https://models.example.test/v1");
        var runContext = new LoopRunContext(Guid.NewGuid(), "1", "t", "d", Path.GetTempPath(), "b", [], null);

        var settings = PiAdapter.ResolveSettings(provider, runContext, null, null, new TestProcessEnvironment(), noTools: true, gateway: _lease);
        var psi = PiAdapter.BuildRunProcessStartInfo(settings, Path.GetTempPath(), Path.GetTempPath(), null, null);

        var environment = PiAdapter.BuildRunEnvironment(settings, Path.GetTempPath());

        Assert.Equal(_lease.Token, environment["ILD_PI_PROVIDER_API_KEY"]);
        Assert.DoesNotContain(RealSecret, settings.ModelsJsonContent);
        Assert.DoesNotContain("models.example.test", settings.ModelsJsonContent);
        Assert.Contains("http://127.0.0.1:8080/gateway/upstream", settings.ModelsJsonContent);
        Assert.Null(settings.ReadOnlyAuthJson);
        AssertHoldsNoSecret(psi, environment);
    }

    [Fact]
    public void A_base_url_provider_without_a_key_is_pointed_at_the_gateway_too()
    {
        var runContext = new LoopRunContext(Guid.NewGuid(), "1", "t", "d", Path.GetTempPath(), "b", [], null);

        var (_, json) = OpenCodeAdapter.BuildOpenCodeConfig(Provider("opencode", null, "http://models.example.test:1234/v1"), new TestProcessEnvironment(), noTools: true, gateway: _lease);
        var settings = PiAdapter.ResolveSettings(Provider("pi", null, "http://models.example.test:1234/v1"), runContext, null, null, new TestProcessEnvironment(), noTools: true, gateway: _lease);

        var options = JsonDocument.Parse(json).RootElement.GetProperty("provider").EnumerateObject().Single().Value.GetProperty("options");
        Assert.Equal("http://127.0.0.1:8080/gateway/upstream", options.GetProperty("baseURL").GetString());
        Assert.Equal(_lease.Token, options.GetProperty("apiKey").GetString());
        Assert.DoesNotContain("models.example.test", settings.ModelsJsonContent);
        Assert.Contains("http://127.0.0.1:8080/gateway/upstream", settings.ModelsJsonContent);
        Assert.Equal(_lease.Token, PiAdapter.BuildRunEnvironment(settings, Path.GetTempPath())["ILD_PI_PROVIDER_API_KEY"]);
    }

    [Fact]
    public void Pi_in_a_run_of_its_own_keeps_its_agent_directory_in_the_runs_home()
    {
        var provider = Provider("pi", baseUrl: "https://models.example.test/v1");
        var runContext = new LoopRunContext(Guid.NewGuid(), "1", "t", "d", Path.GetTempPath(), "b", [], null);
        var runHome = RunPaths.HomeDirectory(runContext.LoopRunId);

        var settings = PiAdapter.ResolveSettings(provider, runContext, null, null, new TestProcessEnvironment(), noTools: true, gateway: _lease, runHome: runHome);

        Assert.Equal(Path.Combine(runHome, ".pi", "agent"), settings.AgentDirectory);
        Assert.Equal(settings.AgentDirectory, PiAdapter.BuildRunEnvironment(settings, Path.GetTempPath())["PI_CODING_AGENT_DIR"]);
        Assert.NotNull(RunPaths.OwnerOf(settings.AgentDirectory));
    }

    [Fact]
    public void Pi_with_a_built_in_provider_gets_a_read_only_copy_of_its_login_for_the_run()
    {
        var home = Directory.CreateTempSubdirectory("ild-pi-home-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(home, ".pi", "agent"));
            File.WriteAllText(Path.Combine(home, ".pi", "agent", "auth.json"), "{\"anthropic\":{\"type\":\"oauth\"}}");
            var provider = Provider("pi", key: null);
            provider.Config = "{\"provider\":\"anthropic\",\"model\":\"claude-x\"}";
            var runContext = new LoopRunContext(Guid.NewGuid(), "1", "t", "d", Path.GetTempPath(), "b", [], null);

            var settings = PiAdapter.ResolveSettings(provider, runContext, null, null, new TestProcessEnvironment { { "HOME", home } }, noTools: true, gateway: _lease);

            Assert.Equal("{\"anthropic\":{\"type\":\"oauth\"}}", settings.ReadOnlyAuthJson);
            Assert.NotNull(settings.AgentDirectory);
            Assert.Contains(runContext.LoopRunId.ToString("N"), settings.AgentDirectory);
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }

    [Theory]
    [InlineData("claude-code", "", "gateway")]
    [InlineData("codex", "", "gateway")]
    [InlineData("copilot", "", "gateway")]
    public void The_subscription_adapters_report_the_gateway_path(string type, string baseUrl, string expected)
    {
        var adapter = Adapter(type);

        Assert.Equal(expected, adapter.CredentialPathFor(Provider(type, null, baseUrl))!.Value.ToString().ToLowerInvariant());
    }

    [Fact]
    public void Base_url_adapters_report_the_path_only_when_they_hold_a_credential()
    {
        var withUrl = "https://models.example.test/v1";

        Assert.Equal(CredentialPath.Gateway, Adapter("opencode").CredentialPathFor(Provider("opencode", baseUrl: withUrl)));
        Assert.Null(Adapter("opencode").CredentialPathFor(Provider("opencode", null, withUrl)));
        Assert.Equal(CredentialPath.Gateway, Adapter("pi").CredentialPathFor(Provider("pi", baseUrl: withUrl)));
        Assert.Null(Adapter("pi").CredentialPathFor(Provider("pi", null, withUrl)));
        Assert.Equal(CredentialPath.ReadOnlyCopy, Adapter("pi").CredentialPathFor(Provider("pi", null, "pi")));
    }

    [Fact]
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    public async Task A_read_only_copy_is_a_file_only_its_owner_can_read_and_nobody_can_change()
    {
        var directory = Directory.CreateTempSubdirectory("ild-copy-").FullName;
        try
        {
            var path = Path.Combine(directory, "auth.json");
            await AgentWritableFiles.WriteFileAsync(path, "old", TestContext.Current.CancellationToken);

            await AgentWritableFiles.WriteReadOnlyFileAsync(path, "{\"login\":1}", TestContext.Current.CancellationToken);

            Assert.Equal("{\"login\":1}", File.ReadAllText(path));
            Assert.Equal(UnixFileMode.UserRead, File.GetUnixFileMode(path));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData("Claude-Code")]
    [InlineData(" codex ")]
    public void The_route_follows_the_provider_type_whatever_its_case(string type)
        => Assert.NotNull(GatewayRoutes.RouteFor(type));

    private static ILD.Core.Services.Interfaces.IAgentAdapter Adapter(string type) => type switch
    {
        "claude-code" => new ClaudeCodeAdapter(),
        "codex" => new CodexAdapter(),
        "copilot" => new CopilotAdapter(),
        "opencode" => new OpenCodeAdapter(),
        _ => new PiAdapter(),
    };
}
