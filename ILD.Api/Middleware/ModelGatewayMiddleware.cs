using System.Net;
using System.Text.RegularExpressions;
using ILD.Core.Services.Implementations.ModelGateway;
using ILD.Data.Stores.Interfaces;
using Microsoft.AspNetCore.Http.Features;
using Yarp.ReverseProxy.Forwarder;

namespace ILD.Api.Middleware;

/// <summary>
/// The model gateway (ADR-0031). An agent CLI is pointed at <c>/gateway/{route}/…</c> and holds only a
/// placeholder, which is the per-run token. This checks the token, swaps it for the provider's real
/// credential and forwards over ILD's own connection, streaming the answer back unbuffered.
/// It sits ahead of authentication, CORS and the request log: the token travels in a header and never
/// in the path, so nothing logs it.
/// </summary>
public sealed partial class ModelGatewayMiddleware
{
    public const string PathPrefix = "/gateway";
    public const string TokenHeader = GatewayRoutes.TokenHeader;
    private const string CopilotEndpointPrefix = "_ep/";
    private const string CopilotUserPath = "copilot_internal/user";

    private static readonly string[] CredentialHeaders = ["Authorization", "x-api-key", "api-key", TokenHeader];

    private static readonly HttpMessageInvoker Client = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.None,
        UseCookies = false,
        ActivityHeadersPropagator = null,
        ConnectTimeout = TimeSpan.FromSeconds(15),
    });

    private static readonly ForwarderRequestConfig RequestConfig = new()
    {
        ActivityTimeout = TimeSpan.FromMinutes(30),
        Version = HttpVersion.Version11,
        VersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
    };

    private readonly RequestDelegate _next;
    private readonly IHttpForwarder _forwarder;
    private readonly ILogger<ModelGatewayMiddleware> _logger;

    public ModelGatewayMiddleware(RequestDelegate next, IHttpForwarder forwarder, ILogger<ModelGatewayMiddleware> logger)
    {
        _next = next;
        _forwarder = forwarder;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var services = context.RequestServices;
        var tls = services.GetService<GatewayTlsEndpoint>();
        var onTlsListener = tls is not null && context.Connection.LocalPort == tls.Port;
        PathString remaining;
        if (onTlsListener)
            remaining = new PathString("/" + GatewayRoutes.GitHub + context.Request.Path);
        else if (!context.Request.Path.StartsWithSegments(PathPrefix, out remaining))
        {
            await _next(context);
            return;
        }

        var registry = services.GetRequiredService<IModelGatewayRegistry>();
        var state = registry.Lookup(PresentedToken(context.Request, registry), out var grant);
        if (state == GatewayTokenState.Unknown)
        {
            await RefuseAsync(context, StatusCodes.Status401Unauthorized, "The gateway needs the run's token.");
            return;
        }
        if (state == GatewayTokenState.Ended)
        {
            await RefuseAsync(context, StatusCodes.Status403Forbidden, "The run this token belongs to has ended.");
            return;
        }

        var segments = remaining.Value!.Trim('/').Split('/', 2);
        var route = segments[0];
        var rest = segments.Length > 1 ? segments[1] : string.Empty;

        var provider = await services.GetRequiredService<IProviderStore>().GetAiProviderByIdAsync(grant!.ProviderId);
        var source = services.GetRequiredService<IModelCredentialSource>();
        var credential = provider is null ? null : await source.ResolveAsync(provider, route, context.RequestAborted);
        if (provider is null || credential is null)
        {
            await RefuseAsync(context, StatusCodes.Status502BadGateway, "ILD holds no credential for this provider and route.");
            return;
        }

        if (!TryBuildTarget(credential.Upstream, route, rest, context.Request.QueryString, out var target))
        {
            await RefuseAsync(context, StatusCodes.Status404NotFound, "Unknown gateway route.");
            return;
        }

        var rewriteEndpointsTo = tls is not null && route == GatewayRoutes.GitHub && rest == CopilotUserPath
            ? $"https://{context.Request.Host}/{CopilotEndpointPrefix}"
            : null;
        context.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
        var error = await _forwarder.SendAsync(
            context, target.GetLeftPart(UriPartial.Authority), Client, RequestConfig, new SwapTransformer(target, credential, rewriteEndpointsTo));

        if (error != ForwarderError.None)
        {
            _logger.LogWarning(context.GetForwarderErrorFeature()?.Exception,
                "Gateway request for provider {ProviderId} to {Host} failed: {Error}", provider.Id, target.Host, error);
            if (!context.Response.HasStarted)
                await RefuseAsync(context, StatusCodes.Status502BadGateway, "The model provider could not be reached.");
        }
        else if (context.Response.StatusCode == StatusCodes.Status401Unauthorized)
        {
            source.Rejected(provider, route);
        }
    }

    private static string? PresentedToken(HttpRequest request, IModelGatewayRegistry registry)
    {
        foreach (var name in CredentialHeaders)
        {
            var value = request.Headers[name].ToString();
            if (name == "Authorization" && value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) value = value[7..];
            if (registry.Lookup(value.Trim(), out _) != GatewayTokenState.Unknown) return value.Trim();
        }
        return null;
    }

    // The agent chooses the path, never the host: scheme, host and port always come from the credential's
    // upstream (for Copilot, from an endpoint the answer named, matched strictly).
    internal static bool TryBuildTarget(Uri upstream, string route, string rest, QueryString query, out Uri target)
    {
        target = upstream;
        var root = upstream;
        if (route == GatewayRoutes.GitHub && rest.StartsWith(CopilotEndpointPrefix, StringComparison.Ordinal))
        {
            var afterPrefix = rest[CopilotEndpointPrefix.Length..].Split('/', 2);
            if (!CopilotHost().IsMatch(afterPrefix[0])) return false;
            root = new Uri($"https://{afterPrefix[0].ToLowerInvariant()}");
            rest = afterPrefix.Length > 1 ? afterPrefix[1] : string.Empty;
        }
        else if (!IsAllowedPath(route, rest))
        {
            return false;
        }

        // Kestrel has decoded the path once, so a '%' left over is double encoding (%252e%252e) or %2F, which no
        // agent CLI sends and which Uri would otherwise decode into a dot segment.
        if (rest.StartsWith('/') || rest.Any(c => c is '\\' or '#' or '?' or '@' or '%' || char.IsControl(c))
            || rest.Split('/').Any(segment => segment is ".." or "."))
            return false;

        var expectedPath = $"{root.AbsolutePath.TrimEnd('/')}/{rest}";
        var builder = new UriBuilder(root.Scheme, root.Host, root.Port)
        {
            Path = expectedPath,
            Query = query.HasValue ? query.Value![1..] : string.Empty,
        };
        target = builder.Uri;
        return string.Equals(target.Host, root.Host, StringComparison.OrdinalIgnoreCase)
            && target.Port == root.Port && target.Scheme == root.Scheme
            && string.Equals(target.AbsolutePath, expectedPath, StringComparison.Ordinal);
    }

    // What each agent CLI calls on its route. An entry ending in '/' is a prefix; any other is the exact path.
    private static readonly Dictionary<string, string[]> AllowedPaths = new()
    {
        [GatewayRoutes.GitHub] = ["copilot_internal/"],
        [GatewayRoutes.ChatGpt] = ["codex/", "wham/"],
        [GatewayRoutes.Anthropic] = ["v1/messages", "v1/messages/count_tokens", "v1/models", "v1/models/"],
        [GatewayRoutes.Upstream] =
            ["chat/completions", "completions", "responses", "messages", "messages/count_tokens", "models", "models/"],
    };

    // An upstream provider's base URL may or may not end in the API version, so the inference paths are also
    // accepted under one version segment (Pi's anthropic-messages API sends v1/messages to a bare host).
    private static readonly string[] UpstreamVersions = ["v1/", "v1beta/"];

    private static bool IsAllowedPath(string route, string rest)
    {
        if (IsAllowedUnder(route, rest)) return true;
        if (route != GatewayRoutes.Upstream) return false;
        var version = UpstreamVersions.FirstOrDefault(v => rest.StartsWith(v, StringComparison.Ordinal));
        return version is not null && IsAllowedUnder(route, rest[version.Length..]);
    }

    private static bool IsAllowedUnder(string route, string rest)
        => AllowedPaths.TryGetValue(route, out var allowed)
            && allowed.Any(entry => entry.EndsWith('/')
                ? rest.StartsWith(entry, StringComparison.Ordinal)
                : string.Equals(rest, entry, StringComparison.Ordinal));

    [GeneratedRegex(@"^[a-z0-9-]+(\.[a-z0-9-]+)*\.githubcopilot\.com\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CopilotHost();

    private static Task RefuseAsync(HttpContext context, int status, string message)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json";
        return context.Response.WriteAsJsonAsync(new { error = new { type = "ild_gateway", message } });
    }

    private sealed partial class SwapTransformer(Uri target, ModelCredential credential, string? rewriteEndpointsTo) : HttpTransformer
    {
        [GeneratedRegex(@"https://([a-z0-9.-]+\.githubcopilot\.com)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
        private static partial Regex CopilotEndpoint();

        // The model endpoints the answer names are rewritten to this gateway, which routes them by /_ep/<host>
        // (ADR-0031); so the answer has to arrive unencoded to be read.
        public override async ValueTask<bool> TransformResponseAsync(
            HttpContext httpContext, HttpResponseMessage? proxyResponse, CancellationToken cancellationToken)
        {
            if (rewriteEndpointsTo is null || proxyResponse is not { IsSuccessStatusCode: true })
                return await base.TransformResponseAsync(httpContext, proxyResponse, cancellationToken);

            await base.TransformResponseAsync(httpContext, proxyResponse, cancellationToken);
            var body = await proxyResponse.Content.ReadAsStringAsync(cancellationToken);
            var rewritten = System.Text.Encoding.UTF8.GetBytes(CopilotEndpoint().Replace(body, match => rewriteEndpointsTo + match.Groups[1].Value));
            httpContext.Response.ContentLength = rewritten.Length;
            await httpContext.Response.Body.WriteAsync(rewritten, cancellationToken);
            return false;
        }

        public override async ValueTask TransformRequestAsync(
            HttpContext httpContext, HttpRequestMessage proxyRequest, string destinationPrefix, CancellationToken cancellationToken)
        {
            await base.TransformRequestAsync(httpContext, proxyRequest, destinationPrefix, cancellationToken);
            proxyRequest.RequestUri = target;
            proxyRequest.Headers.Host = null;

            var carriedKey = false;
            foreach (var name in new[] { "x-api-key", "api-key" })
            {
                if (proxyRequest.Headers.Remove(name))
                {
                    proxyRequest.Headers.TryAddWithoutValidation(name, credential.Secret);
                    carriedKey = true;
                }
            }
            proxyRequest.Headers.Remove(TokenHeader);
            if (rewriteEndpointsTo is not null) proxyRequest.Headers.AcceptEncoding.Clear();
            if (!carriedKey || proxyRequest.Headers.Authorization is not null)
                proxyRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", credential.Secret);

            foreach (var (name, value) in credential.Headers)
            {
                proxyRequest.Headers.Remove(name);
                proxyRequest.Headers.TryAddWithoutValidation(name, value);
            }
        }
    }
}
