using ABook.Api.Auth;
using ABook.Api.Services;
using ABook.Core.Interfaces;
using ABook.Core.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

namespace ABook.Api.Controllers;

[ApiController]
[Route("api/auth")]
public class AuthController : ControllerBase
{
    private readonly IUserRepository _users;
    private readonly IPasswordHasher<AppUser> _hasher;
    private readonly JwtTokenService _tokens;
    private readonly JwtKeyService _keys;

    public AuthController(IUserRepository users, IPasswordHasher<AppUser> hasher, JwtTokenService tokens, JwtKeyService keys)
    {
        _users = users;
        _hasher = hasher;
        _tokens = tokens;
        _keys = keys;
    }

    /// <summary>Returns whether any users exist (used to show Register vs Login on first launch).</summary>
    [HttpGet("setup")]
    public async Task<IActionResult> Setup() =>
        Ok(new { needsSetup = !await _users.AnyUsersExistAsync() });

    [HttpPost("register")]
    public async Task<IActionResult> Register([FromBody] AuthRequest req, [FromServices] UserProvisioningService provisioning)
    {
        // Only allow registration if no users exist yet (first admin) OR current user is admin
        bool anyExist = await _users.AnyUsersExistAsync();
        if (anyExist)
        {
            // Subsequent registrations only via admin panel
            return Forbid();
        }

        if (await _users.GetByUsernameAsync(req.Username) is not null)
            return Conflict(new { message = "Username already taken." });

        var user = new AppUser { Username = req.Username, IsAdmin = true };
        user.PasswordHash = _hasher.HashPassword(user, req.Password);
        user = await provisioning.CreateAsync(user);

        await _tokens.IssueRefreshTokenAsync(user, HttpContext);
        return Ok(SessionDto(user, _tokens.CreateAccessToken(user)));
    }

    [HttpPost("login")]
    public async Task<IActionResult> Login([FromBody] AuthRequest req)
    {
        var user = await _users.GetByUsernameAsync(req.Username);
        if (user is null) return Unauthorized(new { message = "Invalid credentials." });

        var result = _hasher.VerifyHashedPassword(user, user.PasswordHash, req.Password);
        if (result == PasswordVerificationResult.Failed)
            return Unauthorized(new { message = "Invalid credentials." });

        if (result == PasswordVerificationResult.SuccessRehashNeeded)
        {
            user.PasswordHash = _hasher.HashPassword(user, req.Password);
            await _users.UpdateAsync(user);
        }

        await _tokens.IssueRefreshTokenAsync(user, HttpContext);
        return Ok(SessionDto(user, _tokens.CreateAccessToken(user)));
    }

    /// <summary>
    /// Exchanges the httpOnly refresh cookie for a fresh access token (rotating the cookie). The SPA calls it on
    /// load and whenever its access token has expired.
    /// </summary>
    [HttpPost("refresh")]
    public async Task<IActionResult> Refresh()
    {
        var session = await _tokens.RefreshAsync(HttpContext);
        if (session is not var (user, accessToken)) return Unauthorized(new { message = "Not signed in." });
        return Ok(SessionDto(user, accessToken));
    }

    /// <summary>Anonymous on purpose: the access token may already have expired when the user signs out.</summary>
    [HttpPost("logout")]
    public async Task<IActionResult> Logout()
    {
        await _tokens.SignOutAsync(HttpContext);
        return NoContent();
    }

    [HttpGet("me")]
    public async Task<IActionResult> Me()
    {
        if (User.Identity?.IsAuthenticated != true)
            return Unauthorized();
        var userId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var user = await _users.GetByIdAsync(userId);
        if (user is null) return Unauthorized();
        return Ok(new
        {
            id = userId,
            username = user.Username,
            displayName = user.DisplayName ?? user.Username,
            isAdmin = User.IsInRole("Admin")
        });
    }

    [HttpPatch("profile")]
    [Authorize]
    public async Task<IActionResult> UpdateProfile([FromBody] UpdateProfileRequest req)
    {
        var userId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var user = await _users.GetByIdAsync(userId);
        if (user is null) return Unauthorized();
        var name = req.DisplayName?.Trim();
        if (name?.Length > 100) return BadRequest(new { message = "Display name must be 100 characters or less." });
        user.DisplayName = string.IsNullOrEmpty(name) ? null : name;
        await _users.UpdateAsync(user);
        return Ok(new { displayName = user.DisplayName ?? user.Username });
    }

    [HttpGet("api-token")]
    [Authorize]
    public async Task<IActionResult> GetApiToken()
    {
        var userId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var user = await _users.GetByIdAsync(userId);
        if (user is null) return Unauthorized();
        return Ok(new { token = user.ApiToken });
    }

    [HttpPost("api-token/regenerate")]
    [Authorize]
    public async Task<IActionResult> RegenerateApiToken()
    {
        var userId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var newToken = Guid.NewGuid().ToString();
        await _users.UpdateApiTokenAsync(userId, newToken);
        return Ok(new { token = newToken });
    }

    private static object UserDto(AppUser u) => new { u.Id, u.Username, DisplayName = u.DisplayName ?? u.Username, u.IsAdmin };

    private object SessionDto(AppUser user, AccessToken accessToken) => new
    {
        user = UserDto(user),
        accessToken = accessToken.Token,
        accessTokenExpiresAt = accessToken.ExpiresAt,
        // Deployment problems only an admin can fix, shown as a banner in the UI.
        serverWarnings = user.IsAdmin && _keys.FromDatabase
            ? new[] { $"The sign-in signing key is stored in the database. Run `docker compose exec abook-api dotnet ABook.Api.dll secrets show` and set it as the {JwtKeyService.EnvVarName} environment variable." }
            : Array.Empty<string>(),
    };
}

public record AuthRequest(string Username, string Password);
public record UpdateProfileRequest(string? DisplayName);
