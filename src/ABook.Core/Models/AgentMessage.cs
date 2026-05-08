using System.Text.Json.Serialization;

namespace ABook.Core.Models;

public class AgentMessage
{
    public int Id { get; set; }
    public int BookId { get; set; }
    public int? ChapterId { get; set; }
    public AgentRole AgentRole { get; set; }
    public MessageType MessageType { get; set; }
    public string Content { get; set; } = string.Empty;
    public bool IsResolved { get; set; }
    public bool IsOptional { get; set; }
    public bool IsDeleted { get; set; }
    public DateTime CreatedAt { get; set; }

    [JsonIgnore] public Book Book { get; set; } = null!;
    [JsonIgnore] public Chapter? Chapter { get; set; }
}
