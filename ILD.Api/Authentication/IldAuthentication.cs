using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;

namespace ILD.Api.Authentication;

/// <summary>
/// ILD's one authentication scheme and the vocabulary built on it.
///
/// Three principals exist and nothing else can authenticate: the <c>user</c> — the
/// single seeded operator, holding a session token from <c>/api/v1/auth/login</c>
/// — the <c>agent</c>, ILD's own spawned MCP server presenting the
/// per-launch token ILD issues to each run and chat it starts (see <see cref="ILD.Core.Services.Interfaces.IAgentTokenService"/>),
/// and the <c>external-agent</c>, an AI session outside ILD presenting the token of a temporary
/// grant the operator created (see <see cref="ILD.Core.Services.Interfaces.IExternalAgentGrantService"/>).
/// "Agent or user" is any caller ILD itself launched or the operator; an external agent is admitted
/// only by <see cref="AgentSurfacePolicy"/>.
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

    /// <summary>An AI session outside ILD holding an access grant (ADR-0037). Never the operator, and never admitted outside <see cref="AgentSurfacePolicy"/>.</summary>
    public const string ExternalAgentRole = "external-agent";

    /// <summary>The claim naming what an agent token was issued for: <c>run</c> or <c>chat</c> (later a grant).</summary>
    public const string SessionSourceClaim = "ild:session-source";

    /// <summary>The claim carrying the id of the run or chat the agent token was issued for.</summary>
    public const string SessionIdClaim = "ild:session-id";

    /// <summary>The claim carrying the scope of a grant: <c>Full</c> or <c>ReadOnly</c>.</summary>
    public const string GrantScopeClaim = "ild:grant-scope";

    /// <summary>
    /// Any authenticated caller, agent included. The opt-out from the user-only
    /// fallback: the agent API surface, plus the webhook routes whose external
    /// callers present an operator-configured token of either kind.
    /// </summary>
    public const string AgentOrUserPolicy = "AgentOrUser";

    /// <summary>
    /// What the agent API (<c>/api/v1/agent/*</c>) and the MCP endpoint admit: the user, ILD's own agents
    /// and external-agent grants. The only policy that admits <see cref="ExternalAgentRole"/>.
    /// </summary>
    public const string AgentSurfacePolicy = "AgentSurface";

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
                .RequireRole(AgentRole, UserRole))
            .AddPolicy(AgentSurfacePolicy, policy => policy
                .AddAuthenticationSchemes(Scheme)
                .RequireAuthenticatedUser()
                .RequireRole(AgentRole, UserRole, ExternalAgentRole));

        return services;
    }
}
