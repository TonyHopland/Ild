using ILD.Core.Services.Interfaces;
using ILD.Core.DTOs;
using ILD.Core.Enums;
using ILD.Core.Models;
namespace ILD.Api.Middleware;

public class AuthOptions
{
    public List<string> ExcludedPaths { get; set; } = new()
    {
        "/api/v1/auth/login",
        "/api/v1/health",
        "/metrics"
    };

    public bool AllowStaticFiles { get; set; } = true;
}
