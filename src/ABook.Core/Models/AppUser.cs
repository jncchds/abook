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

    [JsonIgnore] public ICollection<Book> Books { get; set; } = new List<Book>();
}
