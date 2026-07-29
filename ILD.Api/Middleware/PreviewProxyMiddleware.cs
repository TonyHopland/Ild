using System.Net;
using System.Text;
using ILD.Core.Services.Interfaces;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Primitives;
using Yarp.ReverseProxy.Forwarder;

namespace ILD.Api.Middleware;

/// <summary>
/// Serves worktree previews on wildcard subdomains of
/// <c>ILD_PREVIEW_PROXY_BASE</c> — <c>wi-12.ild.kube</c> reaches work item 12's
/// public preview service, <c>wi-12-api.ild.kube</c> reaches its <c>api</c>
/// service — by forwarding to the loopback port the preview runtime allocated.
/// This exists because ILD in a cluster owns exactly one published port: a
/// preview's port is picked at runtime inside the container and nothing outside
/// can reach it, so <c>http://{publicHost}:{port}</c> is a URL that resolves to
/// nothing. See <c>docs/adr/0015-wildcard-subdomain-preview-routing.md</c>.
///
/// <para>
/// It is registered ahead of <see cref="AuthMiddleware"/> deliberately: a preview
/// is a foreign app that knows nothing of ILD sessions, so it cannot be asked to
/// carry an ILD token. <strong>Proxied previews are therefore unauthenticated</strong>
/// — anyone who can resolve the hostname can reach the running service and
/// whatever a repository's preview env put in it. Leaving
/// <c>ILD_PREVIEW_PROXY_BASE</c> unset is the opt-out, and this middleware then
/// forwards nothing at all.
/// </para>
///
/// <para>
/// The single most important property here is that non-preview traffic is
/// untouched. A request only enters this path when its <c>Host</c> is
/// <c>&lt;label&gt;.&lt;base host&gt;</c>; the apex host serving the ILD UI does
/// not match, and neither does anything else.
/// </para>
/// </summary>
public sealed class PreviewProxyMiddleware
{
    // One invoker for the process, per YARP guidance. Redirects, cookies and
    // decompression are all left to the browser: this is a transparent hop, and
    // decompressing here would break streaming responses.
    private static readonly HttpMessageInvoker Client = new(new SocketsHttpHandler
    {
        UseProxy = false,
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.None,
        UseCookies = false,
        ActivityHeadersPropagator = null,
        ConnectTimeout = TimeSpan.FromSeconds(10),
    });

    // Generous: previews stream (SSE, long-poll, dev-server event channels) and a
    // cold dev server can take a while to answer its first request.
    private static readonly ForwarderRequestConfig RequestConfig = new()
    {
        ActivityTimeout = TimeSpan.FromMinutes(10),
        Version = HttpVersion.Version11,
        VersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
    };

    private readonly RequestDelegate _next;
    private readonly IHttpForwarder _forwarder;
    private readonly PreviewProxyBase _proxyBase;
    private readonly ILogger<PreviewProxyMiddleware> _logger;

    public PreviewProxyMiddleware(
        RequestDelegate next,
        IHttpForwarder forwarder,
        PreviewProxyBase proxyBase,
        ILogger<PreviewProxyMiddleware> logger)
    {
        _next = next;
        _forwarder = forwarder;
        _proxyBase = proxyBase;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (!_proxyBase.TryGetHostLabel(context.Request.Host.Host, out var hostLabel))
        {
            await _next(context);
            return;
        }

        // Resolved here rather than as InvokeAsync parameters: this middleware sits
        // near the front of the pipeline, so those would be constructed for every
        // API, SPA and static request — and for every request at all when the
        // feature is off — and IWorkItemManager drags in the DbContext-backed run
        // store behind it.
        var previews = context.RequestServices.GetRequiredService<IWorktreePreviewService>();
        var workItems = context.RequestServices.GetRequiredService<IWorkItemManager>();

        var target = await previews.ResolvePreviewTargetAsync(hostLabel, workItems, context.RequestAborted);
        if (!target.IsResolved)
        {
            await WriteUnavailablePageAsync(context, target);
            return;
        }

        // Previews stream: server-sent events, dev-server HMR channels, chunked
        // responses. Buffering any of those turns a live page into a hang.
        context.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();

        var transformer = new PreviewProxyTransformer(target.Port, target.RewriteHost, _proxyBase.Scheme);
        var error = await _forwarder.SendAsync(
            context,
            $"http://127.0.0.1:{target.Port}",
            Client,
            RequestConfig,
            transformer);

        if (error != ForwarderError.None && !context.Response.HasStarted)
        {
            var exception = context.GetForwarderErrorFeature()?.Exception;
            _logger.LogWarning(
                exception,
                "Preview proxy for {HostLabel} -> 127.0.0.1:{Port} failed: {Error}",
                hostLabel, target.Port, error);

            context.Response.StatusCode = (int)HttpStatusCode.BadGateway;
            await WriteHtmlAsync(
                context,
                "Preview unreachable",
                $"The preview service '{target.ServiceName}' is registered on port {target.Port} but did not answer. "
                + "It may have crashed — check its log in the work item's Preview tab.");
        }
    }

