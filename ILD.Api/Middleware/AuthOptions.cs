using ILD.Core.Services.Interfaces;
using ILD.Data.DTOs;
using ILD.Data.Enums;
using ILD.Data.Entities;
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
