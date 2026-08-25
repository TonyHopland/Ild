using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using ILD.Api.Configuration;
using ILD.Core.Services.Interfaces;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace ILD.Api.Authentication;

/// <summary>
/// Turns an ILD bearer token into a <see cref="ClaimsPrincipal"/>: the agent
/// service token yields the <c>agent</c> role, a live session token yields the
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
/// no other way to authenticate.
/// </summary>
public sealed class IldAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    private const string NoToken = "No authentication token provided";
    private const string InvalidSession = "Invalid or expired session";

    private readonly IAuthService _authService;
    private readonly AgentAuthTokenProvider _agentTokens;

    public IldAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        IAuthService authService,
        AgentAuthTokenProvider agentTokens)
        : base(options, logger, encoder)
    {
        _authService = authService;
        _agentTokens = agentTokens;
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var token = ExtractToken();

        if (string.IsNullOrWhiteSpace(token))
            return AuthenticateResult.NoResult();

        // Agent service token: lets the in-host MCP server (and other ILD-spawned
        // agents) call /api/v1/agent/... without going through user login.
        if (_agentTokens.Matches(token))
            return Ticket(IldAuthentication.AgentRole, name: "agent");

        if (!await _authService.ValidateSessionAsync(token))
            return AuthenticateResult.Fail(InvalidSession);

        // A session with no resolvable username stays authenticated but nameless,
        // which is what the endpoints that need a user identity check for.
        return Ticket(IldAuthentication.UserRole, name: await _authService.GetUsernameAsync(token));
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

    private AuthenticateResult Ticket(string role, string? name)
    {
        var claims = new List<Claim> { new(ClaimTypes.Role, role) };
        if (!string.IsNullOrEmpty(name))
            claims.Add(new Claim(ClaimTypes.Name, name));

        var identity = new ClaimsIdentity(claims, Scheme.Name, ClaimTypes.Name, ClaimTypes.Role);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name));
    }

    private string? ExtractToken()
    {
        var authHeader = Request.Headers.Authorization.FirstOrDefault();

        // A Basic credential is never an ILD token: it is how an Azure DevOps
        // service hook authenticates itself to the webhook adapter, which owns
        // that check. Falling through to the query token is what lets such a
        // caller carry both credentials at once.
        if (!string.IsNullOrWhiteSpace(authHeader)
            && !authHeader.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            return authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                ? authHeader["Bearer ".Length..].Trim()
                : authHeader.Trim();
        }

        return Request.Query.TryGetValue("access_token", out var queryToken)
            ? queryToken.ToString()
            : null;
    }
}
