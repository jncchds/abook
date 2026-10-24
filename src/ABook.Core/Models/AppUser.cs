using System.Text.Json.Serialization;

namespace ABook.Core.Models;

public class AppUser
{
    public int Id { get; set; }
    public string Username { get; set; } = string.Empty;
    public string? DisplayName { get; set; }
    [JsonIgnore] public string PasswordHash { get; set; } = string.Empty;
    public bool IsAdmin { get; set; }
    [JsonIgnore] public string? ApiToken { get; set; }
    public DateTime CreatedAt { get; set; }
    /// <summary>The preset (own or shared) whose settings are copied into each new book.</summary>
    public int? DefaultPresetId { get; set; }
    /// <summary>
    /// The Telegram account linked to this user (one each way): in a private chat with the bot it can create books and
    /// open its books, and in any connected chat it has full rights on its own books.
    /// </summary>
    [JsonIgnore] public long? TelegramUserId { get; set; }
    /// <summary>The linked Telegram account's display name (@username or name), for the web UI.</summary>
    public string? TelegramUsername { get; set; }
    public DateTime? TelegramLinkedAt { get; set; }

    [JsonIgnore] public LlmPreset? DefaultPreset { get; set; }

    [JsonIgnore] public ICollection<Book> Books { get; set; } = new List<Book>();
}
