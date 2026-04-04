using ABook.Core.Interfaces;
using ABook.Core.Models;
using ABook.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace ABook.Infrastructure.Repositories;

public class UserRepository : IUserRepository
{
    private readonly AppDbContext _db;

    public UserRepository(AppDbContext db) => _db = db;

    public async Task<AppUser?> GetByUsernameAsync(string username) =>
        await _db.Users.FirstOrDefaultAsync(u => u.Username == username);

    public async Task<AppUser?> GetByIdAsync(int id) =>
        await _db.Users.FindAsync(id);

    public async Task<IEnumerable<AppUser>> GetAllAsync() =>
        await _db.Users.OrderBy(u => u.CreatedAt).ToListAsync();

    public async Task<AppUser> AddAsync(AppUser user)
    {
        user.CreatedAt = DateTime.UtcNow;
        _db.Users.Add(user);
        await _db.SaveChangesAsync();
        return user;
    }

    public async Task UpdateAsync(AppUser user)
    {
        _db.Users.Update(user);
        await _db.SaveChangesAsync();
    }

    public async Task<bool> AnyUsersExistAsync() =>
        await _db.Users.AnyAsync();
}
