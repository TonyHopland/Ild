using ILD.Data.Entities;
using ILD.Data.Enums;

namespace ILD.Data.Stores.Interfaces;

public interface IAuthStore
{
    Task<User?> GetByUsernameAsync(string username);
    Task<User?> GetBySessionTokenAsync(string sessionToken);
    Task<bool> ExistsBySessionTokenAsync(string sessionToken);
    Task CreateUserAsync(User user);
    Task UpdateUserAsync(User user);
}
