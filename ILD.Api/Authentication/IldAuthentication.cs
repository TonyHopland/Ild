using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;

namespace ILD.Api.Authentication;

/// <summary>
/// ILD's one authentication scheme and the vocabulary built on it.
///
/// Two principals exist and nothing else can authenticate: the <c>user</c> — the
/// single seeded operator, holding a session token from <c>/api/v1/auth/login</c>
/// — and the <c>agent</c>, ILD's own spawned MCP server presenting the
/// process-lifetime service token (see <see cref="Configuration.AgentAuthTokenProvider"/>).
/// Because those are the only two roles, "agent or user" is exactly "any
/// authenticated caller".
///
/// The fallback policy is the point of the whole arrangement: an endpoint that
/// says nothing about authorization is user-only. A coding agent that finds an
/// endpoint nobody remembered to think about is refused by default rather than
/// admitted by default, so a forgotten endpoint is a 403, not a breach.
/// </summary>
public static class IldAuthentication
{
    public const string Scheme = "ILD";

    public const string UserRole = "user";
    public const string AgentRole = "agent";

    /// <summary>
    /// Any authenticated caller, agent included. The opt-out from the user-only
    /// fallback: the agent API surface, plus the webhook routes whose external
    /// callers present an operator-configured token of either kind.
    /// </summary>
    public const string AgentOrUserPolicy = "AgentOrUser";

    /// <summary>
    /// The fallback policy under its own name, for endpoints that want to state
    /// the user-only requirement at the endpoint as well as inherit it.
    ///
    /// It has to exist, and any such endpoint has to name it: a bare
    /// <c>[Authorize]</c> is authorization metadata, and metadata suppresses the
    /// fallback policy entirely — the endpoint drops to the framework's default
    /// policy, which is "authenticated" and nothing more, and so quietly admits
    /// the agent. An attribute meant as belt-and-braces would be a downgrade.
    /// Naming this policy makes the redundant statement genuinely redundant,
    /// because it is built from the same requirements as the fallback below.
    /// </summary>
    public const string UserOnlyPolicy = "UserOnly";

    public static IServiceCollection AddIldAuthentication(this IServiceCollection services)
    {
        services.AddAuthentication(Scheme)
            .AddScheme<AuthenticationSchemeOptions, IldAuthenticationHandler>(Scheme, configureOptions: null);

        var userOnly = new AuthorizationPolicyBuilder(Scheme)
            .RequireAuthenticatedUser()
            .RequireRole(UserRole)
            .Build();

        services.AddAuthorizationBuilder()
            .SetFallbackPolicy(userOnly)
            .AddPolicy(UserOnlyPolicy, userOnly)
            .AddPolicy(AgentOrUserPolicy, policy => policy
                .AddAuthenticationSchemes(Scheme)
                .RequireAuthenticatedUser()
                .RequireRole(AgentRole, UserRole));

        return services;
    }
}
