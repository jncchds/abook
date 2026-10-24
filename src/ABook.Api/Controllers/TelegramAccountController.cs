using System.Security.Claims;
using ABook.Api.Telegram;
using ABook.Core.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ABook.Api.Controllers;

/// <summary>The signed-in user's link to a Telegram account (Settings → Telegram).</summary>
[ApiController]
[Route("api/telegram/account")]
[Authorize]
public class TelegramAccountController(
    IUserRepository users, TelegramAccountService accounts, TelegramOptions options, TelegramBotState bot) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get()
    {
        var user = await users.GetByIdAsync(CurrentUserId);
        if (user is null) return NotFound();
        return Ok(new
        {
            Enabled = options.Enabled,
            Ready = bot.IsReady,
            bot.Problem,
            BotUsername = bot.Username,
            Linked = user.TelegramUserId is null ? null : new { Username = user.TelegramUsername, LinkedAt = user.TelegramLinkedAt },
        });
    }

    [HttpPost("code")]
    public async Task<IActionResult> CreateCode()
    {
        if (!options.Enabled) return Conflict(new { message = "The Telegram bot is off: set TELEGRAM_BOT_TOKEN." });
        if (!bot.IsReady) return Conflict(new { message = bot.Problem ?? "The Telegram bot is still starting. Try again in a moment." });
        var code = await accounts.CreateCodeAsync(CurrentUserId);
        return Ok(new { code.Link, Command = $"/start {code.StartParameter}", code.ExpiresAt });
    }

    [HttpDelete]
    public async Task<IActionResult> Unlink()
    {
        var user = await users.GetByIdAsync(CurrentUserId);
        if (user is null) return NotFound();
        await accounts.UnlinkAsync(user);
        return NoContent();
    }

    private int CurrentUserId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
}
