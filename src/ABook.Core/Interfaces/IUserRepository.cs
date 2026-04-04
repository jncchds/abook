using ABook.Core.Models;

namespace ABook.Core.Interfaces;

public interface IUserRepository
{
    Task<AppUser?> GetByUsernameAsync(string username);
    Task<AppUser?> GetByIdAsync(int id);
    Task<IEnumerable<AppUser>> GetAllAsync();
    Task<AppUser> AddAsync(AppUser user);
    Task UpdateAsync(AppUser user);
    Task<bool> AnyUsersExistAsync();
}
