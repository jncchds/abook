using ABook.Core.Interfaces;
using ABook.Core.Models;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
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

    public AuthController(IUserRepository users, IPasswordHasher<AppUser> hasher)
    {
        _users = users;
        _hasher = hasher;
    }

    /// <summary>Returns whether any users exist (used to show Register vs Login on first launch).</summary>
    [HttpGet("setup")]
    public async Task<IActionResult> Setup() =>
        Ok(new { needsSetup = !await _users.AnyUsersExistAsync() });

    [HttpPost("register")]
    public async Task<IActionResult> Register([FromBody] AuthRequest req)
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
        user = await _users.AddAsync(user);

        await SignInAsync(user);
        return Ok(UserDto(user));
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

        await SignInAsync(user);
        return Ok(UserDto(user));
    }

    [HttpPost("logout")]
    [Authorize]
    public async Task<IActionResult> Logout()
    {
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return NoContent();
    }

    [HttpGet("me")]
    public IActionResult Me()
    {
        if (User.Identity?.IsAuthenticated != true)
            return Unauthorized();
        return Ok(new
        {
            id = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!),
            username = User.FindFirstValue(ClaimTypes.Name),
            isAdmin = User.IsInRole("Admin")
        });
    }

    private async Task SignInAsync(AppUser user)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClaimTypes.Name, user.Username),
            new(ClaimTypes.Role, user.IsAdmin ? "Admin" : "User")
        };
        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        await HttpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(identity),
            new AuthenticationProperties { IsPersistent = true, ExpiresUtc = DateTimeOffset.UtcNow.AddDays(30) });
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

    private static object UserDto(AppUser u) => new { u.Id, u.Username, u.IsAdmin };
}

public record AuthRequest(string Username, string Password);
