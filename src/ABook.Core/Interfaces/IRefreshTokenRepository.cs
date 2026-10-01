using ABook.Core.Models;

namespace ABook.Core.Interfaces;

public interface IRefreshTokenRepository
{
    Task AddAsync(RefreshToken token);
    Task<RefreshToken?> GetByHashAsync(string tokenHash);
    /// <summary>Revokes <paramref name="token"/> and stores <paramref name="replacement"/> in one save.</summary>
    Task RotateAsync(RefreshToken token, RefreshToken replacement);
    Task RevokeAsync(RefreshToken token);
    /// <summary>Signs the user out everywhere (password change, suspected token theft).</summary>
    Task RevokeAllForUserAsync(int userId);
    /// <summary>Deletes tokens that expired or were revoked before <paramref name="olderThan"/>.</summary>
    Task<int> DeleteStaleAsync(DateTime olderThan);
}
