using ABook.Core.Models;

namespace ABook.Core.Interfaces;

public interface IBookRepository
{
    Task<IEnumerable<Book>> GetAllAsync(int? userId = null);
    Task<Book?> GetByIdAsync(int id);
    /// <summary>Loads a book with all chapters — archived included. For display only.</summary>
    Task<Book?> GetByIdWithDetailsAsync(int id);
    /// <summary>
    /// Loads a book whose chapter collection excludes archived chapters. Use this for anything that
    /// feeds generation or export.
    /// </summary>
    Task<Book?> GetByIdWithActiveDetailsAsync(int id);
    Task<IReadOnlyList<int>> GetAncestryBookIdsAsync(int bookId, CancellationToken ct = default);
    /// <summary>
    /// Planning data (bible, non-archived characters, threads and chapters) of every ancestor of a
    /// continuation book, nearest ancestor first. Empty for a book with no base book.
    /// </summary>
    Task<IReadOnlyList<AncestorBookContext>> GetAncestorPlanningContextAsync(int bookId, CancellationToken ct = default);
    Task<Book> AddAsync(Book book);
    Task UpdateAsync(Book book);
    Task DeleteAsync(int id);

    /// <summary>The book's chapters in book order (<see cref="Chapter.SortOrder"/>).</summary>
    Task<IEnumerable<Chapter>> GetChaptersAsync(int bookId, bool includeArchived = false);
    Task<Chapter?> GetChapterAsync(int bookId, int chapterId);
    /// <summary>
    /// Adds a chapter: with <see cref="Chapter.SectionId"/> set, at the end of that section; otherwise in a new section
    /// of its own at the end of the book. Numbers are set from the place.
    /// </summary>
    Task<Chapter> AddChapterAsync(Chapter chapter);
    /// <summary>
    /// Adds a chapter after <paramref name="afterChapterId"/> (null = at the start). With <see cref="Chapter.SectionId"/>
    /// set it goes into that section (the anchor must be in it; null = first in it); with <paramref name="sameSection"/>
    /// it joins the anchor's section; otherwise it gets a new section of its own, placed after the anchor's section.
    /// </summary>
    Task<Chapter> InsertChapterAsync(Chapter chapter, int? afterChapterId, bool sameSection = false);
    /// <summary>
    /// Moves a chapter (archived ones too) after <paramref name="afterChapterId"/> (null = to the start). A chapter alone
    /// in its section moves with its section (placed after the anchor's section); otherwise it joins the anchor's
    /// section (null: first in its own section).
    /// </summary>
    Task MoveChapterAsync(int bookId, int chapterId, int? afterChapterId);
    /// <summary>Moves a chapter into a section, after <paramref name="afterChapterId"/> of it (null = first); an emptied section is removed.</summary>
    Task MoveChapterToSectionAsync(int bookId, int chapterId, int sectionId, int? afterChapterId);
    /// <summary>Sets every section's and chapter's number from its place again.</summary>
    Task RenumberChaptersAsync(int bookId);

    /// <summary>The book's sections in order.</summary>
    Task<IReadOnlyList<Section>> GetSectionsAsync(int bookId, bool includeArchived = false);
    Task<Section?> GetSectionAsync(int bookId, int sectionId);
    /// <summary>Saves a section's plan (placement and archive state are kept by the structure methods).</summary>
    Task UpdateSectionAsync(Section section);
    /// <summary>Moves a section with its chapters after <paramref name="afterSectionId"/> (null = to the start).</summary>
    Task MoveSectionAsync(int bookId, int sectionId, int? afterSectionId);
    /// <summary>Archives every active chapter of the section (it keeps its place).</summary>
    Task ArchiveSectionAsync(int bookId, int sectionId);
    /// <summary>Brings back the chapters archived with the section.</summary>
    Task RestoreSectionAsync(int bookId, int sectionId);
    /// <summary>
    /// Syncs the book's plan to the given sections in a single transaction, matched by section number: a matching
    /// section gets the new plan (its chapter too when it has only one; prose, status and versions preserved), new
    /// numbers become sections with one outlined chapter, and active sections absent from the set are deleted with
    /// their active chapters. Archived chapters are never touched and keep their place. Returns the active chapters
    /// of the planned sections, in order.
    /// </summary>
    Task<IReadOnlyList<Chapter>> ReplaceSectionsAsync(int bookId, IEnumerable<Section> sections);
    /// <summary>
    /// Merges section outlines by number into the plan without deleting anything, and returns the active chapters of
    /// the affected sections. Used to keep the outlines salvaged from a run that failed mid-stream.
    /// </summary>
    Task<IReadOnlyList<Chapter>> MergeSectionOutlinesAsync(int bookId, IEnumerable<Section> sections);
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
    /// <summary>Chapter id → id of its active version, for the book's chapters that have one.</summary>
    Task<IReadOnlyDictionary<int, int>> GetActiveChapterVersionIdsAsync(int bookId);
    /// <summary>Marks versionId as active, deactivates all others for this chapter, and copies version fields back to the Chapter row.</summary>
    Task<ChapterVersion> ActivateChapterVersionAsync(int bookId, int chapterId, int versionId);

