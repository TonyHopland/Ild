using ILD.Core.DTOs;
using ILD.Core.Enums;
using ILD.Core.Models;
using System.Net;
using System.Text.Json;
using ILD.Core.Services.Interfaces;

namespace ILD.Api.Middleware;

public class AuthMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IAuthService _authService;
    private readonly AuthOptions _options;

    public AuthMiddleware(RequestDelegate next, IAuthService authService, AuthOptions options)
    {
        _next = next;
        _authService = authService;
        _options = options;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (IsExcludedPath(context.Request.Path))
        {
            await _next(context);
            return;
        }

        var token = ExtractToken(context);

        if (string.IsNullOrWhiteSpace(token))
        {
            context.Response.StatusCode = (int)HttpStatusCode.Unauthorized;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(
                JsonSerializer.Serialize(new { error = "Unauthorized", message = "No authentication token provided" }));
            return;
        }

        var isValid = await _authService.ValidateSessionAsync(token);

        if (!isValid)
        {
            context.Response.StatusCode = (int)HttpStatusCode.Unauthorized;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(
                JsonSerializer.Serialize(new { error = "Unauthorized", message = "Invalid or expired session" }));
            return;
        }

        context.Items["SessionId"] = token;
        var username = await _authService.GetUsernameAsync(token);
        if (!string.IsNullOrEmpty(username))
        {
            context.Items["Username"] = username;
        }

        await _next(context);
    }

    private bool IsExcludedPath(PathString path)
    {
        var pathValue = path.Value?.ToLowerInvariant() ?? string.Empty;

        foreach (var excluded in _options.ExcludedPaths)
        {
            if (pathValue.StartsWith(excluded, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        if (_options.AllowStaticFiles && IsStaticFile(pathValue))
        {
            return true;
        }

        return false;
    }

    private bool IsStaticFile(string path)
    {
        var extension = Path.GetExtension(path);

        return extension switch
        {
            ".css" or ".js" or ".png" or ".jpg" or ".jpeg" or ".gif" or
            ".ico" or ".svg" or ".woff" or ".woff2" or ".ttf" or ".eot" or
            ".map" => true,
            _ => false
        };
    }

    private static string? ExtractToken(HttpContext context)
    {
        var authHeader = context.Request.Headers.Authorization.FirstOrDefault();

        if (!string.IsNullOrWhiteSpace(authHeader))
        {
            if (authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                return authHeader["Bearer ".Length..].Trim();
            }

            return authHeader.Trim();
        }

        var queryToken = context.Request.Query["token"].FirstOrDefault();

        return string.IsNullOrWhiteSpace(queryToken) ? null : queryToken;
    }
}
