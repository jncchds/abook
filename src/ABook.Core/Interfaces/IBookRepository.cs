using ABook.Core.Models;

namespace ABook.Core.Interfaces;

public interface IBookRepository
{
    Task<IEnumerable<Book>> GetAllAsync(int? userId = null);
    Task<Book?> GetByIdAsync(int id);
    Task<Book?> GetByIdWithDetailsAsync(int id);
    Task<IReadOnlyList<int>> GetAncestryBookIdsAsync(int bookId, CancellationToken ct = default);
    Task<string> BuildAncestorPlanningReferenceAsync(int bookId, CancellationToken ct = default);
    Task<Book> AddAsync(Book book);
    Task UpdateAsync(Book book);
    Task DeleteAsync(int id);

    Task<IEnumerable<Chapter>> GetChaptersAsync(int bookId, bool includeArchived = false);
    Task<Chapter?> GetChapterAsync(int bookId, int chapterId);
    Task<Chapter> AddChapterAsync(Chapter chapter);
    /// <summary>Delete all chapters for the book then add the new set in a single transaction.</summary>
    Task ReplaceChaptersAsync(int bookId, IEnumerable<Chapter> chapters);
    Task UpdateChapterAsync(Chapter chapter);
    Task DeleteChaptersAsync(int bookId);
    Task ArchiveChapterAsync(int bookId, int chapterId);
    Task RestoreChapterAsync(int bookId, int chapterId);
    Task ArchiveChaptersAsync(int bookId);

    // Chapter versions
    Task<ChapterVersion> AddChapterVersionAsync(ChapterVersion version);
    Task UpdateChapterVersionAsync(ChapterVersion version);
    Task<IEnumerable<ChapterVersion>> GetChapterVersionsAsync(int chapterId);
    Task<ChapterVersion?> GetChapterVersionAsync(int chapterId, int versionId);
    /// <summary>Marks versionId as active, deactivates all others for this chapter, and copies version fields back to the Chapter row.</summary>
    Task<ChapterVersion> ActivateChapterVersionAsync(int bookId, int chapterId, int versionId);

    Task<IEnumerable<AgentMessage>> GetMessagesAsync(int bookId, int? chapterId = null);
    Task<AgentMessage?> FindMessageByIdAsync(int messageId);
    Task<AgentMessage> AddMessageAsync(AgentMessage message);
    Task UpdateMessageAsync(AgentMessage message);

    Task<LlmConfiguration?> GetLlmConfigAsync(int? bookId, int? userId = null);
    Task<LlmConfiguration> UpsertLlmConfigAsync(LlmConfiguration config);

    Task<TokenUsageRecord> AddTokenUsageAsync(TokenUsageRecord record);
    Task AddWorkflowStepAsync(int bookId, string step, string? endpoint = null, string? modelName = null, CancellationToken ct = default);
    Task<IEnumerable<TokenUsageRecord>> GetTokenUsageAsync(int bookId);
    Task DeleteTokenUsageAsync(int bookId);
    Task DeleteMessagesAsync(int bookId);

    // Story Bible
    Task<StoryBible?> GetStoryBibleAsync(int bookId);
    Task<StoryBible> UpsertStoryBibleAsync(StoryBible bible);
    Task DeleteStoryBibleAsync(int bookId);

    // Character Cards
    Task<IEnumerable<CharacterCard>> GetCharacterCardsAsync(int bookId);
    Task<IEnumerable<CharacterCard>> GetAllCharacterCardsAsync(int bookId);
    Task<CharacterCard?> GetCharacterCardAsync(int bookId, int cardId);
    Task<CharacterCard> AddCharacterCardAsync(CharacterCard card);
    Task UpdateCharacterCardAsync(CharacterCard card);
    Task ArchiveCharacterCardAsync(int bookId, int cardId);
    Task UnarchiveCharacterCardAsync(int bookId, int cardId);
    Task DeleteCharacterCardAsync(int bookId, int cardId);
    Task DeleteCharacterCardsAsync(int bookId);

    // Character Card versions
    Task<CharacterCardVersion> AddCharacterVersionAsync(CharacterCardVersion version);
    Task<IEnumerable<CharacterCardVersion>> GetCharacterVersionsAsync(int bookId, int cardId);
    Task<CharacterCardVersion?> GetCharacterVersionAsync(int bookId, int cardId, int versionId);
    Task<CharacterCard> RestoreCharacterVersionAsync(int bookId, int cardId, int versionId);

    // Plot Threads
    Task<IEnumerable<PlotThread>> GetPlotThreadsAsync(int bookId);
    Task<IEnumerable<PlotThread>> GetAllPlotThreadsAsync(int bookId);
    Task<PlotThread?> GetPlotThreadAsync(int bookId, int threadId);
    Task<PlotThread> AddPlotThreadAsync(PlotThread thread);
    Task UpdatePlotThreadAsync(PlotThread thread);
    Task ArchivePlotThreadAsync(int bookId, int threadId);
    Task UnarchivePlotThreadAsync(int bookId, int threadId);
    Task DeletePlotThreadAsync(int bookId, int threadId);
    Task DeletePlotThreadsAsync(int bookId);

    // Plot Thread versions
    Task<PlotThreadVersion> AddPlotThreadVersionAsync(PlotThreadVersion version);
    Task<IEnumerable<PlotThreadVersion>> GetPlotThreadVersionsAsync(int bookId, int threadId);
    Task<PlotThreadVersion?> GetPlotThreadVersionAsync(int bookId, int threadId, int versionId);
    Task<PlotThread> RestorePlotThreadVersionAsync(int bookId, int threadId, int versionId);

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

    // Snapshots (append-only history)
    Task AddStoryBibleSnapshotAsync(StoryBibleSnapshot snapshot);
    Task<IEnumerable<StoryBibleSnapshot>> GetStoryBibleSnapshotsAsync(int bookId);
    Task<StoryBibleSnapshot?> GetStoryBibleSnapshotAsync(int bookId, int snapshotId);
    Task<StoryBible> RestoreStoryBibleSnapshotAsync(int bookId, int snapshotId);

    Task AddCharactersSnapshotAsync(CharactersSnapshot snapshot);
    Task<IEnumerable<CharactersSnapshot>> GetCharactersSnapshotsAsync(int bookId);
    Task<CharactersSnapshot?> GetCharactersSnapshotAsync(int bookId, int snapshotId);
    Task<IEnumerable<CharacterCard>> RestoreCharactersSnapshotAsync(int bookId, int snapshotId);

    Task AddPlotThreadsSnapshotAsync(PlotThreadsSnapshot snapshot);
    Task<IEnumerable<PlotThreadsSnapshot>> GetPlotThreadsSnapshotsAsync(int bookId);
    Task<PlotThreadsSnapshot?> GetPlotThreadsSnapshotAsync(int bookId, int snapshotId);
    Task<IEnumerable<PlotThread>> RestorePlotThreadsSnapshotAsync(int bookId, int snapshotId);

    Task AddBookSnapshotAsync(BookSnapshot snapshot);
    Task<IEnumerable<BookSnapshot>> GetBookSnapshotsAsync(int bookId);
    Task<BookSnapshot?> GetBookSnapshotAsync(int bookId, int snapshotId);
}