    /// <summary>
    /// Renders the four ways a preview hostname can fail to point at anything.
    /// A human typed this hostname into a browser, so they get a page saying what
    /// to do next rather than a bare status code.
    /// </summary>
    private static Task WriteUnavailablePageAsync(HttpContext context, PreviewTarget target)
    {
        context.Response.StatusCode = (int)(target.Outcome switch
        {
            PreviewTargetOutcome.NotAPreviewHost => HttpStatusCode.NotFound,
            PreviewTargetOutcome.UnknownWorkItem => HttpStatusCode.NotFound,
            // Nothing is down — the hostname just does not name one service.
            PreviewTargetOutcome.AmbiguousService => HttpStatusCode.NotFound,
            _ => HttpStatusCode.ServiceUnavailable,
        });

        var title = target.Outcome switch
        {
            PreviewTargetOutcome.NotAPreviewHost => "Not a preview hostname",
            PreviewTargetOutcome.UnknownWorkItem => "Unknown work item",
            PreviewTargetOutcome.NoWorktree => "No worktree yet",
            PreviewTargetOutcome.PreviewNotRunning => "Preview not running",
            PreviewTargetOutcome.AmbiguousService => "More than one public service",
            _ => "Preview service not running",
        };

        return WriteHtmlAsync(context, title, target.Message);
    }

    private static Task WriteHtmlAsync(HttpContext context, string title, string message)
    {
        context.Response.ContentType = "text/html; charset=utf-8";
        var html = $"""
            <!doctype html>
            <html lang="en"><head><meta charset="utf-8"><title>{WebUtility.HtmlEncode(title)}</title></head>
            <body style="font-family:system-ui,sans-serif;max-width:40rem;margin:4rem auto;padding:0 1rem;line-height:1.5">
            <h1 style="font-size:1.25rem">{WebUtility.HtmlEncode(title)}</h1>
            <p>{WebUtility.HtmlEncode(message)}</p>
            <p style="color:#666;font-size:.875rem">ILD worktree preview</p>
            </body></html>
            """;
        return context.Response.WriteAsync(html, Encoding.UTF8);
    }

    /// <summary>
    /// The proxy hygiene a preview needs to behave like it was served directly:
    /// forwarded-for/proto/host on the way in, and on the way back the loopback
    /// authority scrubbed out of anything the browser will act on.
    ///
    /// <para>
    /// The public scheme comes from <see cref="PreviewProxyBase.Scheme"/>, never
    /// from <c>HttpRequest.Scheme</c>. ILD listens on plain HTTP
    /// (<c>ASPNETCORE_URLS=http://+:8080</c>) and does not run
    /// <c>UseForwardedHeaders</c>, so behind a TLS-terminating ingress the request
    /// this middleware sees is always <c>http</c> however the browser reached it.
    /// Trusting it would downgrade every rewritten <c>Location</c> to <c>http</c>,
    /// tell the preview the wrong protocol, and strip <c>Secure</c> from cookies
    /// that were served over TLS. The configured base is what the browser actually
    /// used, and unlike a request header it cannot be spoofed.
    /// </para>
    /// </summary>
    private sealed class PreviewProxyTransformer : HttpTransformer
    {
        private readonly int _port;
        private readonly bool _rewriteHost;
        private readonly string _publicScheme;

        public PreviewProxyTransformer(int port, bool rewriteHost, string publicScheme)
        {
            _port = port;
            _rewriteHost = rewriteHost;
            _publicScheme = publicScheme;
        }

        private bool IsPublicSchemeSecure
            => string.Equals(_publicScheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);

        public override async ValueTask TransformRequestAsync(
            HttpContext httpContext,
            HttpRequestMessage proxyRequest,
            string destinationPrefix,
            CancellationToken cancellationToken)
        {
            await base.TransformRequestAsync(httpContext, proxyRequest, destinationPrefix, cancellationToken);

            var request = httpContext.Request;

            // The base transform copies the inbound Host verbatim. Clearing it makes
            // the request go out with the destination's authority, which is what
            // host-checking dev servers (Vite, webpack-dev-server) require; a service
            // that opts out keeps the real browser-facing host instead.
            proxyRequest.Headers.Host = _rewriteHost ? null : request.Host.Value;

            // Whatever the client claimed about forwarding is not evidence; replace
            // it with what this hop actually observed.
            var forwardedFor = proxyRequest.Headers.TryGetValues("X-Forwarded-For", out var existing)
                ? string.Join(", ", existing)
                : null;
            proxyRequest.Headers.Remove("X-Forwarded-For");
            proxyRequest.Headers.Remove("X-Forwarded-Proto");
            proxyRequest.Headers.Remove("X-Forwarded-Host");

            var remoteIp = httpContext.Connection.RemoteIpAddress?.ToString();
            if (remoteIp != null)
                forwardedFor = forwardedFor == null ? remoteIp : $"{forwardedFor}, {remoteIp}";
            if (forwardedFor != null)
                proxyRequest.Headers.TryAddWithoutValidation("X-Forwarded-For", forwardedFor);

            proxyRequest.Headers.TryAddWithoutValidation("X-Forwarded-Proto", _publicScheme);
            if (request.Host.HasValue)
                proxyRequest.Headers.TryAddWithoutValidation("X-Forwarded-Host", request.Host.Value);
        }

