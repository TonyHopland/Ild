using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using ILD.Core.Services.Implementations;
using ILD.Core.Services.Interfaces;
using ILD.Data.Entities;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace ILD.Api.Authentication;

/// <summary>
/// Turns an ILD bearer token into a <see cref="ClaimsPrincipal"/>: a live agent token yields the <c>agent</c> role with its run or chat as claims, a live grant token yields the
/// <c>external-agent</c> role with the grant as claims, a live session token yields the
/// <c>user</c> role named after its owner. Authorization is then the framework's
/// job — see <see cref="IldAuthentication"/> for the policies.
///
/// The token is read from <c>Authorization: Bearer &lt;token&gt;</c>, from a bare
/// <c>Authorization</c> value, or from an <c>?access_token=</c> query parameter.
/// A <c>Basic</c> Authorization header is skipped rather than read as a token:
/// it belongs to a webhook adapter's own verification, and the caller carries
/// its ILD token in the query instead.
/// The query form is not decoration: browsers cannot set headers on a WebSocket
/// handshake, so the interactive terminals and SignalR's WebSocket transport have
/// no other way to authenticate. A grant token is never read from the query: URLs
/// end up in logs and browser history, so a grant token sent there is refused.
/// </summary>
public sealed class IldAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    private const string NoToken = "No authentication token provided";
    private const string InvalidSession = "Invalid or expired session";
    private const string GrantTokenInQuery = "A grant token is accepted only in the Authorization header";

    private readonly IAuthService _authService;
    private readonly IAgentTokenService _agentTokens;
    private readonly IExternalAgentGrantService _grants;

    public IldAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        IAuthService authService,
        IAgentTokenService agentTokens,
        IExternalAgentGrantService grants)
        : base(options, logger, encoder)
    {
        _authService = authService;
        _agentTokens = agentTokens;
        _grants = grants;
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var (token, fromQuery) = ExtractToken();

        if (string.IsNullOrWhiteSpace(token))
            return AuthenticateResult.NoResult();

        // A token ILD issued to one agent launch: it names the run or chat it belongs to, and stops
        // authenticating the moment that launch ends.
        if (await _agentTokens.AuthenticateAsync(token, Context.RequestAborted) is { } agent)
            return Ticket(IldAuthentication.AgentRole, name: "agent", agent);

        if (IsGrantToken(token))
        {
            if (fromQuery)
                return AuthenticateResult.Fail(GrantTokenInQuery);
            if (await _grants.AuthenticateAsync(token, Context.RequestAborted) is { } grant)
                return GrantTicket(grant);
        }

        if (await _authService.AuthenticateSessionAsync(token) is not { } session)
            return AuthenticateResult.Fail(InvalidSession);

        // A session with no resolvable username stays authenticated but nameless,
        // which is what the endpoints that need a user identity check for.
        return Ticket(IldAuthentication.UserRole, name: session.Username);
    }

    /// <summary>
    /// Keeps the 401 body the shape every client already parses:
    /// <c>{"error":"Unauthorized","message":"..."}</c>.
    /// </summary>
    protected override async Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        var failure = (await HandleAuthenticateOnceAsync()).Failure;

        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.ContentType = "application/json";
        await Response.WriteAsync(JsonSerializer.Serialize(new
        {
            error = "Unauthorized",
            message = failure?.Message ?? NoToken,
        }));
    }

    private static bool IsGrantToken(string token) => token.StartsWith(ExternalAgentGrantService.TokenPrefix, StringComparison.Ordinal);

    private AuthenticateResult GrantTicket(ExternalAgentGrantIdentity grant)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.Role, IldAuthentication.ExternalAgentRole),
            new(ClaimTypes.Name, grant.Name),
            new(IldAuthentication.SessionSourceClaim, AgentTokenSource.Grant.ToString().ToLowerInvariant()),
            new(IldAuthentication.SessionIdClaim, grant.Id.ToString()),
            new(IldAuthentication.GrantScopeClaim, grant.Scope.ToString()),
        };
        var identity = new ClaimsIdentity(claims, Scheme.Name, ClaimTypes.Name, ClaimTypes.Role);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name));
    }

    private AuthenticateResult Ticket(string role, string? name, AgentTokenIdentity? agent = null)
    {
        var claims = new List<Claim> { new(ClaimTypes.Role, role) };
        if (!string.IsNullOrEmpty(name))
            claims.Add(new Claim(ClaimTypes.Name, name));
        if (agent is not null)
        {
            claims.Add(new Claim(IldAuthentication.SessionSourceClaim, agent.Source.ToString().ToLowerInvariant()));
            claims.Add(new Claim(IldAuthentication.SessionIdClaim, agent.SourceId.ToString()));
        }

        var identity = new ClaimsIdentity(claims, Scheme.Name, ClaimTypes.Name, ClaimTypes.Role);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name));
    }

    private (string? Token, bool FromQuery) ExtractToken()
    {
        var authHeader = Request.Headers.Authorization.FirstOrDefault();

        // A Basic credential is never an ILD token: it is how an Azure DevOps
        // service hook authenticates itself to the webhook adapter, which owns
        // that check. Falling through to the query token is what lets such a
        // caller carry both credentials at once.
        if (!string.IsNullOrWhiteSpace(authHeader)
            && !authHeader.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            return (authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                ? authHeader["Bearer ".Length..].Trim()
                : authHeader.Trim(), false);
        }

        return Request.Query.TryGetValue("access_token", out var queryToken)
            ? (queryToken.ToString(), true)
            : (null, false);
    }
}
