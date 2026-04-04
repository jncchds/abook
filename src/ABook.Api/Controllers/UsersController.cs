using ABook.Core.Interfaces;
using ABook.Core.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace ABook.Api.Controllers;

[ApiController]
[Route("api/users")]
[Authorize(Roles = "Admin")]
public class UsersController : ControllerBase
{
    private readonly IUserRepository _users;
    private readonly IPasswordHasher<AppUser> _hasher;

    public UsersController(IUserRepository users, IPasswordHasher<AppUser> hasher)
    {
        _users = users;
        _hasher = hasher;
    }

    [HttpGet]
    public async Task<IActionResult> GetAll()
    {
        var users = await _users.GetAllAsync();
        return Ok(users.Select(u => new { u.Id, u.Username, u.IsAdmin, u.CreatedAt }));
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateUserRequest req)
    {
        if (await _users.GetByUsernameAsync(req.Username) is not null)
            return Conflict(new { message = "Username already taken." });

        var user = new AppUser { Username = req.Username, IsAdmin = req.IsAdmin };
        user.PasswordHash = _hasher.HashPassword(user, req.Password);
        user = await _users.AddAsync(user);
        return Ok(new { user.Id, user.Username, user.IsAdmin, user.CreatedAt });
    }

    [HttpPut("{id:int}/password")]
    public async Task<IActionResult> ChangePassword(int id, [FromBody] ChangePasswordRequest req)
    {
        var user = await _users.GetByIdAsync(id);
        if (user is null) return NotFound();
        user.PasswordHash = _hasher.HashPassword(user, req.NewPassword);
        await _users.UpdateAsync(user);
        return NoContent();
    }

    [HttpPut("{id:int}/role")]
    public async Task<IActionResult> ChangeRole(int id, [FromBody] ChangeRoleRequest req)
    {
        var user = await _users.GetByIdAsync(id);
        if (user is null) return NotFound();
        user.IsAdmin = req.IsAdmin;
        await _users.UpdateAsync(user);
        return Ok(new { user.Id, user.Username, user.IsAdmin });
    }
}

public record CreateUserRequest(string Username, string Password, bool IsAdmin = false);
public record ChangePasswordRequest(string NewPassword);
public record ChangeRoleRequest(bool IsAdmin);
