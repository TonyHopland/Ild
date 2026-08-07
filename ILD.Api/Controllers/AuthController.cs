using ILD.Core.Services.Interfaces;
using ILD.Data.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ILD.Api.Controllers;

[ApiController]
[Route("api/v1/[controller]")]
public class AuthController : ControllerBase
{
    private readonly IAuthService _authService;

    public AuthController(IAuthService authService)
    {
        _authService = authService;
    }

    // The one endpoint that mints the token everything else demands.
    [AllowAnonymous]
    [HttpPost("login")]
    public async Task<IActionResult> Login([FromBody] LoginRequest request)
    {
        if (!ModelState.IsValid)
            return BadRequest(ModelState);

        try
        {
            // The user agent and caller address are labels for the
            // active-sessions list, so the operator can tell one device from
            // another; nothing authenticates against them.
            var result = await _authService.LoginAsync(
                request.Username,
                request.Password,
                Request.Headers.UserAgent.ToString(),
                HttpContext.Connection.RemoteIpAddress?.ToString());

            if (!result.Success || string.IsNullOrEmpty(result.SessionToken))
            {
                return Unauthorized(new { error = result.ErrorMessage ?? "Invalid username or password" });
            }
            return Ok(new LoginResponse(result.SessionToken, result.Username, result.ExpiresAt));
        }
        catch
        {
            return Unauthorized(new { error = "Invalid username or password" });
        }
    }

    [HttpPost("logout")]
    public async Task<IActionResult> Logout()
    {
        var token = BearerToken();
        if (string.IsNullOrEmpty(token))
            return Unauthorized();

        await _authService.LogoutAsync(token);
        return NoContent();
    }

    [HttpGet("me")]
    public IActionResult Me()
    {
        var username = User.Identity?.Name;
        if (string.IsNullOrEmpty(username))
            return Unauthorized();

        return Ok(new { id = username, username, email = string.Empty, role = "admin" });
    }

    /// <summary>
    /// The caller's own live sign-ins. Carries no token or token hash — a
    /// session is addressed by its id, which is useless as a credential.
    /// </summary>
    [HttpGet("sessions")]
    public async Task<IActionResult> Sessions()
    {
        var token = BearerToken();
        if (string.IsNullOrEmpty(token))
            return Unauthorized();

        return Ok(await _authService.GetSessionsAsync(token));
    }

    /// <summary>Signs one other device out. Revoking your own session is a logout.</summary>
    [HttpDelete("sessions/{id:guid}")]
    public async Task<IActionResult> RevokeSession(Guid id)
    {
        var token = BearerToken();
        if (string.IsNullOrEmpty(token))
            return Unauthorized();

        return await _authService.RevokeSessionAsync(token, id) ? NoContent() : NotFound();
    }

    /// <summary>
    /// Signs out everywhere else, keeping the calling session — revoking it too
    /// would just log the operator out of the page they pressed the button on.
    /// </summary>
    [HttpPost("sessions/revoke-others")]
    public async Task<IActionResult> RevokeOtherSessions()
    {
        var token = BearerToken();
        if (string.IsNullOrEmpty(token))
            return Unauthorized();

        return Ok(new { revoked = await _authService.RevokeOtherSessionsAsync(token) });
    }

    /// <summary>
    /// The raw session token behind this request. The principal carries a
    /// username but not the token, and revoking "this session" needs the token
    /// itself — so read it back the same way
    /// <c>IldAuthenticationHandler</c> did.
    /// </summary>
    private string? BearerToken()
    {
        var header = Request.Headers.Authorization.FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(header))
        {
            return header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                ? header["Bearer ".Length..].Trim()
                : header.Trim();
        }

        return Request.Query.TryGetValue("access_token", out var queryToken)
            ? queryToken.ToString()
            : null;
    }
}