    Task<IEnumerable<AgentMessage>> GetMessagesAsync(int bookId, int? chapterId = null);
    Task<AgentMessage?> FindMessageByIdAsync(int messageId);
    Task<AgentMessage> AddMessageAsync(AgentMessage message);
    Task UpdateMessageAsync(AgentMessage message);

    /// <summary>The book's own LLM settings; there is no user or global fallback.</summary>
    Task<LlmConfiguration?> GetLlmConfigAsync(int bookId);
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
    Task<IReadOnlyList<CharacterCard>> AddCharacterCardsBatchAsync(IEnumerable<CharacterCard> cards);
    Task UpdateCharacterCardAsync(CharacterCard card);
    Task ArchiveCharacterCardAsync(int bookId, int cardId);
    Task UnarchiveCharacterCardAsync(int bookId, int cardId);
    Task DeleteCharacterCardAsync(int bookId, int cardId);
    /// <summary>Deletes the book's active character cards. Archived cards are left untouched.</summary>
    Task DeleteCharacterCardsAsync(int bookId);
    /// <summary>
    /// Merges cards by Name into the active set without deleting anything, and returns the affected rows.
    /// Used to keep the characters salvaged from a run that failed mid-stream.
    /// </summary>
    Task<IReadOnlyList<CharacterCard>> MergeCharacterCardsAsync(int bookId, IEnumerable<CharacterCard> cards);

    // Character Card versions
    Task<CharacterCardVersion> AddCharacterVersionAsync(CharacterCardVersion version);
    Task AddCharacterVersionsBatchAsync(IEnumerable<CharacterCardVersion> versions);
    Task<IEnumerable<CharacterCardVersion>> GetCharacterVersionsAsync(int bookId, int cardId);
    Task<CharacterCardVersion?> GetCharacterVersionAsync(int bookId, int cardId, int versionId);
    Task<CharacterCard> RestoreCharacterVersionAsync(int bookId, int cardId, int versionId);

    // Plot Threads
    Task<IEnumerable<PlotThread>> GetPlotThreadsAsync(int bookId);
    Task<IEnumerable<PlotThread>> GetAllPlotThreadsAsync(int bookId);
    Task<PlotThread?> GetPlotThreadAsync(int bookId, int threadId);
    Task<PlotThread> AddPlotThreadAsync(PlotThread thread);
    Task<IReadOnlyList<PlotThread>> AddPlotThreadsBatchAsync(IEnumerable<PlotThread> threads);
    Task UpdatePlotThreadAsync(PlotThread thread);
    Task ArchivePlotThreadAsync(int bookId, int threadId);
    Task UnarchivePlotThreadAsync(int bookId, int threadId);
    Task DeletePlotThreadAsync(int bookId, int threadId);
    /// <summary>Deletes the book's active plot threads. Archived threads are left untouched.</summary>
    Task DeletePlotThreadsAsync(int bookId);
    /// <summary>
    /// Merges threads by Name into the active set without deleting anything, and returns the affected rows.
    /// Used to keep the plot threads salvaged from a run that failed mid-stream.
    /// </summary>
    Task<IReadOnlyList<PlotThread>> MergePlotThreadsAsync(int bookId, IEnumerable<PlotThread> threads);

