using ILD.Core.DTOs;
using Microsoft.Extensions.Logging;
using ILD.Core.Enums;
using ILD.Core.Models;
using ILD.Core.Services.Interfaces;

namespace ILD.Core.Services.Implementations;

public class AuthService : IAuthService
{
    private readonly ILogger<AuthService> _logger;
    private readonly AppDbContext _dbContext;

    public AuthService(ILogger<AuthService> logger, AppDbContext dbContext)
    {
        _logger = logger;
        _dbContext = dbContext;
    }

    public Task<AuthResult> LoginAsync(string username, string password)
    {
        throw new NotImplementedException(nameof(LoginAsync));
    }

    public Task LogoutAsync(string sessionId)
    {
        throw new NotImplementedException(nameof(LogoutAsync));
    }

    public Task<bool> ValidateSessionAsync(string sessionId)
    {
        throw new NotImplementedException(nameof(ValidateSessionAsync));
    }

    public Task<string?> GetUsernameAsync(string sessionId)
    {
        throw new NotImplementedException(nameof(GetUsernameAsync));
    }

    public Task<string> GenerateApiKeyAsync(string description)
    {
        throw new NotImplementedException(nameof(GenerateApiKeyAsync));
    }

    public Task RevokeApiKeyAsync(string apiKey)
    {
        throw new NotImplementedException(nameof(RevokeApiKeyAsync));
    }

    public Task<IEnumerable<ApiKeyRecord>> GetApiKeysAsync()
    {
        throw new NotImplementedException(nameof(GetApiKeysAsync));
    }
}
