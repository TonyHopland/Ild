using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace ILD.Tests.Integration;

/// <summary>
/// The pipeline-level half of the authorization story: what the user-only
/// fallback policy must NOT break. Every endpoint being deny-by-default is only
/// safe if the handful of things that legitimately run without a session — the
/// SPA shell, its bundle, the probes — still do, and if the clients that cannot
/// send an Authorization header can still authenticate.
/// </summary>
[Collection("AuthEnvironment")]
public class AuthorizationPolicyTests
{
    [Theory]
    [InlineData("/")]                    // UseDefaultFiles -> index.html
    [InlineData("/workitems")]           // an SPA route: served by the fallback
    [InlineData("/assets/app.js")]       // the bundle the shell then loads
    public async Task The_SPA_shell_is_served_to_a_caller_with_no_session(string path)
    {
        // Nobody can log in through a UI that itself demands a login, and the
        // fallback file is a routed endpoint, so it needs the explicit opt-out.
        await using var factory = new ApiFactory();
        var client = factory.CreateClient();

        var response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData("/api/v1/health")]
    [InlineData("/metrics")]
    public async Task An_operational_endpoint_answers_without_a_session(string path)
    {
        await using var factory = new ApiFactory();
        var client = factory.CreateClient();

        var response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData("/api/v1/loopruns/00000000-0000-0000-0000-000000000001/terminal")]
    [InlineData("/api/v1/aiproviders/00000000-0000-0000-0000-000000000001/interactive")]
    public async Task A_raw_WebSocket_endpoint_authenticates_from_the_access_token_query(string path)
    {
        // The worktree terminal and the interactive provider session are plain
        // controller actions the browser upgrades to a WebSocket, so the token
        // can only travel in the query string. Sent as an ordinary GET here: each
        // action's first act is to reject a non-upgrade request, so a 400 means
        // authorization let it through — which is the whole of what this pins.
        await using var factory = new ApiFactory();
        var client = factory.CreateClient();
        var token = await factory.GetAdminTokenAsync();

        var anonymous = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

        var authenticated = await client.GetAsync($"{path}?access_token={token}");
        Assert.Equal(HttpStatusCode.BadRequest, authenticated.StatusCode);
    }

