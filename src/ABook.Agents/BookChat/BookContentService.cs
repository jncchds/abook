using System.Text.Json;
using ABook.Core.Interfaces;
using ABook.Core.Models;
using Microsoft.Extensions.Logging;

namespace ABook.Agents;

/// <summary>What one change did: a note for the author, and the target rendered before and after it.</summary>
public sealed record ContentChange(string Note, string Before, string After, int? ChapterId = null, int? ClarificationId = null);

/// <summary>Thrown when an Interactive book's locked premise or Story Bible would be changed.</summary>
public class StoryBibleLockedException() : InvalidOperationException(
    "The premise and Story Bible are locked because writing has started. Unlock them in Book Settings to change them.");

/// <summary>
/// The changes the book chat can make to a book, one piece of content at a time. Each returns the target
/// rendered (<see cref="BookContentRender"/>) before and after, so the change can be shown as a diff and
/// undone; undo goes through the same methods. Every change keeps the book's usual history (snapshots,
/// character and thread versions, chapter versions), and characters and threads are archived, never deleted.
/// </summary>
public class BookContentService : AgentBase
{
    public BookContentService(
        IBookRepository repo,
        ILlmProviderFactory llmFactory,
        IVectorStoreService vectorStore,
        IBookNotifier notifier,
        AgentRunStateService stateService,
        ILoggerFactory loggerFactory)
        : base(repo, llmFactory, vectorStore, notifier, stateService, loggerFactory) { }

    /// <summary>True when the book's premise and Story Bible are fixed canon (an Interactive book that started writing).</summary>
    public static bool IsBibleLocked(Book book) => book.Mode == BookMode.Interactive && book.StoryBibleLockedAt is not null;

    // ── Targets ─────────────────────────────────────────────────────────────────

    public const string PremiseTarget = "premise";
    public const string BibleTarget = "bible";
    public const string NoteTarget = "note";
    public static string CharacterTarget(string name) => $"character:{name}";
    public static string ThreadTarget(string name) => $"thread:{name}";
    public static string ChapterTarget(int chapterId) => $"chapter:{chapterId}";
    public static string ProseTarget(int chapterId) => $"prose:{chapterId}";

    /// <summary>The target as it reads now — what a proposal's preview and an undo are compared against.</summary>
    public async Task<string> RenderAsync(int bookId, string? target)
    {
        if (target is null) return "";
        var (kind, key) = target.IndexOf(':') is var i and >= 0 ? (target[..i], target[(i + 1)..]) : (target, "");
        switch (kind)
        {
            case PremiseTarget:
                return (await Repo.GetByIdAsync(bookId))?.Premise ?? "";
            case BibleTarget:
                return BookContentRender.Render(await Repo.GetStoryBibleAsync(bookId));
            case "character":
                return BookContentRender.Render(await FindCharacterAsync(bookId, key));
            case "thread":
                return BookContentRender.Render(await FindThreadAsync(bookId, key));
            case "chapter" when int.TryParse(key, out var id):
                var outlined = await Repo.GetChapterAsync(bookId, id);
                return outlined is { IsArchived: false } ? BookContentRender.Render(outlined) : "";
            case "prose" when int.TryParse(key, out var id):
                var written = await Repo.GetChapterAsync(bookId, id);
                return written is { IsArchived: false } ? written.Content : "";
            default:
                return "";
        }
    }

