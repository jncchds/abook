using ABook.Core.Models;

namespace ABook.Core.Interfaces;

public interface IBookRepository
{
    Task<IEnumerable<Book>> GetAllAsync(int? userId = null);
    Task<Book?> GetByIdAsync(int id);
    Task<Book?> GetByIdWithDetailsAsync(int id);
    Task<Book> AddAsync(Book book);
    Task UpdateAsync(Book book);
    Task DeleteAsync(int id);

    Task<IEnumerable<Chapter>> GetChaptersAsync(int bookId);
    Task<Chapter?> GetChapterAsync(int bookId, int chapterId);
    Task<Chapter> AddChapterAsync(Chapter chapter);
    Task UpdateChapterAsync(Chapter chapter);

    Task<IEnumerable<AgentMessage>> GetMessagesAsync(int bookId, int? chapterId = null);
    Task<AgentMessage?> FindMessageByIdAsync(int messageId);
    Task<AgentMessage> AddMessageAsync(AgentMessage message);
    Task UpdateMessageAsync(AgentMessage message);

    Task<LlmConfiguration?> GetLlmConfigAsync(int? bookId);
    Task<LlmConfiguration> UpsertLlmConfigAsync(LlmConfiguration config);
}

