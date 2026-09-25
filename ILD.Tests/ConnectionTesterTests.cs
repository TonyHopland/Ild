using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using ILD.Core.Services.Implementations;
using ILD.Core.Services.Implementations.RemoteProviders;
using ILD.Core.Services.Interfaces;
using ILD.Data.Entities;
using Microsoft.Extensions.Logging;

namespace ILD.Tests;

[Collection("Git")]
public class ConnectionTesterTests : IDisposable
{
    private const string Key = "sekrit-KEY-9f3a";

    private readonly string _tmp;

    public ConnectionTesterTests()
    {
        _tmp = Path.Combine(Path.GetTempPath(), "ild-conntest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmp);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmp, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    // ── Doubles ───────────────────────────────────────────────────────────

    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _respond;
        public List<(HttpMethod Method, Uri Uri, string? Authorization)> Requests { get; } = new();

        public ScriptedHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) => _respond = respond;

        public static ScriptedHandler Answer(HttpStatusCode status, string body = "")
            => new((_, _) => Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            }));

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.Method, request.RequestUri!, request.Headers.Authorization?.ToString()));
            return _respond(request, cancellationToken);
        }
    }

    private sealed class ScriptedRunner : IProcessRunner
    {
        private readonly Func<CancellationToken, Task<ProcessResult>> _respond;
        public List<(IReadOnlyList<string> Args, string? WorkingDirectory, IReadOnlyDictionary<string, string?> Environment, CancellationToken Token)> Calls { get; } = new();

        public ScriptedRunner(Func<CancellationToken, Task<ProcessResult>> respond) => _respond = respond;

        public static ScriptedRunner Exit(int code, string stderr = "")
            => new(_ => Task.FromResult(new ProcessResult(code, string.Empty, stderr)));

        public Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> args, string? workingDirectory = null, CancellationToken ct = default, IReadOnlyDictionary<string, string?>? environmentVariables = null)
        {
            Calls.Add((args, workingDirectory, new Dictionary<string, string?>(environmentVariables ?? new Dictionary<string, string?>()), ct));
            return _respond(ct);
        }
    }

    private sealed class RecordingLogger : ILogger<ConnectionTester>
    {
        public List<string> Lines { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Lines.Add(formatter(state, exception));
            if (state is IEnumerable<KeyValuePair<string, object?>> values)
                Lines.AddRange(values.Select(v => $"{v.Key}={v.Value}"));
            if (exception is not null)
                Lines.Add(exception.ToString());
        }
    }

    private static IRemoteGitProviderAdapter[] Adapters() =>
    [
        new ForgejoRemoteGitProviderAdapter(),
        new GitHubRemoteGitProviderAdapter(),
        new AzureDevOpsRemoteGitProviderAdapter(),
    ];

    private static ConnectionTester ProviderTester(HttpMessageHandler handler, ILogger<ConnectionTester>? logger = null)
        => new(Adapters(), new RepositoryManager(ScriptedRunner.Exit(0)), new HttpClient(handler), logger);

    private static ConnectionTester RepoTester(IProcessRunner runner, ILogger<ConnectionTester>? logger = null)
        => new(Adapters(), new RepositoryManager(runner), new HttpClient(ScriptedHandler.Answer(HttpStatusCode.OK)), logger);

    private static RemoteProvider Provider(string type, string url, string? apiKey) => new()
    {
        Id = Guid.NewGuid(),
        Name = "p",
        Type = type,
        Url = url,
        ApiKey = apiKey,
    };

    private static Repository Repo(string cloneUrl, string? defaultBranch = "main", Guid? providerId = null) => new()
    {
        Id = Guid.NewGuid(),
        Name = "app",
        CloneUrl = cloneUrl,
        DefaultBranch = defaultBranch,
        RemoteProviderId = providerId ?? Guid.NewGuid(),
    };

    private static void AssertOutcome(ConnectionTestOutcome expected, ConnectionTestResult result)
    {
        Assert.Equal(expected, result.Outcome);
        Assert.Equal(expected == ConnectionTestOutcome.Ok, result.Ok);
        Assert.False(string.IsNullOrWhiteSpace(result.Message));
    }

    private static string Text(ConnectionTestResult result) => $"{result.Message}\n{result.Detail}";

    /// <summary>
    /// A caller that gave up (the browser request was cancelled) must not be
    /// reported as the server timing out. Rethrowing the cancellation and answering
    /// with anything that is not a timeout both satisfy that.
    /// </summary>
    private static async Task AssertNotReportedAsTimeout(Func<Task<ConnectionTestResult>> run)
    {
        ConnectionTestResult result;
        try
        {
            result = await run();
        }
        catch (OperationCanceledException)
        {
            return;
        }
        Assert.NotEqual(ConnectionTestOutcome.Unreachable, result.Outcome);
        Assert.DoesNotContain("timed out", Text(result), StringComparison.OrdinalIgnoreCase);
    }

    // ── Remote provider ───────────────────────────────────────────────────

    public static TheoryData<string, string, string, string, string, string> WhoAmICases => new()
    {
        {
            "GitHub", "https://github.com",
            "https://api.github.com/user",
            "Bearer " + Key,
            "{\"login\":\"octo-cat\",\"id\":1}",
            "octo-cat"
        },
        {
            "Forgejo", "https://git.example.com/",
            "https://git.example.com/api/v1/user",
            "token " + Key,
            "{\"id\":3,\"login\":\"forge-bot\",\"full_name\":\"Forge Bot\"}",
            "forge-bot"
        },
        {
            "AzureDevOps", "https://dev.azure.com/contoso/",
            "https://dev.azure.com/contoso/_apis/connectionData?api-version=7.1",
            "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(":" + Key)),
            "{\"authenticatedUser\":{\"id\":\"8c1b\",\"descriptor\":\"x\",\"providerDisplayName\":\"Ada Lovelace\"},\"authorizedUser\":{\"id\":\"8c1b\"}}",
            "Ada Lovelace"
        },
    };

    [Theory]
    [MemberData(nameof(WhoAmICases))]
    public async Task Provider_test_asks_who_am_i_with_the_adapters_own_auth_and_names_the_account(
        string type, string url, string expectedUri, string expectedAuthorization, string body, string account)
    {
        var handler = ScriptedHandler.Answer(HttpStatusCode.OK, body);

        var result = await ProviderTester(handler).TestRemoteProviderAsync(Provider(type, url, Key), CancellationToken.None);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal(expectedUri, request.Uri.ToString());
        Assert.Equal(expectedAuthorization, request.Authorization);
        AssertOutcome(ConnectionTestOutcome.Ok, result);
        Assert.Contains(account, result.Message);
    }

    [Theory]
    [InlineData("GitHub", "https://github.com", HttpStatusCode.OK, "{\"login\":\"someone\"}", "github.com")]
    [InlineData("Forgejo", "https://git.example.com", HttpStatusCode.Unauthorized, "{\"message\":\"token is required\"}", "git.example.com")]
    [InlineData("GitHub", "https://github.com", HttpStatusCode.Forbidden, "{\"message\":\"Forbidden\"}", "github.com")]
    [InlineData("AzureDevOps", "https://dev.azure.com/contoso", HttpStatusCode.NonAuthoritativeInformation, "<html>Sign in</html>", "dev.azure.com")]
    [InlineData("AzureDevOps", "https://dev.azure.com/contoso", HttpStatusCode.Unauthorized, "", "dev.azure.com")]
    public async Task Provider_without_a_key_still_asks_and_reports_the_key_missing(
        string type, string url, HttpStatusCode status, string body, string host)
    {
        var handler = ScriptedHandler.Answer(status, body);

        var result = await ProviderTester(handler).TestRemoteProviderAsync(Provider(type, url, null), CancellationToken.None);

        var request = Assert.Single(handler.Requests);
        Assert.Null(request.Authorization);
        AssertOutcome(ConnectionTestOutcome.MissingApiKey, result);
        Assert.Contains(host, result.Message);
    }

    [Theory]
    [InlineData("GitHub", "https://github.com", HttpStatusCode.Unauthorized, "{\"message\":\"Bad credentials\"}", ConnectionTestOutcome.InvalidApiKey)]
    [InlineData("Forgejo", "https://git.example.com", HttpStatusCode.Unauthorized, "{\"message\":\"user does not exist\"}", ConnectionTestOutcome.InvalidApiKey)]
    [InlineData("AzureDevOps", "https://dev.azure.com/contoso", HttpStatusCode.NonAuthoritativeInformation, "<html><title>Azure DevOps Services | Sign In</title></html>", ConnectionTestOutcome.InvalidApiKey)]
    [InlineData("AzureDevOps", "https://dev.azure.com/contoso", HttpStatusCode.Unauthorized, "", ConnectionTestOutcome.InvalidApiKey)]
    [InlineData("Forgejo", "https://git.example.com", HttpStatusCode.Forbidden, "{\"message\":\"token does not have at least one of required scope(s): [read:user]\"}", ConnectionTestOutcome.AccessDenied)]
    [InlineData("GitHub", "https://github.com", HttpStatusCode.NotFound, "{\"message\":\"Not Found\"}", ConnectionTestOutcome.NotFound)]
    [InlineData("Forgejo", "https://git.example.com", HttpStatusCode.NotFound, "404 page not found", ConnectionTestOutcome.NotFound)]
    [InlineData("GitHub", "https://github.com", HttpStatusCode.InternalServerError, "oops", ConnectionTestOutcome.Error)]
    [InlineData("GitHub", "https://github.com", HttpStatusCode.OK, "{}", ConnectionTestOutcome.Error)]
    [InlineData("Forgejo", "https://git.example.com", HttpStatusCode.OK, "<html>a login portal</html>", ConnectionTestOutcome.Error)]
    [InlineData("AzureDevOps", "https://dev.azure.com/contoso", HttpStatusCode.OK, "{\"authenticatedUser\":{\"id\":\"8c1b\"}}", ConnectionTestOutcome.Error)]
    public async Task Provider_with_a_key_classifies_the_forges_answer(
        string type, string url, HttpStatusCode status, string body, ConnectionTestOutcome expected)
    {
        var result = await ProviderTester(ScriptedHandler.Answer(status, body))
            .TestRemoteProviderAsync(Provider(type, url, Key), CancellationToken.None);

        AssertOutcome(expected, result);
    }

    [Fact]
    public async Task Provider_access_denied_carries_the_forges_own_explanation()
    {
        const string forgeText = "Resource protected by organization SAML enforcement. You must grant your Personal Access token access to this organization.";
        var handler = ScriptedHandler.Answer(HttpStatusCode.Forbidden, "{\"message\":\"" + forgeText + "\"}");

        var result = await ProviderTester(handler).TestRemoteProviderAsync(Provider("GitHub", "https://github.com", Key), CancellationToken.None);

        AssertOutcome(ConnectionTestOutcome.AccessDenied, result);
        Assert.Contains(forgeText, result.Detail);
    }

    [Fact]
    public async Task Provider_not_found_names_the_provider_type()
    {
        var result = await ProviderTester(ScriptedHandler.Answer(HttpStatusCode.NotFound, "404 page not found"))
            .TestRemoteProviderAsync(Provider("Forgejo", "https://git.example.com/wrong", Key), CancellationToken.None);

        AssertOutcome(ConnectionTestOutcome.NotFound, result);
        Assert.Contains("Forgejo", result.Message);
    }

    [Fact]
    public async Task Provider_unexpected_status_puts_the_status_code_in_the_detail()
    {
        var result = await ProviderTester(ScriptedHandler.Answer(HttpStatusCode.BadGateway, "upstream down"))
            .TestRemoteProviderAsync(Provider("GitHub", "https://github.com", Key), CancellationToken.None);

        AssertOutcome(ConnectionTestOutcome.Error, result);
        Assert.Contains("502", result.Detail);
    }

    [Fact]
    public async Task Provider_detail_is_trimmed_and_capped()
    {
        var body = "  \n" + new string('x', 5000) + "\n  ";

        var result = await ProviderTester(ScriptedHandler.Answer(HttpStatusCode.Forbidden, body))
            .TestRemoteProviderAsync(Provider("GitHub", "https://github.com", Key), CancellationToken.None);

        Assert.NotNull(result.Detail);
        Assert.InRange(result.Detail!.Length, 1, 1000);
        Assert.Equal(result.Detail, result.Detail.Trim());
    }

    [Theory]
    [InlineData("Forgejo", "not a url")]
    [InlineData("GitHub", "")]
    [InlineData("Forgejo", "/relative/path")]
    [InlineData("GitLab", "https://gitlab.com")]
    public async Task Provider_that_cannot_be_asked_is_misconfigured_without_a_network_call(string type, string url)
    {
        var handler = ScriptedHandler.Answer(HttpStatusCode.OK, "{\"login\":\"x\"}");

        var result = await ProviderTester(handler).TestRemoteProviderAsync(Provider(type, url, Key), CancellationToken.None);

        AssertOutcome(ConnectionTestOutcome.Misconfigured, result);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Provider_refusing_connections_is_unreachable_with_the_reason()
    {
        // A real refused connection on loopback, through the real socket handler.
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        var tester = new ConnectionTester(Adapters(), new RepositoryManager(ScriptedRunner.Exit(0)), new HttpClient());

        var result = await tester.TestRemoteProviderAsync(Provider("Forgejo", $"http://127.0.0.1:{port}", Key), CancellationToken.None);

        AssertOutcome(ConnectionTestOutcome.Unreachable, result);
        Assert.Contains("refused", result.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Provider_that_never_answers_is_unreachable_after_the_bounded_timeout()
    {
        var handler = new ScriptedHandler(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            throw new InvalidOperationException("unreachable");
        });
        var tester = ProviderTester(handler);
        tester.ProviderTimeout = TimeSpan.FromMilliseconds(1);

        var result = await tester.TestRemoteProviderAsync(Provider("GitHub", "https://github.com", Key), CancellationToken.None);

        AssertOutcome(ConnectionTestOutcome.Unreachable, result);
        Assert.Contains("timed out", Text(result), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Provider_timeout_is_bounded_to_at_most_twenty_seconds()
    {
        var tester = ProviderTester(ScriptedHandler.Answer(HttpStatusCode.OK));

        Assert.InRange(tester.ProviderTimeout, TimeSpan.FromMilliseconds(1), TimeSpan.FromSeconds(20));
    }

    [Fact]
    public async Task Provider_test_cancelled_by_the_caller_is_not_reported_as_a_timeout()
    {
        using var caller = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new ScriptedHandler(async (_, ct) =>
        {
            started.SetResult();
            await Task.Delay(Timeout.Infinite, ct);
            throw new InvalidOperationException("unreachable");
        });
        var tester = ProviderTester(handler);

        var run = tester.TestRemoteProviderAsync(Provider("GitHub", "https://github.com", Key), caller.Token);
        await started.Task;
        caller.Cancel();

        await AssertNotReportedAsTimeout(() => run);
    }

    [Theory]
    [InlineData(HttpStatusCode.OK)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task Provider_key_never_appears_in_the_result_or_the_log(HttpStatusCode status)
    {
        var logger = new RecordingLogger();
        var handler = ScriptedHandler.Answer(status, "{\"login\":\"octo-cat\",\"message\":\"nope\"}");

        var result = await ProviderTester(handler, logger)
            .TestRemoteProviderAsync(Provider("GitHub", "https://github.com", Key), CancellationToken.None);

        Assert.DoesNotContain(Key, Text(result));
        Assert.DoesNotContain(logger.Lines, line => line.Contains(Key));
    }

    // ── Repository: against real git ──────────────────────────────────────

    /// <summary>A local repository whose only branch (and HEAD) is <paramref name="branch"/>.</summary>
    private string OriginWithBranch(string branch)
    {
        var origin = Path.Combine(_tmp, "origin-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(origin);
        Git(origin, "init", "-b", branch);
        File.WriteAllText(Path.Combine(origin, "README.md"), "hi\n");
        Git(origin, "add", "-A");
        Git(origin, "-c", "user.email=t@t.io", "-c", "user.name=Tester", "commit", "-m", "init");
        return origin;
    }

    private static ConnectionTester RealGitTester()
        => new(Adapters(), new RepositoryManager(), new HttpClient(ScriptedHandler.Answer(HttpStatusCode.OK)));

    [Fact]
    public async Task Repository_whose_default_branch_exists_is_ok_and_names_it()
    {
        var origin = OriginWithBranch("main");

        var result = await RealGitTester().TestRepositoryAsync(Repo(origin, "main"), null, CancellationToken.None);

        AssertOutcome(ConnectionTestOutcome.Ok, result);
        Assert.Contains("main", result.Message);
    }

    [Fact]
    public async Task Repository_whose_default_branch_is_absent_reports_branch_missing_naming_it()
    {
        var origin = OriginWithBranch("main");

        var result = await RealGitTester().TestRepositoryAsync(Repo(origin, "release/2"), null, CancellationToken.None);

        AssertOutcome(ConnectionTestOutcome.BranchMissing, result);
        Assert.Contains("release/2", result.Message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Repository_without_a_default_branch_asks_for_the_remotes_head(string? defaultBranch)
    {
        // "trunk" is nobody's guess at a default, so only asking for HEAD finds it.
        var origin = OriginWithBranch("trunk");

        var result = await RealGitTester().TestRepositoryAsync(Repo(origin, defaultBranch), null, CancellationToken.None);

        AssertOutcome(ConnectionTestOutcome.Ok, result);
    }

    [Fact]
    public async Task Repository_that_does_not_exist_is_not_found_with_gits_stderr()
    {
        var missing = Path.Combine(_tmp, "no-such-repo");

        var result = await RealGitTester().TestRepositoryAsync(Repo(missing), null, CancellationToken.None);

        AssertOutcome(ConnectionTestOutcome.NotFound, result);
        Assert.Contains("does not appear to be a git repository", result.Detail);
    }

    // ── Repository: stderr classification ─────────────────────────────────

    // The first strings in each group are git 2.43's own output against a loopback
    // HTTP server answering 401/403/404, a closed port, and an HTTPS proxy refusing
    // the tunnel; the rest are the forges' "remote:" lines the criteria name.
    [Theory]
    [InlineData("fatal: could not read Username for 'https://git.example.com': terminal prompts disabled", false, ConnectionTestOutcome.MissingApiKey)]
    [InlineData("fatal: Authentication failed for 'https://git.example.com/team/app.git/'", false, ConnectionTestOutcome.MissingApiKey)]
    [InlineData("fatal: Authentication failed for 'https://git.example.com/team/app.git/'", true, ConnectionTestOutcome.InvalidApiKey)]
    [InlineData("FATAL: AUTHENTICATION FAILED FOR 'https://git.example.com/team/app.git/'", true, ConnectionTestOutcome.InvalidApiKey)]
    [InlineData("fatal: could not read Password for 'https://git@git.example.com': terminal prompts disabled", true, ConnectionTestOutcome.InvalidApiKey)]
    [InlineData("remote: Invalid username or password.\nfatal: Authentication failed for 'https://github.com/team/app.git/'", true, ConnectionTestOutcome.InvalidApiKey)]
    [InlineData("remote: Invalid username or token. Password authentication is not supported for Git operations.", true, ConnectionTestOutcome.InvalidApiKey)]
    [InlineData("remote: HTTP Basic: Access denied", true, ConnectionTestOutcome.InvalidApiKey)]
    [InlineData("fatal: unable to access 'https://git.example.com/team/app.git/': The requested URL returned error: 401", true, ConnectionTestOutcome.InvalidApiKey)]
    [InlineData("git@git.example.com: Permission denied (publickey).\nfatal: Could not read from remote repository.", false, ConnectionTestOutcome.MissingApiKey)]
    [InlineData("remote: Repository not found.\nfatal: Authentication failed for 'https://github.com/team/app.git/'", true, ConnectionTestOutcome.InvalidApiKey)]
    [InlineData("fatal: unable to access 'https://git.example.com/team/app.git/': The requested URL returned error: 403", true, ConnectionTestOutcome.AccessDenied)]
    [InlineData("remote: not found\nfatal: unable to access 'https://git.example.com/team/app.git/': The requested URL returned error: 403", true, ConnectionTestOutcome.AccessDenied)]
    [InlineData("fatal: repository 'https://git.example.com/team/app.git/' not found", true, ConnectionTestOutcome.NotFound)]
    [InlineData("remote: Repository not found.\nfatal: repository 'https://github.com/team/app.git/' not found", false, ConnectionTestOutcome.NotFound)]
    [InlineData("fatal: '/srv/git/app' does not appear to be a git repository\nfatal: Could not read from remote repository.", true, ConnectionTestOutcome.NotFound)]
    [InlineData("fatal: unable to access 'https://git.example.com/team/app.git/': The requested URL returned error: 404", true, ConnectionTestOutcome.NotFound)]
    [InlineData("fatal: repository 'https://git.example.com/team/app.git/' not found\nerror: Connection timed out", true, ConnectionTestOutcome.NotFound)]
    [InlineData("fatal: unable to access 'https://git.example.com/team/app.git/': Could not resolve host: git.example.com", true, ConnectionTestOutcome.Unreachable)]
    [InlineData("fatal: unable to access 'http://127.0.0.1:1/x.git/': Failed to connect to 127.0.0.1 port 1 after 0 ms: Couldn't connect to server", true, ConnectionTestOutcome.Unreachable)]
    [InlineData("ssh: connect to host git.example.com port 22: Connection refused", false, ConnectionTestOutcome.Unreachable)]
    [InlineData("ssh: connect to host git.example.com port 22: Connection timed out", false, ConnectionTestOutcome.Unreachable)]
    [InlineData("fatal: unable to access 'https://git.example.com/team/app.git/': Operation timed out after 300000 milliseconds with 0 out of 0 bytes received", true, ConnectionTestOutcome.Unreachable)]
    [InlineData("ssh: connect to host git.example.com port 22: Network is unreachable", false, ConnectionTestOutcome.Unreachable)]
    [InlineData("fatal: unable to access 'https://git.example.com/team/app.git/': SSL certificate problem: self-signed certificate", true, ConnectionTestOutcome.Unreachable)]
    [InlineData("fatal: unable to access 'https://git.example.com/team/app.git/': server certificate verification failed. CAfile: none CRLfile: none", true, ConnectionTestOutcome.Unreachable)]
    [InlineData("fatal: unable to access 'https://git.example.com/team/app.git/': CONNECT tunnel failed, response 502", true, ConnectionTestOutcome.Error)]
    [InlineData("error: something unforeseen happened", false, ConnectionTestOutcome.Error)]
    public async Task Repository_git_failure_is_classified_from_stderr_and_carries_it_as_detail(
        string stderr, bool hasKey, ConnectionTestOutcome expected)
    {
        var provider = Provider("Forgejo", "https://git.example.com", hasKey ? Key : null);
        var runner = ScriptedRunner.Exit(128, stderr + "\n");

        var result = await RepoTester(runner).TestRepositoryAsync(
            Repo("https://git.example.com/team/app.git", "main", provider.Id), provider, CancellationToken.None);

        AssertOutcome(expected, result);
        Assert.Contains(stderr.Trim(), result.Detail);
    }

    [Fact]
    public async Task Repository_detail_is_capped()
    {
        var runner = ScriptedRunner.Exit(128, "fatal: " + new string('x', 5000));

        var result = await RepoTester(runner).TestRepositoryAsync(
            Repo("https://git.example.com/team/app.git"), null, CancellationToken.None);

        Assert.NotNull(result.Detail);
        Assert.InRange(result.Detail!.Length, 1, 1000);
    }

    [Fact]
    public async Task Repository_without_a_remote_provider_probes_uncredentialed_and_says_so_on_auth_failure()
    {
        var runner = ScriptedRunner.Exit(128, "fatal: could not read Username for 'https://git.example.com': terminal prompts disabled");

        var result = await RepoTester(runner).TestRepositoryAsync(
            Repo("https://git.example.com/team/app.git"), null, CancellationToken.None);

        AssertOutcome(ConnectionTestOutcome.MissingApiKey, result);
        Assert.Contains("remote provider", result.Message, StringComparison.OrdinalIgnoreCase);
        var call = Assert.Single(runner.Calls);
        Assert.False(call.Environment.ContainsKey("ILD_GIT_PASSWORD"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Repository_with_a_blank_clone_url_is_misconfigured_without_running_git(string cloneUrl)
    {
        var runner = ScriptedRunner.Exit(0);

        var result = await RepoTester(runner).TestRepositoryAsync(Repo(cloneUrl), null, CancellationToken.None);

        AssertOutcome(ConnectionTestOutcome.Misconfigured, result);
        Assert.Empty(runner.Calls);
    }

    // ── Repository: how git is run ─────────────────────────────────────────

    [Fact]
    public async Task Repository_probe_authenticates_exactly_as_fetch_does_and_never_prompts()
    {
        // The user part of the clone URL feeds the git username, so a probe built
        // from anything but the clone URL would authenticate differently from fetch.
        const string cloneUrl = "https://ci-bot@git.example.com/team/app.git";
        var provider = Provider("Forgejo", "https://git.example.com", Key);
        var runner = ScriptedRunner.Exit(0);
        var mgr = new RepositoryManager(runner);
        var tester = new ConnectionTester(Adapters(), mgr, new HttpClient(ScriptedHandler.Answer(HttpStatusCode.OK)));

        await tester.TestRepositoryAsync(Repo(cloneUrl, "main", provider.Id), provider, CancellationToken.None);
        await mgr.FetchAsync(_tmp, auth: new GitAuthOptions(cloneUrl, Key, "Forgejo"));

        var probe = runner.Calls[0];
        var fetch = runner.Calls[1];
        Assert.Contains(cloneUrl, probe.Args);
        Assert.Equal(Key, fetch.Environment["ILD_GIT_PASSWORD"]);
        foreach (var (name, value) in fetch.Environment)
            Assert.Equal(value, probe.Environment.GetValueOrDefault(name));
        Assert.Equal("0", probe.Environment.GetValueOrDefault("GIT_TERMINAL_PROMPT"));
    }

    [Fact]
    public async Task Repository_probe_without_a_key_still_never_prompts()
    {
        var provider = Provider("Forgejo", "https://git.example.com", null);
        var runner = ScriptedRunner.Exit(0);

        await RepoTester(runner).TestRepositoryAsync(
            Repo("https://git.example.com/team/app.git", "main", provider.Id), provider, CancellationToken.None);

        var call = Assert.Single(runner.Calls);
        Assert.Equal("0", call.Environment.GetValueOrDefault("GIT_TERMINAL_PROMPT"));
        Assert.False(call.Environment.ContainsKey("ILD_GIT_PASSWORD"));
    }

    [Fact]
    public async Task Repository_probe_runs_where_no_other_user_can_plant_git_config()
    {
        var runner = ScriptedRunner.Exit(0);

        await RepoTester(runner).TestRepositoryAsync(Repo("https://git.example.com/team/app.git"), null, CancellationToken.None);

        var call = Assert.Single(runner.Calls);
        var cwd = Path.GetFullPath(call.WorkingDirectory!).TrimEnd('/');
        Assert.True(Directory.Exists(cwd));
        if (OperatingSystem.IsLinux())
            Assert.False(OthersCanWriteInto(cwd), $"{cwd} is writable by another user");

        // Discovery must stop at the private directory: a ceiling at the cwd itself
        // or its parent keeps git from ever reading a .git above it.
        var ceilings = (call.Environment.GetValueOrDefault("GIT_CEILING_DIRECTORIES") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(c => Path.GetFullPath(c).TrimEnd('/'))
            .ToList();
        var parent = Path.GetDirectoryName(cwd)!.TrimEnd('/');
        Assert.Contains(ceilings, c => c == cwd || c == parent);
    }

    [Fact]
    public async Task Repository_probe_that_outlives_the_bounded_timeout_is_unreachable_and_killed()
    {
        var runner = new ScriptedRunner(async ct =>
        {
            // ProcessRunner's contract: kill the tree, then surface the cancellation.
            await Task.Delay(Timeout.Infinite, ct);
            throw new InvalidOperationException("unreachable");
        });
        var tester = RepoTester(runner);
        tester.RepositoryTimeout = TimeSpan.FromMilliseconds(1);

        var result = await tester.TestRepositoryAsync(Repo("https://git.example.com/team/app.git"), null, CancellationToken.None);

        AssertOutcome(ConnectionTestOutcome.Unreachable, result);
        Assert.Contains("timed out", Text(result), StringComparison.OrdinalIgnoreCase);
        Assert.True(Assert.Single(runner.Calls).Token.IsCancellationRequested);
    }

    [Fact]
    public void Repository_timeout_is_bounded_to_at_most_sixty_seconds()
    {
        var tester = RepoTester(ScriptedRunner.Exit(0));

        Assert.InRange(tester.RepositoryTimeout, TimeSpan.FromMilliseconds(1), TimeSpan.FromSeconds(60));
    }

    [Fact]
    public async Task Repository_test_cancelled_by_the_caller_is_not_reported_as_a_timeout()
    {
        using var caller = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new ScriptedRunner(async ct =>
        {
            started.SetResult();
            await Task.Delay(Timeout.Infinite, ct);
            throw new InvalidOperationException("unreachable");
        });
        var tester = RepoTester(runner);

        var run = tester.TestRepositoryAsync(Repo("https://git.example.com/team/app.git"), null, caller.Token);
        await started.Task;
        caller.Cancel();

        await AssertNotReportedAsTimeout(() => run);
    }

    [Fact]
    public async Task Repository_key_never_appears_in_the_result_or_the_log()
    {
        var logger = new RecordingLogger();
        var provider = Provider("GitHub", "https://github.com", Key);
        var runner = ScriptedRunner.Exit(128, "fatal: Authentication failed for 'https://github.com/team/app.git/'");

        var result = await RepoTester(runner, logger).TestRepositoryAsync(
            Repo("https://github.com/team/app.git", "main", provider.Id), provider, CancellationToken.None);

        AssertOutcome(ConnectionTestOutcome.InvalidApiKey, result);
        Assert.DoesNotContain(Key, Text(result));
        Assert.DoesNotContain(logger.Lines, line => line.Contains(Key));
    }

    /// <summary>
    /// Whether a user other than the owner can create entries in
    /// <paramref name="dir"/>: it grants group or other write, and every ancestor
    /// lets group or other traverse to it.
    /// </summary>
    private static bool OthersCanWriteInto(string dir)
    {
        const UnixFileMode othersWrite = UnixFileMode.GroupWrite | UnixFileMode.OtherWrite;
        const UnixFileMode othersTraverse = UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
        if ((File.GetUnixFileMode(dir) & othersWrite) == 0)
            return false;
        for (var up = Path.GetDirectoryName(dir); up is not null; up = Path.GetDirectoryName(up))
        {
            if ((File.GetUnixFileMode(up) & othersTraverse) == 0)
                return false;
        }
        return true;
    }

    private static void Git(string cwd, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = cwd, UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        p.StandardOutput.ReadToEnd();
        var err = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0) throw new InvalidOperationException($"git {string.Join(' ', args)}: {err}");
    }
}
