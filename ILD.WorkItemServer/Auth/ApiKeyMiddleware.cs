using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace ILD.WorkItemServer.Auth;

/// <summary>
/// Bearer-token middleware. Compares supplied API key against the configured
/// allow-list using <see cref="CryptographicOperations.FixedTimeEquals"/> to
/// guard against timing attacks. Health endpoint is exempt so container
/// probes don't need credentials.
/// </summary>
public sealed class ApiKeyMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IOptionsMonitor<ApiKeyOptions> _opts;

    public ApiKeyMiddleware(RequestDelegate next, IOptionsMonitor<ApiKeyOptions> opts)
    {
        _next = next;
        _opts = opts;
    }

    public async Task InvokeAsync(HttpContext ctx)
    {
        var path = ctx.Request.Path.Value ?? string.Empty;
        if (path.Equals("/health", StringComparison.OrdinalIgnoreCase))
        {
            await _next(ctx);
            return;
        }

        var keys = _opts.CurrentValue.ParseKeys();
        if (keys.Count == 0)
        {
            ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            await ctx.Response.WriteAsync("API keys not configured");
            return;
        }

        var supplied = ExtractKey(ctx.Request);
        if (supplied == null || !KeyMatches(keys, supplied))
        {
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        await _next(ctx);
    }

    private static string? ExtractKey(HttpRequest req)
    {
        if (req.Headers.TryGetValue("Authorization", out var auth))
        {
            var v = auth.ToString();
            const string prefix = "Bearer ";
            if (v.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return v.Substring(prefix.Length).Trim();
        }
        if (req.Headers.TryGetValue("X-Api-Key", out var apiKey))
            return apiKey.ToString().Trim();
        return null;
    }

    private static bool KeyMatches(IReadOnlySet<string> allowed, string supplied)
    {
        var s = Encoding.UTF8.GetBytes(supplied);
        foreach (var k in allowed)
        {
            var expected = Encoding.UTF8.GetBytes(k);
            if (expected.Length == s.Length && CryptographicOperations.FixedTimeEquals(expected, s))
                return true;
        }
        return false;
    }
}