    // Plot Thread versions
    Task<PlotThreadVersion> AddPlotThreadVersionAsync(PlotThreadVersion version);
    Task AddPlotThreadVersionsBatchAsync(IEnumerable<PlotThreadVersion> versions);
    Task<IEnumerable<PlotThreadVersion>> GetPlotThreadVersionsAsync(int bookId, int threadId);
    Task<PlotThreadVersion?> GetPlotThreadVersionAsync(int bookId, int threadId, int versionId);
    Task<PlotThread> RestorePlotThreadVersionAsync(int bookId, int threadId, int versionId);

    // Illustrations
    /// <summary>Illustrations of the book, optionally narrowed to one chapter and/or kind, in reading order.</summary>
    Task<IReadOnlyList<Illustration>> GetIllustrationsAsync(int bookId, int? chapterId = null, IllustrationKind? kind = null);
    Task<Illustration?> GetIllustrationAsync(int bookId, int illustrationId);
    Task<Illustration> AddIllustrationAsync(Illustration illustration);
    Task UpdateIllustrationAsync(Illustration illustration);
    /// <summary>Deletes the illustration and its rendered image.</summary>
    Task DeleteIllustrationAsync(int bookId, int illustrationId);
    /// <summary>
    /// Replaces the never-rendered illustrations of one kind — scenes of a chapter, or the book's cover —
    /// with <paramref name="planned"/>. Illustrations that have an image are kept.
    /// </summary>
    Task<IReadOnlyList<Illustration>> ReplaceUnrenderedIllustrationsAsync(
        int bookId, int? chapterId, IllustrationKind kind, IEnumerable<Illustration> planned);
    /// <summary>Stores a rendered image for the illustration, replacing (and deleting) its previous image.</summary>
    Task<Illustration> SaveRenderedImageAsync(int bookId, int illustrationId, BookImage image);
    Task<BookImage?> GetBookImageAsync(int bookId, int imageId);
    Task<IReadOnlyList<BookImage>> GetBookImagesAsync(int bookId, IEnumerable<int> imageIds);

    // Clarifications (answered planning questions and author notes, appended to the premise)
    /// <summary>The book's clarifications, oldest first.</summary>
    Task<IReadOnlyList<Clarification>> GetClarificationsAsync(int bookId, bool includeArchived = false);
    Task<Clarification?> GetClarificationAsync(int bookId, int clarificationId);
    /// <summary>
    /// Saves a new clarification and its first version. <paramref name="createdBy"/> names who made it (null: derived —
    /// Telegram when it has an <see cref="Clarification.Origin"/>, the planner for its questions, otherwise the web UI).
    /// </summary>
    Task<Clarification> AddClarificationAsync(Clarification clarification, string? createdBy = null);
    /// <summary>Saves a clarification; a changed question or answer is recorded as a new version (a manual one amends a manual one).</summary>
    Task UpdateClarificationAsync(Clarification clarification, string? createdBy = null);
    /// <summary>Newest first.</summary>
    Task<IReadOnlyList<ClarificationVersion>> GetClarificationVersionsAsync(int bookId, int clarificationId);
    Task<ClarificationVersion?> GetClarificationVersionAsync(int bookId, int clarificationId, int versionId);
    /// <summary>Puts the version's question and answer back (and un-archives it), recorded as a new version.</summary>
    Task<Clarification> RestoreClarificationVersionAsync(int bookId, int clarificationId, int versionId);
    /// <summary>Archives every active clarification of the book.</summary>
    Task ArchiveClarificationsAsync(int bookId);