    [Theory]
    [InlineData("/api/v1/looprins")]
    [InlineData("/hubs/loop-runs")]
    [InlineData("/metrics/summary")]
    public async Task An_unknown_path_under_a_non_SPA_prefix_is_not_answered_with_the_SPA(string path)
    {
        // The SPA fallback is a catch-all over every extensionless path, so
        // without a narrower fallback ahead of it a mistyped API route would come
        // back as 200 text/html — and to an anonymous caller at that.
        await using var factory = new ApiFactory();
        var client = factory.CreateClient();

        var anonymous = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

        var authenticated = await (await factory.CreateAuthenticatedClientAsync()).GetAsync(path);
        Assert.Equal(HttpStatusCode.NotFound, authenticated.StatusCode);
        Assert.DoesNotContain("ILD-UI-SPA-MARKER", await authenticated.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_rejected_request_keeps_the_401_body_clients_already_parse()
    {
        await using var factory = new ApiFactory();
        var client = factory.CreateClient();

        var missing = await client.GetAsync("/api/v1/repositories");
        Assert.Equal(HttpStatusCode.Unauthorized, missing.StatusCode);
        Assert.Equal("application/json", missing.Content.Headers.ContentType?.MediaType);
        var body = await missing.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Unauthorized", body.GetProperty("error").GetString());
        Assert.Equal("No authentication token provided", body.GetProperty("message").GetString());

        var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/repositories");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "not-a-session");
        var rejected = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);
        Assert.Equal(
            "Invalid or expired session",
            (await rejected.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("message").GetString());
    }

    [Fact]
    public async Task A_hub_authenticates_from_the_access_token_query_on_negotiate_and_on_the_socket()
    {
        // A browser cannot put a header on a WebSocket handshake, so SignalR
        // appends the token to the query string instead. Both legs are separate
        // requests through the whole pipeline and both have to be let in.
        await using var factory = new ApiFactory();
        var token = await factory.GetAdminTokenAsync();
        var client = factory.CreateClient();

        var anonymous = await client.PostAsync("/hubs/loop-run/negotiate?negotiateVersion=1", null);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

        var negotiate = await client.PostAsync($"/hubs/loop-run/negotiate?negotiateVersion=1&access_token={token}", null);
        Assert.Equal(HttpStatusCode.OK, negotiate.StatusCode);
        var connectionToken = (await negotiate.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("connectionToken").GetString();

        var sockets = factory.Server.CreateWebSocketClient();
        var hub = new Uri(factory.Server.BaseAddress, $"/hubs/loop-run?id={connectionToken}&access_token={token}");
        using var socket = await sockets.ConnectAsync(hub, CancellationToken.None);
        Assert.Equal(WebSocketState.Open, socket.State);

        var unauthenticatedHub = new Uri(factory.Server.BaseAddress, $"/hubs/loop-run?id={connectionToken}");
        await Assert.ThrowsAnyAsync<Exception>(
            () => sockets.ConnectAsync(unauthenticatedHub, CancellationToken.None));
    }

    /// <summary>
    /// The hubs stream a user's chats, runs and backlog events, and ILD's own
    /// spawned agents have no business on any of them. They carry no opt-out, so
    /// the user-only fallback policy already refuses the agent service token —
    /// this pins that, on both legs of the handshake, so a later opt-out or a
    /// change to the fallback cannot quietly hand an agent a live event stream.
    /// </summary>
    [Theory]
    [InlineData("/hubs/chat")]
    [InlineData("/hubs/loop-run")]
    [InlineData("/hubs/work-item")]
    public async Task The_agent_token_is_refused_at_every_hub(string path)
    {
        await using var factory = new ApiFactory();
        var agentToken = factory.Services.GetRequiredService<ILD.Api.Configuration.AgentAuthTokenProvider>().Token;
        var client = factory.CreateClient();

        // Authenticated but not a user, so it is a 403 and not a 401: the token is
        // recognised, the role is what stops it.
        var negotiate = await client.PostAsync($"{path}/negotiate?negotiateVersion=1&access_token={agentToken}", null);
        Assert.Equal(HttpStatusCode.Forbidden, negotiate.StatusCode);

        // The socket leg authorizes independently of negotiate, so it has to be
        // checked on its own — borrow a connection token from a legitimate user
        // negotiate and present the agent token on the upgrade.
        var userToken = await factory.GetAdminTokenAsync();
        var userNegotiate = await client.PostAsync($"{path}/negotiate?negotiateVersion=1&access_token={userToken}", null);
        Assert.Equal(HttpStatusCode.OK, userNegotiate.StatusCode);
        var connectionToken = (await userNegotiate.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("connectionToken").GetString();

        var sockets = factory.Server.CreateWebSocketClient();
        var asAgent = new Uri(factory.Server.BaseAddress, $"{path}?id={connectionToken}&access_token={agentToken}");
        await Assert.ThrowsAnyAsync<Exception>(() => sockets.ConnectAsync(asAgent, CancellationToken.None));
    }

    /// <summary>
    /// The other side of the fallback policy: the opt-out on AgentController has
    /// to actually admit the agent, or ILD's MCP server is locked out of every
    /// tool at once. These are the paths behind the read-only tools, requested
    /// exactly as <c>ILD.McpServer</c>'s <c>IldClient</c> sends them — the agent
    /// service token as a bearer header, plus the run-id header it always sets.
    /// </summary>
    [Theory]
    [InlineData("/api/v1/agent/repositories?skip=0&take=100")]
    [InlineData("/api/v1/agent/workitems?skip=0&take=100")]
    [InlineData("/api/v1/agent/workitems/summary")]
    [InlineData("/api/v1/agent/loop-templates?skip=0&take=100")]
    [InlineData("/api/v1/agent/loop-runs")]
    public async Task The_agent_token_reaches_the_agent_surface(string path)
    {
        await using var factory = new ApiFactory();
        var client = factory.CreateClient();
        var token = factory.Services.GetRequiredService<ILD.Api.Configuration.AgentAuthTokenProvider>().Token;
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        client.DefaultRequestHeaders.Add("X-ILD-Run-Id", Guid.NewGuid().ToString());

        var response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>
    /// An Azure DevOps service hook has only one Authorization header and its own
    /// verification needs it, so the ILD token travels in the query instead. If
    /// the handler read the Basic credential as a token, the outer gate would
    /// reject every such hook before the adapter ever saw it.
    /// </summary>
    [Fact]
    public async Task An_azure_devops_webhook_authenticates_from_the_query_while_basic_auth_carries_its_own_secret()
    {
        const string webhookSecret = "azure-hook-secret";
        await using var factory = new ApiFactory();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ILD.Data.Entities.AppDbContext>();
            db.RemoteProviders.Add(new ILD.Data.Entities.RemoteProvider
            {
                Id = Guid.NewGuid(),
                Name = "azure",
                Type = "AzureDevOps",
                Url = "https://dev.azure.com/contoso",
                WebhookSecret = webhookSecret,
            });
            await db.SaveChangesAsync();
        }

        var token = await factory.GetAdminTokenAsync();
        var client = factory.CreateClient();
        var basic = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Basic", Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"ild:{webhookSecret}")));
        var payload = """
            {"eventType":"git.pullrequest.merged","resource":{"pullRequestId":7,"status":"completed",
             "repository":{"id":"repo-guid","webUrl":"https://dev.azure.com/contoso/widgets/_git/app"}}}
            """;

        HttpRequestMessage Hook(string url)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = JsonContent.Create(JsonDocument.Parse(payload).RootElement),
            };
            request.Headers.Authorization = basic;
            return request;
        }

        var withoutToken = await client.SendAsync(Hook("/api/v1/webhooks/azuredevops"));
        Assert.Equal(HttpStatusCode.Unauthorized, withoutToken.StatusCode);

        var withToken = await client.SendAsync(Hook($"/api/v1/webhooks/azuredevops?access_token={token}"));
        Assert.Equal(HttpStatusCode.OK, withToken.StatusCode);
    }

    /// <summary>
    /// Turning the level up is anonymous on purpose — an operator whose sign-in
    /// is broken needs it — but what the process wrote is another matter: log
    /// lines carry hostnames, tokens and user data, so reading them takes a
    /// session. The two live on separate controllers because a class-level
    /// [AllowAnonymous] cannot be tightened again per action.
    /// </summary>
    [Fact]
    public async Task Reading_the_log_takes_a_session_while_the_level_stays_anonymous()
    {
        await using var factory = new ApiFactory();
        var anonymous = factory.CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync("/api/v1/logging/level")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/v1/logging/entries")).StatusCode);

        var signedIn = await factory.CreateAuthenticatedClientAsync();
        var entries = await signedIn.GetAsync("/api/v1/logging/entries?take=5");

        Assert.Equal(HttpStatusCode.OK, entries.StatusCode);
    }
}
