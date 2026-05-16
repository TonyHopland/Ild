using System.Net;
using System.Net.Http.Headers;
using System.Text;
using FluentAssertions;
using ILD.Core.Services.Implementations;
using ILD.Data.Entities;

namespace ILD.Tests;

public class RemoteProviderServiceTests
{
    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(Clone(request));
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"url\":\"https://example.test/api/pr/1\",\"html_url\":\"https://example.test/pr/1\"}", Encoding.UTF8, "application/json"),
            };

            return Task.FromResult(response);
        }

        private static HttpRequestMessage Clone(HttpRequestMessage request)
        {
            var clone = new HttpRequestMessage(request.Method, request.RequestUri);
            foreach (var header in request.Headers)
                clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
            return clone;
        }
    }

    [Fact]
    public async Task CreatePullRequestAsync_sends_provider_api_key_in_auth_header()
    {
        using var db = new TestDb();
        db.Context.RemoteProviders.Add(new RemoteProvider
        {
            Id = Guid.NewGuid(),
            Name = "gitea",
            Type = "Forgejo",
            Url = "https://gitea.example",
            ApiKey = "provider-key",
        });
        db.Context.SaveChanges();

        var handler = new RecordingHandler();
        var service = new RemoteProviderService(db.Providers, new HttpClient(handler));

        var result = await service.CreatePullRequestAsync(
            "https://gitea.example/team/repo.git",
            "ild/test",
            "main",
            "title",
            "body");

        result.Error.Should().BeNull();
        handler.Requests.Should().ContainSingle();
        handler.Requests[0].Headers.Authorization.Should().BeEquivalentTo(new AuthenticationHeaderValue("token", "provider-key"));
    }

    [Fact]
    public async Task CreatePullRequestAsync_uses_matching_provider_api_key_for_each_request()
    {
        using var db = new TestDb();
        db.Context.RemoteProviders.AddRange(
            new RemoteProvider
            {
                Id = Guid.NewGuid(),
                Name = "gitea",
                Type = "Forgejo",
                Url = "https://gitea.example",
                ApiKey = "gitea-key",
            },
            new RemoteProvider
            {
                Id = Guid.NewGuid(),
                Name = "forgejo",
                Type = "Forgejo",
                Url = "https://forge.example",
                ApiKey = "forge-key",
            });
        db.Context.SaveChanges();

        var handler = new RecordingHandler();
        var service = new RemoteProviderService(db.Providers, new HttpClient(handler));

        await service.CreatePullRequestAsync("https://gitea.example/team/repo.git", "ild/one", "main", "title", "body");
        await service.CreatePullRequestAsync("https://forge.example/team/repo.git", "ild/two", "main", "title", "body");

        handler.Requests.Should().HaveCount(2);
        handler.Requests[0].Headers.Authorization.Should().BeEquivalentTo(new AuthenticationHeaderValue("token", "gitea-key"));
        handler.Requests[1].Headers.Authorization.Should().BeEquivalentTo(new AuthenticationHeaderValue("token", "forge-key"));
    }
}