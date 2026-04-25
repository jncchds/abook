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
    Task DeleteChaptersAsync(int bookId);

    Task<IEnumerable<AgentMessage>> GetMessagesAsync(int bookId, int? chapterId = null);
    Task<AgentMessage?> FindMessageByIdAsync(int messageId);
    Task<AgentMessage> AddMessageAsync(AgentMessage message);
    Task UpdateMessageAsync(AgentMessage message);

    Task<LlmConfiguration?> GetLlmConfigAsync(int? bookId, int? userId = null);
    Task<LlmConfiguration> UpsertLlmConfigAsync(LlmConfiguration config);

    Task<TokenUsageRecord> AddTokenUsageAsync(TokenUsageRecord record);
    Task<IEnumerable<TokenUsageRecord>> GetTokenUsageAsync(int bookId);
    Task DeleteTokenUsageAsync(int bookId);
    Task DeleteMessagesAsync(int bookId);

    // Story Bible
    Task<StoryBible?> GetStoryBibleAsync(int bookId);
    Task<StoryBible> UpsertStoryBibleAsync(StoryBible bible);
    Task DeleteStoryBibleAsync(int bookId);

    // Character Cards
    Task<IEnumerable<CharacterCard>> GetCharacterCardsAsync(int bookId);
    Task<CharacterCard?> GetCharacterCardAsync(int bookId, int cardId);
    Task<CharacterCard> AddCharacterCardAsync(CharacterCard card);
    Task UpdateCharacterCardAsync(CharacterCard card);
    Task DeleteCharacterCardAsync(int bookId, int cardId);
    Task DeleteCharacterCardsAsync(int bookId);

    // Plot Threads
    Task<IEnumerable<PlotThread>> GetPlotThreadsAsync(int bookId);
    Task<PlotThread?> GetPlotThreadAsync(int bookId, int threadId);
    Task<PlotThread> AddPlotThreadAsync(PlotThread thread);
    Task UpdatePlotThreadAsync(PlotThread thread);
    Task DeletePlotThreadAsync(int bookId, int threadId);
    Task DeletePlotThreadsAsync(int bookId);

    // Agent Runs (durable run state for restart resilience)
    Task<AgentRun> CreateRunAsync(AgentRun run);
    Task<AgentRun?> GetRunByIdAsync(Guid runId);
    Task<AgentRun?> GetActiveRunForBookAsync(int bookId);
    Task UpdateRunAsync(AgentRun run);
    Task<IEnumerable<AgentRun>> GetRunsByStatusAsync(AgentRunPersistStatus status);

    // LLM Presets
    Task<IEnumerable<LlmPreset>> GetPresetsAsync(int? userId);
    Task<LlmPreset?> GetPresetAsync(int id);
    Task<LlmPreset> CreatePresetAsync(LlmPreset preset);
    Task UpdatePresetAsync(LlmPreset preset);
    Task DeletePresetAsync(int id);
}

