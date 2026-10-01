namespace ABook.Core.Models;

/// <summary>
/// A browser sign-in session. The token itself lives only in the user's httpOnly cookie; the database keeps its
/// SHA-256 hash. Each use rotates it: the old row is revoked and points at its replacement.
/// </summary>
public class RefreshToken
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public string TokenHash { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime? RevokedAt { get; set; }
    /// <summary>Hash of the token issued when this one was rotated; null when revoked by sign-out.</summary>
    public string? ReplacedByHash { get; set; }

    public AppUser? User { get; set; }
}

/// <summary>
/// Server-generated secrets used when the matching environment variable is not set (e.g. the JWT signing key),
/// so an upgrade without new env vars keeps working and stays stable across restarts.
/// </summary>
public class ServerSecret
{
    public string Name { get; set; } = "";
    public string Value { get; set; } = "";
    public DateTime CreatedAt { get; set; }
}
