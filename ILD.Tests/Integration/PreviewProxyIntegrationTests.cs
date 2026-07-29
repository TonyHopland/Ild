using System.Net;
using System.Net.WebSockets;
using System.Text;
using ILD.Api.Middleware;
using ILD.Core.Services.Interfaces;
using ILD.Data.DTOs;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Moq;

namespace ILD.Tests.Integration;

/// <summary>
/// Drives <see cref="PreviewProxyMiddleware"/> over real sockets against a
/// deliberately plain HTTP backend — a static file server, a redirect that sets
/// cookies, a streaming endpoint and a WebSocket echo. The fixture is framework
/// agnostic on purpose: previews run whatever a repository happens to ship, so
/// nothing here may depend on the shape of ILD's own dev server.
/// </summary>
public sealed class PreviewProxyIntegrationTests : IAsyncLifetime
{
    private const string BaseHost = "preview.test";

    private PreviewBackend _backend = null!;
    private WebApplication _proxy = null!;
    private HttpClient _client = null!;
    private int _proxyPort;

    /// <summary>Resolution the stubbed preview service returns; swapped per test.</summary>
    private Func<string, PreviewTarget> _resolve = null!;

    public async Task InitializeAsync()
    {
        _backend = await PreviewBackend.StartAsync();
        _resolve = _ => PreviewTarget.Resolved(_backend.Port, "app", rewriteHost: true);

        await StartProxyAsync($"http://{BaseHost}");

        // Redirects are asserted on, not followed: a rewritten Location points at a
        // hostname that deliberately does not resolve anywhere.
        _client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
        {
            Timeout = TimeSpan.FromSeconds(30),
        };
    }

    private async Task StartProxyAsync(string proxyBase)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddHttpForwarder();
        builder.Services.AddSingleton(PreviewProxyBase.Parse(proxyBase));
        builder.Services.AddSingleton<IWorktreePreviewService>(new StubPreviewService(label => _resolve(label)));
        builder.Services.AddScoped(_ => Mock.Of<IWorkItemManager>());

        _proxy = builder.Build();
        _proxy.UseWebSockets();
        _proxy.UseMiddleware<PreviewProxyMiddleware>();
        // Stands in for the rest of the real pipeline (auth, routing, the UI). A
        // request that reaches here was NOT treated as a preview.
        _proxy.Run(context => context.Response.WriteAsync("apex-pipeline"));

