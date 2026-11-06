using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using ILD.Core.Services.Implementations.ModelGateway;
using ILD.Data.Entities;

namespace ILD.Tests;

public sealed class ModelCredentialSourceTests : IDisposable
{
    private readonly string _home = Path.Combine(Path.GetTempPath(), "ild-cred-" + Guid.NewGuid().ToString("N"));
    private readonly Clock _clock = new();
    private readonly RecordingHandler _handler = new();
    private readonly ModelCredentialSource _source;

    public ModelCredentialSourceTests()
    {
        Directory.CreateDirectory(_home);
        _source = new ModelCredentialSource(new HttpClient(_handler), null, new TestProcessEnvironment { { "HOME", _home } }, _clock);
    }

    public void Dispose() => Directory.Delete(_home, recursive: true);

    private static AiProvider Provider(string type, string? apiKey = null, string baseUrl = "")
        => new() { Id = Guid.NewGuid(), Name = type, Type = type, ApiKey = apiKey, BaseUrl = baseUrl, Model = "m" };

    private string Write(string relative, string content)
    {
        var path = Path.Combine(_home, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private static string Jwt(DateTimeOffset expires)
    {
        static string Part(string json) => Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return $"{Part("{\"alg\":\"none\"}")}.{Part($"{{\"exp\":{expires.ToUnixTimeSeconds()}}}")}.sig";
    }

    private string WriteCodexLogin(string access, DateTimeOffset lastRefresh, string refresh = "refresh-1")
        => Write(".codex/auth.json", new JsonObject
        {
            ["auth_mode"] = "chatgpt",
            ["tokens"] = new JsonObject
            {
                ["id_token"] = "id",
                ["access_token"] = access,
                ["refresh_token"] = refresh,
                ["account_id"] = "acct-1",
                ["oauth_provenance"] = new JsonObject { ["client_id"] = "client-1" },
            },
            ["last_refresh"] = lastRefresh.UtcDateTime.ToString("O"),
        }.ToJsonString());

    [Fact]
    public async Task Claude_uses_the_token_saved_on_the_provider_over_the_stored_login()
    {
        Write(".claude/.credentials.json", """{"claudeAiOauth":{"accessToken":"stored","expiresAt":9999999999999}}""");

        var credential = await _source.ResolveAsync(Provider("claude-code", " sk-ant-oat01-saved "), GatewayRoutes.Anthropic, TestContext.Current.CancellationToken);

        Assert.Equal("sk-ant-oat01-saved", credential!.Secret);
        Assert.Equal("api.anthropic.com", credential.Upstream.Host);
    }

    [Fact]
    public async Task Claude_falls_back_to_the_stored_access_token_until_it_expires()
    {
        var expiresAt = _clock.GetUtcNow().AddHours(2).ToUnixTimeMilliseconds();
        Write(".claude/.credentials.json", new JsonObject
        {
            ["claudeAiOauth"] = new JsonObject { ["accessToken"] = "stored", ["refreshToken"] = "r", ["expiresAt"] = expiresAt },
        }.ToJsonString());

        Assert.Equal("stored", (await _source.ResolveAsync(Provider("claude-code"), GatewayRoutes.Anthropic, TestContext.Current.CancellationToken))!.Secret);

        _clock.Now += TimeSpan.FromHours(3);
        Assert.Null(await _source.ResolveAsync(Provider("claude-code"), GatewayRoutes.Anthropic, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_route_the_provider_type_does_not_use_resolves_to_nothing()
    {
        Assert.Null(await _source.ResolveAsync(Provider("claude-code", "k"), GatewayRoutes.GitHub, TestContext.Current.CancellationToken));
        Assert.Null(await _source.ResolveAsync(Provider("opencode", "k", "https://models.example.test/v1"), GatewayRoutes.Anthropic, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_base_url_provider_forwards_to_its_own_base_url_with_its_key()
    {
        var credential = await _source.ResolveAsync(
            Provider("pi", "key-1", "https://models.example.test/v1"), GatewayRoutes.Upstream, TestContext.Current.CancellationToken);

        Assert.Equal("https://models.example.test/v1", credential!.Upstream.ToString());
        Assert.Equal("key-1", credential.Secret);
    }

    [Fact]
    public async Task A_base_url_provider_without_a_key_has_no_credential()
    {
        Assert.Null(await _source.ResolveAsync(Provider("opencode", null, "https://models.example.test/v1"), GatewayRoutes.Upstream, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Copilot_reads_the_token_of_the_last_logged_in_user_from_the_commented_config()
    {
        Write(".copilot/config.json", """
            // User settings belong in settings.json.
            // This file is managed automatically.
            {
              "authTokens": {
                "https://github.com:someone": { "token": "gho_real" },
                "https://github.com:other": { "token": "gho_other" }
              },
              "lastLoggedInUser": { "host": "https://github.com", "login": "someone" }
            }
            """);

        var credential = await _source.ResolveAsync(Provider("copilot"), GatewayRoutes.GitHub, TestContext.Current.CancellationToken);

        Assert.Equal("gho_real", credential!.Secret);
        Assert.Equal("api.github.com", credential.Upstream.Host);
    }

    [Fact]
    public async Task Codex_hands_out_a_fresh_login_as_it_is_with_its_account()
    {
        WriteCodexLogin(Jwt(_clock.GetUtcNow().AddDays(9)), _clock.GetUtcNow().AddDays(-1));

        var credential = await _source.ResolveAsync(Provider("codex"), GatewayRoutes.ChatGpt, TestContext.Current.CancellationToken);

        Assert.StartsWith("ey", credential!.Secret);
        Assert.Equal("acct-1", credential.Headers["ChatGPT-Account-ID"]);
        Assert.Equal("https://chatgpt.com/backend-api", credential.Upstream.ToString());
        Assert.Empty(_handler.Requests);
    }

    [Fact]
    public async Task Codex_refreshes_once_the_login_is_older_than_eight_days_and_writes_the_result_back()
    {
        var path = WriteCodexLogin(Jwt(_clock.GetUtcNow().AddDays(9)), _clock.GetUtcNow().AddDays(-9));
        _handler.Reply = """{"access_token":"new-access","refresh_token":"refresh-2","id_token":"new-id"}""";

        var credential = await _source.ResolveAsync(Provider("codex"), GatewayRoutes.ChatGpt, TestContext.Current.CancellationToken);

        Assert.Equal("new-access", credential!.Secret);
        var request = Assert.Single(_handler.Requests);
        Assert.Equal("https://auth.openai.com/oauth/token", request.Uri);
        var sent = JsonNode.Parse(request.Body)!;
        Assert.Equal("refresh-1", (string?)sent["refresh_token"]);
        Assert.Equal("refresh_token", (string?)sent["grant_type"]);
        Assert.Equal("client-1", (string?)sent["client_id"]);

        var stored = JsonNode.Parse(File.ReadAllText(path))!;
        Assert.Equal("new-access", (string?)stored["tokens"]!["access_token"]);
        Assert.Equal("refresh-2", (string?)stored["tokens"]!["refresh_token"]);
        Assert.Equal("acct-1", (string?)stored["tokens"]!["account_id"]);
        Assert.Equal("chatgpt", (string?)stored["auth_mode"]);
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp"));
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
    }

    [Fact]
    public async Task Codex_refreshes_when_the_access_token_is_about_to_expire()
    {
        WriteCodexLogin(Jwt(_clock.GetUtcNow().AddHours(3)), _clock.GetUtcNow().AddDays(-1));
        _handler.Reply = """{"access_token":"new-access"}""";

        Assert.Equal("new-access", (await _source.ResolveAsync(Provider("codex"), GatewayRoutes.ChatGpt, TestContext.Current.CancellationToken))!.Secret);
    }

    [Fact]
    public async Task Codex_refreshes_on_the_next_request_after_the_provider_rejected_the_credential()
    {
        WriteCodexLogin(Jwt(_clock.GetUtcNow().AddDays(9)), _clock.GetUtcNow().AddDays(-1));
        var provider = Provider("codex");
        await _source.ResolveAsync(provider, GatewayRoutes.ChatGpt, TestContext.Current.CancellationToken);
        Assert.Empty(_handler.Requests);

        _source.Rejected(provider, GatewayRoutes.ChatGpt);
        _handler.Reply = """{"access_token":"renewed"}""";

        Assert.Equal("renewed", (await _source.ResolveAsync(provider, GatewayRoutes.ChatGpt, TestContext.Current.CancellationToken))!.Secret);
        Assert.Single(_handler.Requests);
        await _source.ResolveAsync(provider, GatewayRoutes.ChatGpt, TestContext.Current.CancellationToken);
        Assert.Single(_handler.Requests);
    }

    [Fact]
    public async Task A_refused_codex_refresh_keeps_the_stored_login_and_does_not_touch_the_file()
    {
        var path = WriteCodexLogin(Jwt(_clock.GetUtcNow().AddDays(9)), _clock.GetUtcNow().AddDays(-9));
        var before = File.ReadAllText(path);
        _handler.Status = HttpStatusCode.BadRequest;

        var credential = await _source.ResolveAsync(Provider("codex"), GatewayRoutes.ChatGpt, TestContext.Current.CancellationToken);

        Assert.NotNull(credential);
        Assert.Equal(before, File.ReadAllText(path));
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 6, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<(string Uri, string Body)> Requests { get; } = [];
        public string Reply { get; set; } = "{}";
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.RequestUri!.ToString(), await request.Content!.ReadAsStringAsync(cancellationToken)));
            return new HttpResponseMessage(Status) { Content = new StringContent(Reply, Encoding.UTF8, "application/json") };
        }
    }
}
