using System.Net;
using System.Text;
using ILD.Core.Services.Implementations;
using ILD.Core.Services.Implementations.RemoteProviders;
using ILD.Core.Services.Interfaces;
using ILD.Data.Entities;

namespace ILD.Tests;

/// <summary>
/// The API key never reaches a result in any form it went on the wire as, even
/// when the forge or git server echoes it back and the evidence is long enough
/// to be cut.
/// </summary>
public class ConnectionTesterRedactionTests
{
    private const string Key = "sekrit-KEY-9f3a";

    private sealed class EchoHandler(HttpStatusCode status, Func<HttpRequestMessage, string> body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body(request), Encoding.UTF8, "application/json"),
            });
    }

    private sealed class StderrRunner(Func<IReadOnlyDictionary<string, string?>, string> stderr) : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> args, string? workingDirectory = null, CancellationToken ct = default, IReadOnlyDictionary<string, string?>? environmentVariables = null)
            => Task.FromResult(new ProcessResult(128, string.Empty, stderr(environmentVariables ?? new Dictionary<string, string?>())));
    }

    private static IRemoteGitProviderAdapter[] Adapters() =>
    [
        new ForgejoRemoteGitProviderAdapter(),
        new GitHubRemoteGitProviderAdapter(),
        new AzureDevOpsRemoteGitProviderAdapter(),
    ];

    private static RemoteProvider Provider(string type, string url) => new()
    {
        Id = Guid.NewGuid(),
        Name = "p",
        Type = type,
        Url = url,
        ApiKey = Key,
    };

    private static string Text(ConnectionTestResult result) => $"{result.Message}\n{result.Detail}";

    [Theory]
    [InlineData(970)]
    [InlineData(975)]
    [InlineData(977)]
    public async Task A_key_echoed_across_the_detail_cap_is_masked_before_the_cut(int padding)
    {
        var handler = new EchoHandler(HttpStatusCode.Forbidden, _ => new string('x', padding) + Key + new string('y', 200));
        var tester = new ConnectionTester(Adapters(), new RepositoryManager(), new HttpClient(handler));

        var result = await tester.TestRemoteProviderAsync(Provider("GitHub", "https://github.com"), CancellationToken.None);

        Assert.Equal(ConnectionTestOutcome.AccessDenied, result.Outcome);
        Assert.InRange(result.Detail!.Length, 1, ConnectionTestResult.MaxDetailLength);
        Assert.DoesNotContain("sek", result.Detail);
    }

    [Fact]
    public async Task An_azure_devops_forge_echoing_the_encoded_credential_does_not_expose_it()
    {
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes($":{Key}"));
        var handler = new EchoHandler(HttpStatusCode.Unauthorized, request => $"{{\"message\":\"rejected {request.Headers.Authorization}\"}}");
        var tester = new ConnectionTester(Adapters(), new RepositoryManager(), new HttpClient(handler));

        var result = await tester.TestRemoteProviderAsync(Provider("AzureDevOps", "https://dev.azure.com/org"), CancellationToken.None);

        Assert.Equal(ConnectionTestOutcome.InvalidApiKey, result.Outcome);
        Assert.Contains("rejected Basic", result.Detail);
        Assert.DoesNotContain(encoded, Text(result));
    }

    [Theory]
    [InlineData("GitHub", "x-access-token")]
    [InlineData("AzureDevOps", "pat")]
    public async Task A_git_server_echoing_gits_basic_credential_does_not_expose_it(string type, string username)
    {
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{Key}"));
        var runner = new StderrRunner(env =>
        {
            Assert.Equal(username, env["ILD_GIT_USERNAME"]);
            return $"remote: Authorization: Basic {encoded}\nfatal: Authentication failed for 'https://git.example.com/team/app.git/'";
        });
        var tester = new ConnectionTester(Adapters(), new RepositoryManager(runner), new HttpClient());
        var provider = Provider(type, "https://git.example.com");
        var repo = new Repository
        {
            Id = Guid.NewGuid(),
            Name = "app",
            CloneUrl = "https://git.example.com/team/app.git",
            DefaultBranch = "main",
            RemoteProviderId = provider.Id,
        };

        var result = await tester.TestRepositoryAsync(repo, provider, CancellationToken.None);

        Assert.Equal(ConnectionTestOutcome.InvalidApiKey, result.Outcome);
        Assert.Contains("remote: Authorization: Basic", result.Detail);
        Assert.DoesNotContain(encoded, Text(result));
    }
}
