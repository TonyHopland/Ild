using System.Net;
using System.Net.Sockets;
using System.Text;
using ILD.Core.Services.Implementations;
using ILD.Core.Services.Implementations.RemoteProviders;
using ILD.Core.Services.Interfaces;
using ILD.Data.Entities;

namespace ILD.Tests;

/// <summary>
/// A provider test answers with a result, never an exception, even when what it
/// sends or receives is malformed in a way only the real socket stack rejects.
/// </summary>
public class ConnectionTesterMalformedInputTests
{
    private static ConnectionTester Tester(HttpClient http) => new(
        [new ForgejoRemoteGitProviderAdapter(), new GitHubRemoteGitProviderAdapter(), new AzureDevOpsRemoteGitProviderAdapter()],
        new RepositoryManager(),
        http);

    private static RemoteProvider Provider(string type, string url, string apiKey) => new()
    {
        Id = Guid.NewGuid(),
        Name = "p",
        Type = type,
        Url = url,
        ApiKey = apiKey,
    };

    /// <summary>A loopback forge that answers every request with <paramref name="response"/>.</summary>
    private static (int Port, TcpListener Listener) Serve(string response)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        _ = Task.Run(async () =>
        {
            while (true)
            {
                using var client = await listener.AcceptTcpClientAsync();
                var stream = client.GetStream();
                await stream.ReadAsync(new byte[8192]);
                await stream.WriteAsync(Encoding.ASCII.GetBytes(response));
            }
        });
        return (((IPEndPoint)listener.LocalEndpoint).Port, listener);
    }

    private static string Http(int status, string contentType, string body)
        => $"HTTP/1.1 {status} X\r\nContent-Type: {contentType}\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n{body}";

    [Theory]
    [InlineData("GitHub", "sekrit-KEY-9f3a\n")]
    [InlineData("GitHub", "sekrit-KEY-9f3a\r\n")]
    [InlineData("Forgejo", "sekrit-KEY-9f3a\n")]
    [InlineData("Forgejo", "sekrit\0KEY-9f3a")]
    [InlineData("GitHub", "sekrit KEY-9f3a")]
    [InlineData("Forgejo", "sekrit KEY-9f3a")]
    [InlineData("GitHub", "“sekrit-KEY-9f3a”")]
    [InlineData("Forgejo", "sékrit-KEY-9f3a")]
    public async Task A_key_that_cannot_be_sent_in_a_header_is_misconfigured_without_the_key(string type, string key)
    {
        var (port, listener) = Serve(Http(200, "application/json", "{\"login\":\"someone\"}"));
        try
        {
            var result = await Tester(new HttpClient()).TestRemoteProviderAsync(
                Provider(type, $"http://127.0.0.1:{port}", key), CancellationToken.None);

            Assert.Equal(ConnectionTestOutcome.Misconfigured, result.Outcome);
            Assert.Contains("API key", result.Message);
            Assert.DoesNotContain("krit", $"{result.Message}\n{result.Detail}");
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task Azure_DevOps_sends_a_non_ascii_key_encoded_so_the_forge_judges_it()
    {
        var (port, listener) = Serve(Http(401, "application/json", "{\"message\":\"Bad credentials\"}"));
        try
        {
            var result = await Tester(new HttpClient()).TestRemoteProviderAsync(
                Provider("AzureDevOps", $"http://127.0.0.1:{port}", "sekrit KEY-9f3a"), CancellationToken.None);

            Assert.Equal(ConnectionTestOutcome.InvalidApiKey, result.Outcome);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Theory]
    [InlineData("GitHub", 200, ConnectionTestOutcome.Ok, "someone")]
    [InlineData("AzureDevOps", 401, ConnectionTestOutcome.InvalidApiKey, "Bad credentials")]
    public async Task A_response_in_an_unknown_charset_is_still_read(string type, int status, ConnectionTestOutcome expected, string evidence)
    {
        var body = status == 200 ? "{\"login\":\"someone\"}" : "{\"message\":\"Bad credentials\"}";
        var (port, listener) = Serve(Http(status, "application/json; charset=bogus", body));
        try
        {
            var result = await Tester(new HttpClient()).TestRemoteProviderAsync(
                Provider(type, $"http://127.0.0.1:{port}", "sekrit-KEY-9f3a"), CancellationToken.None);

            Assert.Equal(expected, result.Outcome);
            Assert.Contains(evidence, $"{result.Message}\n{result.Detail}");
        }
        finally
        {
            listener.Stop();
        }
    }
}
