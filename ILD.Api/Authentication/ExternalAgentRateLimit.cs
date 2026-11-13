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
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
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