    public async Task<CharacterCard?> FindCharacterAsync(int bookId, string name) =>
        (await Repo.GetCharacterCardsAsync(bookId)).FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    public async Task<PlotThread?> FindThreadAsync(int bookId, string name) =>
        (await Repo.GetPlotThreadsAsync(bookId)).FirstOrDefault(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    // ── Premise and Story Bible ─────────────────────────────────────────────────

    public async Task<ContentChange> UpdatePremiseAsync(int bookId, string premise, bool ignoreLock = false)
    {
        var book = await LoadBookAsync(bookId);
        if (IsBibleLocked(book) && !ignoreLock) throw new StoryBibleLockedException();
        if (string.IsNullOrWhiteSpace(premise)) throw new InvalidOperationException("The premise cannot be empty.");

        var before = book.Premise;
        await Repo.AddBookSnapshotAsync(new BookSnapshot
        {
            BookId = bookId, Title = book.Title, Premise = book.Premise, Genre = book.Genre,
            TargetChapterCount = book.TargetChapterCount, Language = book.Language, Mode = book.Mode, Reason = "chat",
        });
        book.Premise = premise.Trim();
        await Repo.UpdateAsync(book);
        return new ContentChange("Updated the premise.", before, book.Premise);
    }

    /// <summary>Sets the given Story Bible fields, creating the bible when the book has none.</summary>
    public async Task<ContentChange> UpdateStoryBibleAsync(int bookId, IReadOnlyDictionary<string, string> changes, bool ignoreLock = false)
    {
        var book = await LoadBookAsync(bookId);
        if (IsBibleLocked(book) && !ignoreLock) throw new StoryBibleLockedException();

        var existing = await Repo.GetStoryBibleAsync(bookId);
        var before = BookContentRender.Render(existing);
        if (existing is not null)
            await Repo.AddStoryBibleSnapshotAsync(BibleSnapshot(bookId, existing, "chat-overwrite"));

        var bible = existing ?? new StoryBible { BookId = bookId };
        var fields = BookContentRender.Merge(BookContentRender.Fields(bible), changes);
        bible.SettingDescription = fields["settingDescription"];
        bible.TimePeriod = fields["timePeriod"];
        bible.Themes = fields["themes"];
        bible.ToneAndStyle = fields["toneAndStyle"];
        bible.WorldRules = fields["worldRules"];
        bible.Notes = fields["notes"];
        var saved = await Repo.UpsertStoryBibleAsync(bible);
        await Repo.AddStoryBibleSnapshotAsync(BibleSnapshot(bookId, saved, "chat-revised"));

        book = await LoadBookAsync(bookId);
        book.StoryBibleStatus = PlanningPhaseStatus.Complete;
        await Repo.UpdateAsync(book);
        return new ContentChange(existing is null ? "Created the Story Bible." : "Updated the Story Bible.",
            before, BookContentRender.Render(saved));
    }

    /// <summary>Puts the Story Bible back to <paramref name="rendered"/>; "" removes the bible the chat created.</summary>
    public async Task<ContentChange> RestoreStoryBibleAsync(int bookId, string rendered)
    {
        if (rendered.Length > 0)
            return await UpdateStoryBibleAsync(bookId, BookContentRender.Parse(rendered));

        var book = await LoadBookAsync(bookId);
        if (IsBibleLocked(book)) throw new StoryBibleLockedException();
        var existing = await Repo.GetStoryBibleAsync(bookId);
        if (existing is not null)
            await Repo.AddStoryBibleSnapshotAsync(BibleSnapshot(bookId, existing, "chat-overwrite"));
        await Repo.DeleteStoryBibleAsync(bookId);
        book.StoryBibleStatus = PlanningPhaseStatus.NotStarted;
        await Repo.UpdateAsync(book);
        return new ContentChange("Removed the Story Bible.", BookContentRender.Render(existing), "");
    }

    private static StoryBibleSnapshot BibleSnapshot(int bookId, StoryBible b, string reason) => new()
    {
        BookId = bookId,
        SettingDescription = b.SettingDescription,
        TimePeriod = b.TimePeriod,
        Themes = b.Themes,
        ToneAndStyle = b.ToneAndStyle,
        WorldRules = b.WorldRules,
        Notes = b.Notes,
        Reason = reason,
    };

    /// <summary>
    /// Locks the premise and Story Bible: the book moves to its writing stage. Pending proposals that would
    /// change them are superseded. Returns false when they were already locked.
    /// </summary>
    public async Task<bool> LockStoryBibleAsync(int bookId)
    {
        var book = await LoadBookAsync(bookId);
        if (book.StoryBibleLockedAt is not null) return false;
        book.StoryBibleLockedAt = DateTime.UtcNow;
        if (await Repo.GetStoryBibleAsync(bookId) is { } bible)
        {
            await Repo.AddStoryBibleSnapshotAsync(BibleSnapshot(bookId, bible, "locked"));
            book.StoryBibleStatus = PlanningPhaseStatus.Complete;
        }
        await Repo.UpdateAsync(book);

        foreach (var p in await Repo.GetProposalsByStatusAsync(bookId, ChatProposalStatus.Pending))
            if (p.Type is BookChatActions.UpdateStoryBible or BookChatActions.UpdatePremise)
            {
                p.Status = ChatProposalStatus.Superseded;
                p.ResultNote = "The premise and Story Bible were locked.";
                await Repo.UpdateProposalAsync(p);
            }
        return true;
    }

    /// <summary>Unlocks the premise and Story Bible (author only). Returns false when they were not locked.</summary>
    public async Task<bool> UnlockStoryBibleAsync(int bookId)
    {
        var book = await LoadBookAsync(bookId);
        if (book.StoryBibleLockedAt is null) return false;
        book.StoryBibleLockedAt = null;
        await Repo.UpdateAsync(book);
        return true;
    }

    // ── Characters ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Sets the given fields on the character called <paramref name="name"/>, creating it — or bringing back its
    /// archived card, portrait included — when there is no active one.
    /// </summary>
    public async Task<ContentChange> UpsertCharacterAsync(int bookId, string name, IReadOnlyDictionary<string, string> changes, string createdBy = AgentCreatedBy.BookChat)
    {
        name = name.Trim();
        if (name.Length == 0) throw new InvalidOperationException("A character needs a name.");
        var all = (await Repo.GetAllCharacterCardsAsync(bookId)).ToList();
        var card = all.FirstOrDefault(c => !c.IsArchived && c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        var before = BookContentRender.Render(card);
        string verb = "Updated";
        if (card is null)
        {
            card = all.Where(c => c.IsArchived && c.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                      .MaxBy(c => c.Id);
            if (card is not null) { card.IsArchived = false; verb = "Brought back"; }
            else { card = new CharacterCard { BookId = bookId, Name = name }; verb = "Added"; }
        }

        var fields = BookContentRender.Merge(BookContentRender.Fields(card), changes);
        if (Enum.TryParse<CharacterRole>(fields["role"], true, out var role)) card.Role = role;
        card.PhysicalDescription = fields["physicalDescription"];
        card.Personality = fields["personality"];
        card.Backstory = fields["backstory"];
        card.GoalMotivation = fields["goalMotivation"];
        card.Arc = fields["arc"];
        card.FirstAppearanceChapterNumber = BookContentRender.Number(fields["firstAppearanceChapterNumber"]);
        card.Notes = fields["notes"];

        if (card.Id == 0) card = await Repo.AddCharacterCardAsync(card);
        else await Repo.UpdateCharacterCardAsync(card);
        // One at a time: the single-version save numbers it after the card's earlier versions.
        await Repo.AddCharacterVersionAsync(CharactersAgent.VersionsOf(bookId, [card], createdBy).Single());
        await MarkPhaseCompleteAsync(bookId, b => b.CharactersStatus = PlanningPhaseStatus.Complete);
        return new ContentChange($"{verb} character {card.Name}.", before, BookContentRender.Render(card));
    }

    public async Task<ContentChange> ArchiveCharacterAsync(int bookId, string name)
    {
        var card = await FindCharacterAsync(bookId, name)
            ?? throw new InvalidOperationException($"There is no character called {name}.");
        var before = BookContentRender.Render(card);
        await Repo.ArchiveCharacterCardAsync(bookId, card.Id);
        return new ContentChange($"Archived character {card.Name}.", before, "");
    }

    /// <summary>Puts a character back to <paramref name="rendered"/>; "" archives the one the chat added.</summary>
    public Task<ContentChange> RestoreCharacterAsync(int bookId, string name, string rendered) =>
        rendered.Length == 0
            ? ArchiveCharacterAsync(bookId, name)
            : UpsertCharacterAsync(bookId, name, BookContentRender.Parse(rendered));

    // ── Plot threads ────────────────────────────────────────────────────────────

    /// <summary>Same rules as <see cref="UpsertCharacterAsync"/>.</summary>
    public async Task<ContentChange> UpsertPlotThreadAsync(int bookId, string name, IReadOnlyDictionary<string, string> changes, string createdBy = AgentCreatedBy.BookChat)
    {
        name = name.Trim();
        if (name.Length == 0) throw new InvalidOperationException("A plot thread needs a name.");
        var all = (await Repo.GetAllPlotThreadsAsync(bookId)).ToList();
        var thread = all.FirstOrDefault(t => !t.IsArchived && t.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        var before = BookContentRender.Render(thread);
        string verb = "Updated";
        if (thread is null)
        {
            thread = all.Where(t => t.IsArchived && t.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                        .MaxBy(t => t.Id);
            if (thread is not null) { thread.IsArchived = false; verb = "Brought back"; }
            else { thread = new PlotThread { BookId = bookId, Name = name, Type = PlotThreadType.Subplot }; verb = "Added"; }
        }

        var fields = BookContentRender.Merge(BookContentRender.Fields(thread), changes);
        thread.Description = fields["description"];
        if (Enum.TryParse<PlotThreadType>(fields["type"], true, out var type)) thread.Type = type;
        if (Enum.TryParse<PlotThreadStatus>(fields["status"], true, out var status)) thread.Status = status;
        thread.IntroducedChapterNumber = BookContentRender.Number(fields["introducedChapterNumber"]);
        thread.ResolvedChapterNumber = BookContentRender.Number(fields["resolvedChapterNumber"]);

        if (thread.Id == 0) thread = await Repo.AddPlotThreadAsync(thread);
        else await Repo.UpdatePlotThreadAsync(thread);
        await Repo.AddPlotThreadVersionAsync(PlotThreadsAgent.VersionsOf(bookId, [thread], createdBy).Single());
        await MarkPhaseCompleteAsync(bookId, b => b.PlotThreadsStatus = PlanningPhaseStatus.Complete);
        return new ContentChange($"{verb} plot thread {thread.Name}.", before, BookContentRender.Render(thread));
    }

    public async Task<ContentChange> ArchivePlotThreadAsync(int bookId, string name)
    {
        var thread = await FindThreadAsync(bookId, name)
            ?? throw new InvalidOperationException($"There is no plot thread called {name}.");
        var before = BookContentRender.Render(thread);
        await Repo.ArchivePlotThreadAsync(bookId, thread.Id);
        return new ContentChange($"Archived plot thread {thread.Name}.", before, "");
    }

    public Task<ContentChange> RestorePlotThreadAsync(int bookId, string name, string rendered) =>
        rendered.Length == 0
            ? ArchivePlotThreadAsync(bookId, name)
            : UpsertPlotThreadAsync(bookId, name, BookContentRender.Parse(rendered));

    // ── Chapters (append-only: a new chapter always goes at the end) ────────────

    /// <summary>Adds a chapter after the last one, outline only.</summary>
    public async Task<ContentChange> AddChapterAsync(int bookId, IReadOnlyDictionary<string, string> fields)
    {
        var chapters = (await Repo.GetChaptersAsync(bookId)).ToList();
        var chapter = new Chapter { BookId = bookId, Number = (chapters.Count == 0 ? 0 : chapters.Max(c => c.Number)) + 1 };
        ApplyOutline(chapter, fields);
        if (chapter.Title.Length == 0) chapter.Title = $"Chapter {chapter.Number}";
        chapter = await Repo.AddChapterAsync(chapter);
        await MarkPhaseCompleteAsync(bookId, b => b.ChaptersStatus = PlanningPhaseStatus.Complete);
        await Notifier.NotifyChapterUpdatedAsync(bookId, chapter.Id);
        return new ContentChange($"Added Chapter {chapter.Number} \"{chapter.Title}\".", "", BookContentRender.Render(chapter), chapter.Id);
    }

    /// <summary>Sets outline fields of a chapter; its number, prose and status stay as they are.</summary>
    public async Task<ContentChange> UpdateChapterOutlineAsync(int bookId, int chapterId, IReadOnlyDictionary<string, string> changes)
    {
        var chapter = await LoadActiveChapterAsync(bookId, chapterId);
        var before = BookContentRender.Render(chapter);
        ApplyOutline(chapter, BookContentRender.Merge(BookContentRender.Fields(chapter), changes));
        await Repo.UpdateChapterAsync(chapter);
        await Notifier.NotifyChapterUpdatedAsync(bookId, chapterId);
        return new ContentChange($"Updated the outline of Chapter {chapter.Number}.", before, BookContentRender.Render(chapter), chapterId);
    }

    private static void ApplyOutline(Chapter chapter, IReadOnlyDictionary<string, string> fields)
    {
        string Get(string k) => fields.TryGetValue(k, out var v) ? v.Trim() : "";
        if (Get("title").Length > 0) chapter.Title = Get("title");
        chapter.Outline = Get("outline");
        chapter.PovCharacter = Get("povCharacter");
        chapter.CharactersInvolvedJson = BookContentRender.NameListJson(Get("charactersInvolved"));
        chapter.PlotThreadsJson = BookContentRender.NameListJson(Get("plotThreads"));
        chapter.ForeshadowingNotes = Get("foreshadowingNotes");
        chapter.PayoffNotes = Get("payoffNotes");
    }

    /// <summary>Archives the book's last chapter — only the last one, so chapter numbers never have gaps.</summary>
    public async Task<ContentChange> ArchiveLastChapterAsync(int bookId, int chapterId)
    {
        var chapter = await LoadActiveChapterAsync(bookId, chapterId);
        var last = (await Repo.GetChaptersAsync(bookId)).MaxBy(c => c.Number);
        if (last?.Id != chapterId)
            throw new InvalidOperationException($"Only the last chapter can be removed; Chapter {chapter.Number} is not the last.");
        var before = BookContentRender.Render(chapter);
        await Repo.ArchiveChapterAsync(bookId, chapterId);
        try { await VectorStore.DeleteChapterChunksAsync(bookId, chapterId); }
        catch (Exception ex) { Logger.LogWarning(ex, "[Book {BookId}] Removing archived chapter chunks failed.", bookId); }
        await Notifier.NotifyChapterUpdatedAsync(bookId, chapterId);
        return new ContentChange($"Removed Chapter {chapter.Number} \"{chapter.Title}\" (archived).", before, "", chapterId);
    }

    /// <summary>Brings an archived chapter back as the last chapter, provided its number is still free.</summary>
    public async Task<ContentChange> RestoreLastChapterAsync(int bookId, int chapterId)
    {
        var chapter = await Repo.GetChapterAsync(bookId, chapterId)
            ?? throw new InvalidOperationException($"Chapter {chapterId} not found.");
        var lastNumber = (await Repo.GetChaptersAsync(bookId)).Select(c => c.Number).DefaultIfEmpty(0).Max();
        if (chapter.Number != lastNumber + 1)
            throw new InvalidOperationException(
                $"Chapter {chapter.Number} cannot come back: the book now has {lastNumber} chapter(s) and chapters are only added at the end.");
        await Repo.RestoreChapterAsync(bookId, chapterId);
        var restored = await LoadActiveChapterAsync(bookId, chapterId);
        await Notifier.NotifyChapterUpdatedAsync(bookId, chapterId);
        return new ContentChange($"Brought back Chapter {restored.Number}.", "", BookContentRender.Render(restored), chapterId);
    }

    // ── Prose ───────────────────────────────────────────────────────────────────

    /// <summary>The chapter's active version id, or null when it has none (never written).</summary>
    public async Task<int?> ActiveVersionIdAsync(int bookId, int chapterId) =>
        (await Repo.GetActiveChapterVersionIdsAsync(bookId)).TryGetValue(chapterId, out var id) ? id : null;

    /// <summary>
    /// Undoes a prose change: makes <paramref name="versionId"/> the active version again, or — when the chapter
    /// had no prose before — clears it back to an outline.
    /// </summary>
    public async Task<ContentChange> RestoreProseAsync(int bookId, int chapterId, int? versionId, CancellationToken ct)
    {
        var chapter = await LoadActiveChapterAsync(bookId, chapterId);
        var before = chapter.Content;
        if (versionId is int id)
        {
            var version = await Repo.ActivateChapterVersionAsync(bookId, chapterId, id);
            if (!version.HasEmbeddings && !string.IsNullOrEmpty(version.Content))
            {
                var (_, config) = await GetChatClientAsync(bookId);
                try { await IndexChapterAsync(bookId, chapterId, version.Id, config, ct); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex) { Logger.LogWarning(ex, "[Book {BookId}] Indexing the restored version failed.", bookId); }
            }
        }
        else
        {
            try { await VectorStore.DeleteChapterChunksAsync(bookId, chapterId, ct); }
            catch (Exception ex) { Logger.LogWarning(ex, "[Book {BookId}] Removing chapter chunks failed.", bookId); }
            chapter.Content = string.Empty;
            chapter.Summary = string.Empty;
            chapter.SummaryVersionId = null;
            chapter.Status = ChapterStatus.Outlined;
            await Repo.UpdateChapterAsync(chapter);
        }
        var after = await LoadActiveChapterAsync(bookId, chapterId);
        await Notifier.NotifyChapterUpdatedAsync(bookId, chapterId, ct);
        return new ContentChange(versionId is null ? $"Cleared the prose of Chapter {after.Number}." : $"Restored the earlier text of Chapter {after.Number}.",
            before, after.Content, chapterId);
    }

    // ── Notes ───────────────────────────────────────────────────────────────────

    /// <summary>Saves a lasting decision as a clarification, so every later prompt carries it.</summary>
    public async Task<ContentChange> AddNoteAsync(int bookId, string text)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new InvalidOperationException("The note is empty.");
        var note = await Repo.AddClarificationAsync(new Clarification
        {
            BookId = bookId,
            Source = ClarificationSource.ChatNote,
            Question = PromptTemplates.BookChatNoteQuestion,
            Answer = text.Trim(),
        });
        await Notifier.NotifyMessagesUpdatedAsync(bookId);   // the UI re-reads clarifications on this event
        return new ContentChange("Noted the decision.", "", note.Answer, ClarificationId: note.Id);
    }

    public async Task<ContentChange> ArchiveNoteAsync(int bookId, int clarificationId)
    {
        var note = await Repo.GetClarificationAsync(bookId, clarificationId)
            ?? throw new InvalidOperationException("The note no longer exists.");
        note.IsArchived = true;
        await Repo.UpdateClarificationAsync(note);
        await Notifier.NotifyMessagesUpdatedAsync(bookId);
        return new ContentChange("Archived the note.", note.Answer, "", ClarificationId: note.Id);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────

    private async Task<Book> LoadBookAsync(int bookId) =>
        await Repo.GetByIdAsync(bookId) ?? throw new InvalidOperationException($"Book {bookId} not found.");

    private async Task<Chapter> LoadActiveChapterAsync(int bookId, int chapterId) =>
        EnsureNotArchived(await Repo.GetChapterAsync(bookId, chapterId)
            ?? throw new InvalidOperationException($"Chapter {chapterId} not found."));

    /// <summary>
    /// Content added in the chat marks its planning phase complete, so switching the book to Autonomous never
    /// regenerates (and overwrites) it.
    /// </summary>
    private async Task MarkPhaseCompleteAsync(int bookId, Action<Book> mark)
    {
        var book = await LoadBookAsync(bookId);
        mark(book);
        await Repo.UpdateAsync(book);
    }

    internal static string ToJson<T>(T value) => JsonSerializer.Serialize(value);
}
