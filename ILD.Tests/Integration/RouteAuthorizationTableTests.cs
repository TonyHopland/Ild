using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.DependencyInjection;
using ILD.Api.Authentication;

namespace ILD.Tests.Integration;

/// <summary>
/// Every controller route the running app maps and does not open to anonymous callers is
/// refused without a token, and refused to the agent token unless it is an agent-or-user route.
/// Read from the endpoint data source, so a route added later is probed without being listed.
/// </summary>
public class RouteAuthorizationTableTests(RouteAuthorizationTableTests.Host host) : IClassFixture<RouteAuthorizationTableTests.Host>
{
    public sealed class Host : IAsyncLifetime
    {
        public ApiFactory Factory { get; } = new();

        public ValueTask InitializeAsync() => ValueTask.CompletedTask;

        public async ValueTask DisposeAsync() => await Factory.DisposeAsync();
    }

    private sealed record Probe(string Verb, string Path, bool AgentOrUser);

    private static string ValueFor(RoutePatternParameterPart part)
    {
        var constraints = part.ParameterPolicies.Select(p => p.Content ?? string.Empty).ToList();
        if (constraints.Contains("guid")) return Guid.NewGuid().ToString();
        if (constraints.Contains("int") || constraints.Contains("long")) return "1";
        return "x";
    }

    private static List<Probe> Probes(IServiceProvider services)
    {
        var probes = new List<Probe>();
        foreach (var endpoint in services.GetServices<EndpointDataSource>().SelectMany(s => s.Endpoints).OfType<RouteEndpoint>())
        {
            if (endpoint.Metadata.GetMetadata<ControllerActionDescriptor>() is null) continue;
            if (endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null) continue;

            var path = "/" + string.Join("/", endpoint.RoutePattern.PathSegments.Select(segment =>
                string.Concat(segment.Parts.Select(part => part switch
                {
                    RoutePatternLiteralPart literal => literal.Content,
                    RoutePatternParameterPart parameter => ValueFor(parameter),
                    _ => string.Empty,
                }))));
            var agentOrUser = endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>()
                .Any(a => a.Policy == IldAuthentication.AgentOrUserPolicy);
            var verbs = endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? ["GET"];
            foreach (var verb in verbs)
                probes.Add(new Probe(verb, path, agentOrUser));
        }
        return probes;
    }

    [Fact]
    public async Task Every_user_facing_route_refuses_no_token_with_401_and_the_agent_token_with_403_unless_it_is_agent_or_user()
    {
        var probes = Probes(host.Factory.Services);
        Assert.True(probes.Count > 100, $"only {probes.Count} routes were enumerated");
        var anonymous = host.Factory.CreateClient();
        var agent = host.Factory.CreateClient();
        agent.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            host.Factory.Services.GetRequiredService<ILD.Api.Configuration.AgentAuthTokenProvider>().Token);

        var mismatches = new List<string>();
        foreach (var probe in probes)
        {
            using var noToken = await anonymous.SendAsync(new HttpRequestMessage(new HttpMethod(probe.Verb), probe.Path), TestContext.Current.CancellationToken);
            if (noToken.StatusCode != HttpStatusCode.Unauthorized)
                mismatches.Add($"{probe.Verb} {probe.Path} (no token) → {(int)noToken.StatusCode}");
            if (probe.AgentOrUser) continue;
            using var asAgent = await agent.SendAsync(new HttpRequestMessage(new HttpMethod(probe.Verb), probe.Path), TestContext.Current.CancellationToken);
            if (asAgent.StatusCode != HttpStatusCode.Forbidden)
                mismatches.Add($"{probe.Verb} {probe.Path} (agent token) → {(int)asAgent.StatusCode}");
        }

        Assert.Empty(mismatches);
    }
}
