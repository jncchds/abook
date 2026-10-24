using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ABook.Api.Telegram;
using ABook.Core.Interfaces;
using ABook.Core.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ABook.Api.Controllers;

/// <summary>
/// The book's Telegram connections, for its owner: whether the bot is on, the chats bound to the book, one-time
/// bind codes, and changing or revoking a binding.
/// </summary>
[ApiController]
[Route("api/books/{bookId:int}/telegram")]
[Authorize]
public class TelegramController(
    IBookRepository repo,
    ITelegramRepository tg,
    TelegramOptions options,
    TelegramBotState bot,
    TelegramBindingService bindings,
    TelegramDispatcher dispatcher) : ControllerBase
{
    private static readonly string[] Formats = ["epub", "fb2", "html"];

    [HttpGet]
    public async Task<IActionResult> Get(int bookId)
    {
        var denied = await this.RequireBookOwnershipAsync(bookId, repo);
        if (denied is not null) return denied;
        var list = options.Enabled ? await tg.GetBindingsForBookAsync(bookId) : [];
        return Ok(new
        {
            Enabled = options.Enabled,
            Ready = bot.IsReady,
            bot.Problem,
            BotUsername = bot.Username,
            PrivateTopics = bot.TopicsInPrivateChats,
            Webhook = options.UseWebhook,
            Bindings = list.Select(View),
        });
    }

    [HttpPost("codes")]
    public async Task<IActionResult> CreateCode(int bookId, [FromBody] TelegramCodeRequest req)
    {
        var denied = await this.RequireBookOwnershipAsync(bookId, repo);
        if (denied is not null) return denied;
        if (!options.Enabled) return Conflict(new { message = "The Telegram bot is off: set TELEGRAM_BOT_TOKEN." });
        if (!bot.IsReady) return Conflict(new { message = bot.Problem ?? "The Telegram bot is still starting. Try again in a moment." });

        var code = await bindings.CreateCodeAsync(bookId, req.Level ?? TelegramAccessLevel.Participate, CurrentUserId);
        return Ok(new
        {
            code.Code,
            code.ExpiresAt,
            PrivateLink = bot.PrivateLink(code.Code),
            GroupLink = bot.GroupLink(code.Code),
            BindCommand = $"/bind {code.Code}",
        });
    }

    [HttpPut("bindings/{bindingId:int}")]
    public async Task<IActionResult> Update(int bookId, int bindingId, [FromBody] TelegramBindingUpdateRequest req)
    {
        var (denied, binding) = await LoadAsync(bookId, bindingId);
        if (denied is not null) return denied;
        if (req.Level is { } level) binding!.Level = binding.IsChannel ? TelegramAccessLevel.Read : level;
        if (req.ControlAdminsOnly is { } adminsOnly) binding!.ControlAdminsOnly = adminsOnly;
        if (req.FileFormat is { } format)
        {
            format = format.ToLowerInvariant();
            if (!Formats.Contains(format)) return BadRequest(new { message = "The file format must be epub, fb2 or html." });
            binding!.FileFormat = format;
        }
        if (req.LiveDrafts is { } drafts) binding!.LiveDrafts = drafts;
        await tg.UpdateBindingAsync(binding!);
        dispatcher.Reload(binding!.Id);
        return Ok(View(binding));
    }

    [HttpDelete("bindings/{bindingId:int}")]
    public async Task<IActionResult> Revoke(int bookId, int bindingId)
    {
        var (denied, binding) = await LoadAsync(bookId, bindingId);
        if (denied is not null) return denied;
        await bindings.RevokeAsync(binding!, "disconnected by the book's owner", tellChat: true, origin: null);
        return NoContent();
    }

    private async Task<(IActionResult? Denied, TelegramBinding? Binding)> LoadAsync(int bookId, int bindingId)
    {
        var denied = await this.RequireBookOwnershipAsync(bookId, repo);
        if (denied is not null) return (denied, null);
        var binding = await tg.GetBindingAsync(bindingId);
        if (binding is null || binding.BookId != bookId || !binding.IsActive) return (NotFound(), null);
        return (null, binding);
    }

    private static object View(TelegramBinding b) => new
    {
        b.Id,
        b.ChatType,
        b.ChatTitle,
        IsTopic = b.ThreadId is not null,
        b.Level,
        b.ControlAdminsOnly,
        b.FileFormat,
        b.LiveDrafts,
        b.CreatedAt,
    };

    private int? CurrentUserId =>
        int.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;
}

/// <summary>Where Telegram delivers updates when <c>ABOOK_HOSTNAME</c> is set; checked by the webhook secret header.</summary>
[ApiController]
[Route("api/telegram/webhook")]
[AllowAnonymous]
public class TelegramWebhookController(TelegramOptions options, TelegramBotService botService) : ControllerBase
{
    [HttpPost]
    public async Task<IActionResult> Receive()
    {
        if (!options.UseWebhook) return NotFound();
        var header = Request.Headers["X-Telegram-Bot-Api-Secret-Token"].ToString();
        if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(header), Encoding.UTF8.GetBytes(options.WebhookSecret)))
            return Unauthorized();

        TgUpdate? update;
        try { update = await JsonSerializer.DeserializeAsync<TgUpdate>(Request.Body, TelegramApi.Json, HttpContext.RequestAborted); }
        catch (JsonException) { return BadRequest(); }
        if (update is not null) botService.Enqueue(update);
        // Answer at once; the update is handled in the background, in order.
        return Ok();
    }
}

public record TelegramCodeRequest(TelegramAccessLevel? Level);
public record TelegramBindingUpdateRequest(TelegramAccessLevel? Level, bool? ControlAdminsOnly, string? FileFormat, bool? LiveDrafts);
