using ILD.Data.Entities;
using ILD.Data.Enums;
using ILD.Data.Stores.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace ILD.Data.Stores;

public class AuthStore : IAuthStore
{
    private readonly AppDbContext _db;

    public AuthStore(AppDbContext db)
    {
        _db = db;
    }

    public async Task<User?> GetByUsernameAsync(string username)
        => await _db.Users.FirstOrDefaultAsync(u => u.Username == username);

    public async Task<User?> GetBySessionTokenAsync(string sessionToken)
        => await _db.Users.FirstOrDefaultAsync(u => u.SessionToken == sessionToken);

    public Task<bool> ExistsBySessionTokenAsync(string sessionToken)
        => _db.Users.AnyAsync(u => u.SessionToken == sessionToken);

    public async Task CreateUserAsync(User user)
    {
        _db.Users.Add(user);
        await _db.SaveChangesAsync();
    }

    public async Task UpdateUserAsync(User user)
    {
        await _db.SaveChangesAsync();
    }
}