        await _proxy.StartAsync();
        _proxyPort = PortOf(_proxy);
    }

    /// <summary>
    /// Rebuilds the proxy on a different configured origin. The listener stays plain
    /// HTTP either way — that is the point: it reproduces ILD in the container,
    /// where TLS is terminated by the ingress and the request arriving here is
    /// always http however the browser reached it.
    /// </summary>
    private async Task UseProxyBaseAsync(string proxyBase)
    {
        await _proxy.StopAsync();
        await _proxy.DisposeAsync();
        await StartProxyAsync(proxyBase);
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _proxy.StopAsync();
        await _proxy.DisposeAsync();
        await _backend.DisposeAsync();
    }

    private static int PortOf(WebApplication app)
    {
        var address = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.First();
        return new Uri(address).Port;
    }

    private HttpRequestMessage Request(HttpMethod method, string path, string host = "wi-7." + BaseHost)
    {
        var request = new HttpRequestMessage(method, $"http://127.0.0.1:{_proxyPort}{path}");
        request.Headers.Host = host;
        return request;
    }

    [Theory]
    [InlineData(BaseHost)]              // the apex host serves the ILD UI
    [InlineData("localhost")]
    [InlineData("not" + BaseHost)]      // suffix match without the dot separator
    public async Task Requests_that_are_not_preview_hostnames_travel_the_normal_pipeline(string host)
    {
        using var response = await _client.SendAsync(Request(HttpMethod.Get, "/api/v1/health", host));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("apex-pipeline", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Static_content_is_forwarded_verbatim()
    {
        using var response = await _client.SendAsync(Request(HttpMethod.Get, "/assets/app.js"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(PreviewBackend.ScriptBody, await response.Content.ReadAsStringAsync());
        Assert.Equal("text/javascript", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task The_host_header_is_rewritten_to_the_loopback_target_by_default()
    {
        var headers = await GetEchoedHeadersAsync();

        // Host-checking dev servers only accept the authority they are bound to.
        Assert.Equal($"127.0.0.1:{_backend.Port}", headers["host"]);
        Assert.Equal($"wi-7.{BaseHost}", headers["x-forwarded-host"]);
        Assert.Equal("http", headers["x-forwarded-proto"]);
        Assert.False(string.IsNullOrWhiteSpace(headers["x-forwarded-for"]));
    }

    [Fact]
    public async Task A_service_can_opt_out_of_host_rewriting()
    {
        _resolve = _ => PreviewTarget.Resolved(_backend.Port, "app", rewriteHost: false);

        var headers = await GetEchoedHeadersAsync();

        Assert.Equal($"wi-7.{BaseHost}", headers["host"]);
    }

    [Fact]
    public async Task Forwarded_headers_the_client_made_up_are_replaced_not_trusted()
    {
        var request = Request(HttpMethod.Get, "/echo-headers");
        request.Headers.TryAddWithoutValidation("X-Forwarded-Proto", "https");
        request.Headers.TryAddWithoutValidation("X-Forwarded-Host", "evil.example.com");
        request.Headers.TryAddWithoutValidation("X-Forwarded-For", "10.0.0.1");

        using var response = await _client.SendAsync(request);
        var headers = ParseHeaders(await response.Content.ReadAsStringAsync());

        Assert.Equal("http", headers["x-forwarded-proto"]);
        Assert.Equal($"wi-7.{BaseHost}", headers["x-forwarded-host"]);

        // X-Forwarded-For is replaced outright rather than appended to. This hop is
        // directly reachable and unauthenticated, so an inbound value is a string the
        // caller chose — and preserving it would leave 10.0.0.1 leftmost, which is
        // where an app behind a proxy reads the client IP from and what an allowlist
        // or per-IP rate limiter would go on to trust.
        Assert.DoesNotContain("10.0.0.1", headers["x-forwarded-for"]);
        Assert.Equal("127.0.0.1", headers["x-forwarded-for"]);
    }

    [Fact]
    public async Task Redirects_and_cookies_are_rewritten_onto_the_preview_hostname()
    {
        using var response = await _client.SendAsync(Request(HttpMethod.Get, "/redirect"));

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        // The backend redirected to its own loopback authority — unreachable from a
        // browser — so the proxy points it back at the hostname the browser used.
        Assert.Equal($"http://wi-7.{BaseHost}/landed", response.Headers.Location?.ToString());

        var cookie = Assert.Single(response.Headers.GetValues("Set-Cookie"));
        Assert.Contains("sid=abc", cookie);
        Assert.DoesNotContain("Domain=", cookie, StringComparison.OrdinalIgnoreCase);
        // The preview origin is plain http here, so a Secure cookie would be dropped
        // by the browser — and SameSite=None is invalid without it.
        Assert.DoesNotContain("Secure", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("SameSite=Lax", cookie);
        Assert.Contains("HttpOnly", cookie);
        Assert.Contains("Path=/", cookie);
    }

    [Fact]
    public async Task An_https_base_is_honoured_even_though_the_request_arrives_over_plain_http()
    {
        // ILD listens on http and does not run UseForwardedHeaders, so behind a
        // TLS-terminating ingress every request here looks like plain http. Reading
        // the scheme off the request would downgrade the browser to http and tell
        // the preview the wrong protocol.
        await UseProxyBaseAsync($"https://{BaseHost}");

        var headers = await GetEchoedHeadersAsync();
        Assert.Equal("https", headers["x-forwarded-proto"]);

        using var response = await _client.SendAsync(Request(HttpMethod.Get, "/redirect"));
        Assert.Equal($"https://wi-7.{BaseHost}/landed", response.Headers.Location?.ToString());
    }

    [Fact]
    public async Task An_https_base_keeps_a_preview_cookie_secure()
    {
        await UseProxyBaseAsync($"https://{BaseHost}");

        using var response = await _client.SendAsync(Request(HttpMethod.Get, "/redirect"));
        var cookie = Assert.Single(response.Headers.GetValues("Set-Cookie"));

        // The browser did receive this over TLS, so stripping Secure — as judging by
        // the in-container request scheme would — weakens a cookie for no reason.
        Assert.Contains("Secure", cookie, StringComparison.Ordinal);
        Assert.Contains("SameSite=None", cookie);
        Assert.DoesNotContain("Domain=", cookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Absolute_redirects_to_somewhere_else_are_left_alone()
    {
        using var response = await _client.SendAsync(Request(HttpMethod.Get, "/redirect-external"));

        Assert.Equal("https://accounts.example.com/authorize", response.Headers.Location?.ToString());
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    public async Task Every_method_is_forwarded(string method)
    {
        var request = Request(new HttpMethod(method), "/method");
        request.Content = new StringContent("x");

        using var response = await _client.SendAsync(request);

        Assert.Equal(method, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Large_request_and_response_bodies_round_trip()
    {
        var payload = new string('p', 4 * 1024 * 1024);
        var request = Request(HttpMethod.Post, "/echo");
        request.Content = new StringContent(payload, Encoding.UTF8, "text/plain");

        using var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(payload, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Streaming_responses_reach_the_client_before_the_backend_finishes()
    {
        using var response = await _client.SendAsync(
            Request(HttpMethod.Get, "/stream"),
            HttpCompletionOption.ResponseHeadersRead);

        await using var stream = await response.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(stream);

        // The backend is still parked on its gate. If anything buffered the
        // response, this read would block until the request completed.
        var first = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("data: first", first);

        _backend.ReleaseStream();

        Assert.Equal("", await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal("data: second", await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task Websocket_upgrades_pass_through()
    {
        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Host", $"wi-7.{BaseHost}");

        await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{_proxyPort}/ws"), CancellationToken.None);

        var sent = Encoding.UTF8.GetBytes("hello preview");
        await socket.SendAsync(sent, WebSocketMessageType.Text, true, CancellationToken.None);

        var buffer = new byte[128];
        var received = await socket.ReceiveAsync(buffer, CancellationToken.None);

        Assert.Equal("hello preview", Encoding.UTF8.GetString(buffer, 0, received.Count));
        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
    }

    [Theory]
    [InlineData(PreviewTargetOutcome.NotAPreviewHost)]
    [InlineData(PreviewTargetOutcome.UnknownWorkItem)]
    [InlineData(PreviewTargetOutcome.NoWorktree)]
    [InlineData(PreviewTargetOutcome.PreviewNotRunning)]
    [InlineData(PreviewTargetOutcome.ServiceNotRunning)]
    [InlineData(PreviewTargetOutcome.AmbiguousService)]
    public async Task Every_unresolved_outcome_is_the_same_404_and_leaks_nothing(PreviewTargetOutcome outcome)
    {
        // The resolver's message names internal state — which work item, which
        // services. It belongs in ILD's log, not in a response anyone can ask for.
        _resolve = _ => PreviewTarget.Failed(outcome, "work item 12 is running api, frontend");

        using var response = await _client.SendAsync(Request(HttpMethod.Get, "/"));
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.DoesNotContain("work item 12", body);
        Assert.DoesNotContain("api, frontend", body);
        Assert.DoesNotContain(outcome.ToString(), body);
    }

    [Fact]
    public async Task A_preview_that_stopped_listening_is_indistinguishable_from_one_that_never_existed()
    {
        // A port nothing is bound to: the runtime still lists the service, but the
        // process behind it has gone. Reporting that as a 502 would confirm the
        // preview exists, which is exactly what the 404 is there to avoid.
        _resolve = _ => PreviewTarget.Resolved(_backend.ClosedPort, "app", rewriteHost: true);

        using var response = await _client.SendAsync(Request(HttpMethod.Get, "/"));
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.DoesNotContain("app", body);
        Assert.DoesNotContain($"{_backend.ClosedPort}", body);
    }

    private async Task<Dictionary<string, string>> GetEchoedHeadersAsync()
    {
        using var response = await _client.SendAsync(Request(HttpMethod.Get, "/echo-headers"));
        response.EnsureSuccessStatusCode();
        return ParseHeaders(await response.Content.ReadAsStringAsync());
    }

    private static Dictionary<string, string> ParseHeaders(string body)
        => body.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split('=', 2))
            .ToDictionary(parts => parts[0], parts => parts.Length > 1 ? parts[1] : string.Empty, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// A plain HTTP/WebSocket server standing in for "whatever the repository being
    /// previewed happens to run". Deliberately not an ILD component.
    /// </summary>
    private sealed class PreviewBackend : IAsyncDisposable
    {
        internal const string ScriptBody = "export const preview = true;\n";

        private readonly WebApplication _app;
        private readonly string _webRoot;
        private readonly TaskCompletionSource _streamGate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private PreviewBackend(WebApplication app, string webRoot, int port, int closedPort)
        {
            _app = app;
            _webRoot = webRoot;
            Port = port;
            ClosedPort = closedPort;
        }

        public int Port { get; }

        /// <summary>A port that was bound long enough to be reserved, then released.</summary>
        public int ClosedPort { get; }

        public void ReleaseStream() => _streamGate.TrySetResult();

        public static async Task<PreviewBackend> StartAsync()
        {
            var webRoot = Path.Combine(Path.GetTempPath(), "ild-preview-backend-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(webRoot, "assets"));
            await File.WriteAllTextAsync(Path.Combine(webRoot, "assets", "app.js"), ScriptBody);
            await File.WriteAllTextAsync(Path.Combine(webRoot, "index.html"), "<h1>preview</h1>");

            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { WebRootPath = webRoot });
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Logging.ClearProviders();
            var app = builder.Build();

            PreviewBackend backend = null!;

            app.UseWebSockets();
            app.UseDefaultFiles();
            app.UseStaticFiles();

            app.MapGet("/echo-headers", (HttpContext context) =>
            {
                var interesting = new[] { "host", "x-forwarded-host", "x-forwarded-proto", "x-forwarded-for" };
                var body = string.Join('\n', interesting.Select(name =>
                    $"{name}={(context.Request.Headers.TryGetValue(name, out var value) ? value.ToString() : "")}"));
                return Results.Text(body);
            });

            app.MapGet("/redirect", (HttpContext context) =>
            {
                context.Response.Headers.Append(
                    "Set-Cookie",
                    $"sid=abc; Domain=127.0.0.1; Path=/; Secure; SameSite=None; HttpOnly");
                return Results.Redirect($"http://127.0.0.1:{backend.Port}/landed");
            });
            app.MapGet("/redirect-external", () => Results.Redirect("https://accounts.example.com/authorize"));
            app.MapGet("/landed", () => Results.Text("landed"));

            app.MapMethods("/method", ["POST", "PUT", "PATCH", "DELETE"], (HttpContext c) => Results.Text(c.Request.Method));

            app.MapPost("/echo", async (HttpContext context) =>
            {
                using var reader = new StreamReader(context.Request.Body);
                var body = await reader.ReadToEndAsync();
                return Results.Text(body);
            });

            app.MapGet("/stream", async (HttpContext context) =>
            {
                context.Response.Headers.ContentType = "text/event-stream";
                await context.Response.WriteAsync("data: first\n\n");
                await context.Response.Body.FlushAsync();
                await backend._streamGate.Task;
                await context.Response.WriteAsync("data: second\n\n");
                await context.Response.Body.FlushAsync();
            });

            app.Map("/ws", async (HttpContext context) =>
            {
                if (!context.WebSockets.IsWebSocketRequest)
                {
                    context.Response.StatusCode = StatusCodes.Status400BadRequest;
                    return;
                }

                using var socket = await context.WebSockets.AcceptWebSocketAsync();
                var buffer = new byte[4096];
                while (socket.State == WebSocketState.Open)
                {
                    var result = await socket.ReceiveAsync(buffer, CancellationToken.None);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
                        break;
                    }

                    await socket.SendAsync(
                        new ArraySegment<byte>(buffer, 0, result.Count),
                        result.MessageType,
                        result.EndOfMessage,
                        CancellationToken.None);
                }
            });

            await app.StartAsync();

            var closedListener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            closedListener.Start();
            var closedPort = ((IPEndPoint)closedListener.LocalEndpoint).Port;
            closedListener.Stop();

            backend = new PreviewBackend(app, webRoot, PortOf(app), closedPort);
            return backend;
        }

        public async ValueTask DisposeAsync()
        {
            ReleaseStream();
            await _app.StopAsync();
            await _app.DisposeAsync();
            try { Directory.Delete(_webRoot, recursive: true); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// Stands in for the preview runtime: the resolver chain has its own unit tests
    /// (<c>WorktreePreviewServiceProxyTargetTests</c>), so these tests fix the target
    /// and exercise the forwarding.
    /// </summary>
    private sealed class StubPreviewService : IWorktreePreviewService
    {
        private readonly Func<string, PreviewTarget> _resolve;

        public StubPreviewService(Func<string, PreviewTarget> resolve) => _resolve = resolve;

        public Task<PreviewTarget> ResolvePreviewTargetAsync(string hostLabel, IWorkItemManager workItems, CancellationToken cancellationToken = default)
            => Task.FromResult(_resolve(hostLabel));

        public bool IsPreviewRunning(string worktreePath) => true;

        public Task<WorktreePreviewResponse> GetStatusAsync(string worktreePath, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<WorktreePreviewResponse> StartAsync(string worktreePath, WorktreePreviewStartOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<WorktreePreviewResponse> StopAsync(string worktreePath, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<WorktreePreviewResponse> StartServiceAsync(string worktreePath, string serviceName, WorktreePreviewStartOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<WorktreePreviewResponse> StopServiceAsync(string worktreePath, string serviceName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<string?> GetServiceConfigAsync(string worktreePath, string serviceName, string? profileName = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task UpdateServiceConfigAsync(string worktreePath, string serviceName, string serviceConfigJson, string? profileName = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<string?> GetServiceLogAsync(string worktreePath, string serviceName, int maxBytes = 64 * 1024, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<WorktreeInstallResult> InstallAsync(string worktreePath, string? profileName = null, string? customEnv = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<WorktreePreviewValidationResult> ValidateConfigAsync(string worktreePath, string? profileName = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