        public override async ValueTask<bool> TransformResponseAsync(
            HttpContext httpContext,
            HttpResponseMessage? proxyResponse,
            CancellationToken cancellationToken)
        {
            var shouldProxy = await base.TransformResponseAsync(httpContext, proxyResponse, cancellationToken);
            if (!shouldProxy || proxyResponse == null)
                return shouldProxy;

            RewriteLocation(httpContext);
            RewriteSetCookie(httpContext);
            return true;
        }

        /// <summary>
        /// A service that had its Host rewritten builds absolute redirects against
        /// <c>127.0.0.1:{port}</c> — a URL the browser cannot reach. Point those back
        /// at the preview hostname, keeping path, query and fragment. Relative
        /// Locations, and absolute ones pointing somewhere else entirely (an OAuth
        /// provider, say), are left alone.
        /// </summary>
        private void RewriteLocation(HttpContext httpContext)
        {
            var headers = httpContext.Response.Headers;
            if (!headers.TryGetValue("Location", out var values) || values.Count == 0)
                return;

            var rewritten = new string[values.Count];
            var changed = false;
            for (var i = 0; i < values.Count; i++)
            {
                var value = values[i];
                if (value != null
                    && Uri.TryCreate(value, UriKind.Absolute, out var uri)
                    && IsUpstreamAuthority(uri))
                {
                    // The host the browser used, on the scheme the base declares —
                    // the request's own scheme is the in-container one.
                    rewritten[i] = $"{_publicScheme}://{httpContext.Request.Host.Value}{uri.PathAndQuery}{uri.Fragment}";
                    changed = true;
                }
                else
                {
                    rewritten[i] = value!;
                }
            }

            if (changed)
                headers["Location"] = new StringValues(rewritten);
        }

        private bool IsUpstreamAuthority(Uri uri)
            => uri.Port == _port
                && (uri.Host is "127.0.0.1" or "localhost" or "0.0.0.0" or "[::1]" or "::1");

        /// <summary>
        /// Cookies are set by an app that thinks it lives on <c>127.0.0.1</c>, so a
        /// <c>Domain</c> attribute it emits either pins the cookie to the wrong host
        /// or is rejected outright; dropping it binds the cookie to the preview
        /// hostname, which is what is wanted. <c>Path</c> is left as-is — the proxy
        /// mounts the service at the root, so its paths are already correct.
        /// <c>Secure</c> is dropped on a plain-http preview (and <c>SameSite=None</c>
        /// downgraded with it, since browsers reject that pair without Secure),
        /// otherwise every cookie a preview sets would be silently discarded. That
        /// judgement is made against the public scheme, not the request's: behind a
        /// TLS-terminating ingress the request is http and stripping <c>Secure</c>
        /// would weaken a cookie the browser did receive over TLS.
        /// </summary>
        private void RewriteSetCookie(HttpContext httpContext)
        {
            var headers = httpContext.Response.Headers;
            if (!headers.TryGetValue("Set-Cookie", out var values) || values.Count == 0)
                return;

            var isSecureRequest = IsPublicSchemeSecure;
            var rewritten = new string[values.Count];
            var changed = false;

            for (var i = 0; i < values.Count; i++)
            {
                var original = values[i];
                if (original == null)
                {
                    rewritten[i] = original!;
                    continue;
                }

                var attributes = original.Split(';');
                var kept = new List<string>(attributes.Length);
                var strippedSecure = false;

                foreach (var attribute in attributes)
                {
                    var trimmed = attribute.Trim();
                    if (trimmed.StartsWith("Domain=", StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (!isSecureRequest && trimmed.Equals("Secure", StringComparison.OrdinalIgnoreCase))
                    {
                        strippedSecure = true;
                        continue;
                    }
                    kept.Add(trimmed);
                }

                if (strippedSecure)
                {
                    for (var a = 0; a < kept.Count; a++)
                    {
                        if (kept[a].Equals("SameSite=None", StringComparison.OrdinalIgnoreCase))
                            kept[a] = "SameSite=Lax";
                    }
                }

                rewritten[i] = string.Join("; ", kept);
                changed |= !string.Equals(rewritten[i], original, StringComparison.Ordinal);
            }

            if (changed)
                headers["Set-Cookie"] = new StringValues(rewritten);
        }
    }
}
