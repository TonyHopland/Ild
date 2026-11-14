using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

namespace ILD.Api.Authentication;

/// <summary>
/// The per-grant request budget: every grant gets the same fixed window of its own, so one runaway agent
/// cannot starve the operator or another grant. Nobody else is limited.
/// </summary>
public static class ExternalAgentRateLimit
{
    public const string EnvVar = "ILD_EXTERNAL_AGENT_REQUESTS_PER_MINUTE";
    public const int DefaultRequestsPerMinute = 300;

    public static int ParseRequestsPerMinute(string? value)
        => int.TryParse(value, out var parsed) && parsed > 0 ? parsed : DefaultRequestsPerMinute;

    public static IServiceCollection AddExternalAgentRateLimit(this IServiceCollection services)
    {
        services.AddRateLimiter(_ => { });
        services.AddOptions<RateLimiterOptions>().Configure<IConfiguration>((options, configuration) =>
        {
            var requestsPerMinute = ParseRequestsPerMinute(Environment.GetEnvironmentVariable(EnvVar) ?? configuration[EnvVar]);
            options.OnRejected = async (rejected, ct) =>
            {
                var response = rejected.HttpContext.Response;
                response.StatusCode = StatusCodes.Status429TooManyRequests;
                var retryAfter = rejected.Lease.TryGetMetadata(MetadataName.RetryAfter, out var wait) ? (int)Math.Ceiling(wait.TotalSeconds) : 60;
                response.Headers.RetryAfter = retryAfter.ToString(System.Globalization.CultureInfo.InvariantCulture);
                await response.WriteAsJsonAsync(new
                {
                    error = "Too Many Requests",
                    message = $"This grant has used its {requestsPerMinute} requests per minute. Retry in {retryAfter} seconds.",
                    retryAfterSeconds = retryAfter,
                }, ct);
            };
            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
                context.User.IsInRole(IldAuthentication.ExternalAgentRole)
                    ? RateLimitPartition.GetFixedWindowLimiter(
                        context.User.FindFirstValue(IldAuthentication.SessionIdClaim) ?? string.Empty,
                        _ => new FixedWindowRateLimiterOptions
                        {
                            PermitLimit = requestsPerMinute,
                            Window = TimeSpan.FromMinutes(1),
                            QueueLimit = 0,
                        })
                    : RateLimitPartition.GetNoLimiter(string.Empty));
        });
        return services;
    }
}
