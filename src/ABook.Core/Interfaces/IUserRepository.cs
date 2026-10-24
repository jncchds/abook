using ABook.Core.Models;

namespace ABook.Core.Interfaces;

public interface IUserRepository
{
    Task<AppUser?> GetByUsernameAsync(string username);
    Task<AppUser?> GetByIdAsync(int id);
    Task<AppUser?> GetByApiTokenAsync(string token);
    /// <summary>The abook user a Telegram account is linked to.</summary>
    Task<AppUser?> GetByTelegramIdAsync(long telegramUserId);
    Task<IEnumerable<AppUser>> GetAllAsync();
    Task<AppUser> AddAsync(AppUser user);
    Task UpdateAsync(AppUser user);
    Task UpdateApiTokenAsync(int userId, string? token);
    Task<bool> AnyUsersExistAsync();
}
