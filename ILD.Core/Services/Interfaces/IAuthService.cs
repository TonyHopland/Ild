using ILD.Core.DTOs;
using Microsoft.Extensions.Logging;
using ILD.Core.Enums;
using ILD.Core.Models;
namespace ILD.Core.Services.Interfaces;

public interface IAuthService
{
    Task<AuthResult> LoginAsync(string username, string password);
    Task LogoutAsync(string sessionId);
    Task<bool> ValidateSessionAsync(string sessionId);
    Task<string?> GetUsernameAsync(string sessionId);
    Task<string> GenerateApiKeyAsync(string description);
    Task RevokeApiKeyAsync(string apiKey);
    Task<IEnumerable<ApiKeyRecord>> GetApiKeysAsync();
}