    // Book chat (Interactive books): messages with their proposed changes
    /// <summary>
    /// The newest <paramref name="limit"/> messages (all when null) older than <paramref name="beforeId"/>,
    /// returned oldest first, each with its proposals.
    /// </summary>
    /// <summary>
    /// The chat's messages (oldest first). Messages hidden by a rewind are left out unless <paramref name="includeHidden"/>
    /// — only the web UI shows them (collapsed); they never reach the LLM.
    /// </summary>
    Task<IReadOnlyList<BookChatMessage>> GetChatMessagesAsync(int bookId, int? beforeId = null, int? limit = null, bool includeHidden = false);
    Task<BookChatMessage?> GetChatMessageAsync(int bookId, int messageId);
    /// <summary>Saves a message together with its proposals.</summary>
    Task<BookChatMessage> AddChatMessageAsync(BookChatMessage message);
    Task UpdateChatMessageAsync(BookChatMessage message);
    Task<ChatProposal?> GetProposalAsync(int bookId, int proposalId);
    /// <summary>The proposals with these ids that belong to the book, in reply order (message, then ordinal).</summary>
    Task<IReadOnlyList<ChatProposal>> GetProposalsAsync(int bookId, IEnumerable<int> proposalIds);
    Task<IReadOnlyList<ChatProposal>> GetProposalsByStatusAsync(int bookId, ChatProposalStatus status);
    /// <summary>Pending or failed proposals created at or after <paramref name="since"/>, in messages still part of the chat.</summary>
    Task<IReadOnlyList<ChatProposal>> GetOpenProposalsSinceAsync(int bookId, DateTime since);
    Task UpdateProposalAsync(ChatProposal proposal);
    /// <summary>
    /// Forgets unsaved changes and cached rows after a failed save, so the next save does not retry the failed
    /// one and the next read sees the database.
    /// </summary>
    void DiscardPendingChanges();
    /// <summary>
    /// After a restart: author messages still waiting for a reply and proposals still being applied are
    /// marked Failed (their run is gone). Returns how many rows changed.
    /// </summary>
    Task<int> FailInterruptedChatAsync();

    // Visual assets (recurring props, outfits, places … shared by the illustrations)
    Task<IReadOnlyList<VisualAsset>> GetVisualAssetsAsync(int bookId);
    Task<VisualAsset?> GetVisualAssetAsync(int bookId, int assetId);
    Task<VisualAsset> AddVisualAssetAsync(VisualAsset asset);
    Task UpdateVisualAssetAsync(VisualAsset asset);
    Task DeleteVisualAssetAsync(int bookId, int assetId);

    // Agent Runs (durable run state for restart resilience)
    Task<AgentRun> CreateRunAsync(AgentRun run);
    Task<AgentRun?> GetRunByIdAsync(Guid runId);
    Task<AgentRun?> GetActiveRunForBookAsync(int bookId);
    Task UpdateRunAsync(AgentRun run);
    Task<IEnumerable<AgentRun>> GetRunsByStatusAsync(AgentRunPersistStatus status);
    /// <summary>Every run of the book, oldest first.</summary>
    Task<IReadOnlyList<AgentRun>> GetRunsForBookAsync(int bookId);

    // LLM Presets
    Task<IEnumerable<LlmPreset>> GetPresetsAsync(int? userId);
    Task<LlmPreset?> GetPresetAsync(int id);
    /// <summary>The user's default preset, provided it is still their own or shared.</summary>
    Task<LlmPreset?> GetDefaultPresetAsync(int userId);
    Task<LlmPreset> CreatePresetAsync(LlmPreset preset);
    Task UpdatePresetAsync(LlmPreset preset);
    Task DeletePresetAsync(int id);

    // Snapshots (append-only history)
    Task AddStoryBibleSnapshotAsync(StoryBibleSnapshot snapshot);
    /// <summary>Overwrites a snapshot with <paramref name="values"/>, dated now (a manual edit amending a manual snapshot).</summary>
    Task AmendStoryBibleSnapshotAsync(int snapshotId, StoryBibleSnapshot values);
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
    /// <summary>Overwrites a snapshot with <paramref name="values"/>, dated now (a manual edit amending a manual snapshot).</summary>
    Task AmendBookSnapshotAsync(int snapshotId, BookSnapshot values);
    Task<IEnumerable<BookSnapshot>> GetBookSnapshotsAsync(int bookId);
    Task<BookSnapshot?> GetBookSnapshotAsync(int bookId, int snapshotId);

    // Public library
    Task<(List<PublicBookItemDto> Items, int TotalCount)> GetPublicBooksAsync(PublicBookFilter filter);
    Task<IEnumerable<string>> GetAllGenreStringsAsync(int? userId);
}

public record PublicBookFilter(int Page, int PageSize, string? Author, string? Genre, int? ChapterCount, int? UserId);
public record PublicBookItemDto(int Id, string Title, string Genre, int TargetChapterCount, int WrittenChapterCount, string Status, string Language, string AuthorDisplayName, DateTime CreatedAt, DateTime UpdatedAt);

