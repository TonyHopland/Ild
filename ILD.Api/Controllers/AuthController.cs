using ILD.Core.Services.Interfaces;
using ILD.Core.DTOs;
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

    [HttpPost("login")]
    public async Task<IActionResult> Login([FromBody] LoginRequest request)
    {
        if (!ModelState.IsValid)
            return BadRequest(ModelState);

        try
        {
            var result = await _authService.LoginAsync(request.Username, request.Password);
            if (!result.Success || string.IsNullOrEmpty(result.SessionToken))
            {
                return Unauthorized(new { error = result.ErrorMessage ?? "Invalid username or password" });
            }
            return Ok(new LoginResponse(result.SessionToken, result.Username));
        }
        catch
        {
            return Unauthorized(new { error = "Invalid username or password" });
        }
    }

    [HttpPost("logout")]
    public async Task<IActionResult> Logout()
    {
        var token = Request.Headers.Authorization.ToString()
            ?.Replace("Bearer ", "", StringComparison.OrdinalIgnoreCase)
            ?.Trim();

        if (string.IsNullOrEmpty(token))
            return Unauthorized();

        await _authService.LogoutAsync(token);
        return NoContent();
    }

    [HttpGet("me")]
    public IActionResult Me()
    {
        var username = HttpContext.Items["Username"] as string;
        if (string.IsNullOrEmpty(username))
            return Unauthorized();

        return Ok(new { id = username, username, email = string.Empty, role = "admin" });
    }
}
