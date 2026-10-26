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
    /// <summary>The book's two-step replies setting (<see cref="Book.ChatPartRequestsEnabled"/>), rendered "on" / "off".</summary>
    public const string PartRequestsTarget = "setting:partRequests";
    public static string CharacterTarget(string name) => $"character:{name}";
    public static string ThreadTarget(string name) => $"thread:{name}";
    public static string ChapterTarget(int chapterId) => $"chapter:{chapterId}";
    public static string ProseTarget(int chapterId) => $"prose:{chapterId}";
    public static string NoteIdTarget(int clarificationId) => $"{NoteTarget}:{clarificationId}";

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
            case NoteTarget when int.TryParse(key, out var id):
                return await Repo.GetClarificationAsync(bookId, id) is { IsArchived: false } note ? note.Answer : "";
            case "setting" when target == PartRequestsTarget:
                return (await Repo.GetByIdAsync(bookId))?.ChatPartRequestsEnabled == true ? "on" : "off";
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

    /// <summary>Turns the book chat's two-step replies on or off (also the undo of the chat's own suggestion).</summary>
    public async Task<ContentChange> SetPartRequestsAsync(int bookId, bool enabled)
    {
        var book = await LoadBookAsync(bookId);
        var before = book.ChatPartRequestsEnabled ? "on" : "off";
        book.ChatPartRequestsEnabled = enabled;
        await Repo.UpdateAsync(book);
        return new ContentChange(
            enabled ? "The chat may now read the parts of the book it needs before answering."
                    : "The chat no longer asks for parts of the book.",
            before, enabled ? "on" : "off");
    }

    /// <summary>The book's info (title, premise…) as a snapshot.</summary>
    public static BookSnapshot SnapshotOf(Book b, string reason) => new()
    {
        BookId = b.Id, Title = b.Title, Premise = b.Premise, Genre = b.Genre,
        TargetChapterCount = b.TargetChapterCount, Language = b.Language, Mode = b.Mode, Reason = reason,
    };

    private static bool SameBookInfo(BookSnapshot a, BookSnapshot b) =>
        a.Title == b.Title && a.Premise == b.Premise && a.Genre == b.Genre
        && a.TargetChapterCount == b.TargetChapterCount && a.Language == b.Language && a.Mode == b.Mode;

    /// <summary>
    /// Records a manual edit of the book's info (web UI, Telegram card) after it was saved: the state it replaced is kept
    /// unless the newest snapshot already holds it, and the new state is a snapshot of its own — or, on top of a manual
    /// snapshot of the replaced state, amends that one (<see cref="EditSources"/>).
    /// </summary>
    public async Task RecordBookEditAsync(BookSnapshot before, Book after, string source)
    {
        if (SameBookInfo(before, SnapshotOf(after, source))) return;
        var latest = (await Repo.GetBookSnapshotsAsync(after.Id)).FirstOrDefault();
        if (latest is not null && EditSources.IsManual(latest.Reason) && SameBookInfo(latest, before))
        {
            await Repo.AmendBookSnapshotAsync(latest.Id, SnapshotOf(after, source));
            return;
        }
        if (latest is null || !SameBookInfo(latest, before))
            await Repo.AddBookSnapshotAsync(new BookSnapshot
            {
                BookId = after.Id, Title = before.Title, Premise = before.Premise, Genre = before.Genre,
                TargetChapterCount = before.TargetChapterCount, Language = before.Language, Mode = before.Mode, Reason = "before-edit",
            });
        await Repo.AddBookSnapshotAsync(SnapshotOf(after, source));
    }

    /// <summary>
    /// Sets the given Story Bible fields, creating the bible when the book has none. <paramref name="source"/> is who
    /// made the change: null for the book chat, or a manual source (<see cref="EditSources"/>: the web UI, a Telegram
    /// card). History: the state it replaces is kept unless the newest snapshot already holds it, and the new state is
    /// a snapshot of its own — a manual edit on top of a manual snapshot amends that snapshot instead.
    /// </summary>
    public async Task<ContentChange> UpdateStoryBibleAsync(
        int bookId, IReadOnlyDictionary<string, string> changes, bool ignoreLock = false, string? source = null)
    {
        var book = await LoadBookAsync(bookId);
        if (IsBibleLocked(book) && !ignoreLock) throw new StoryBibleLockedException();

        var existing = await Repo.GetStoryBibleAsync(bookId);
        var before = BookContentRender.Render(existing);
        var latest = (await Repo.GetStoryBibleSnapshotsAsync(bookId)).FirstOrDefault();
        // Amend only a manual snapshot that holds the current state (older "user-update" snapshots hold the state
        // before an edit and stay as they are).
        var amend = latest is not null && existing is not null && EditSources.IsManual(source)
                    && EditSources.IsManual(latest.Reason) && SameBible(latest, existing);
        if (existing is not null && !amend && (latest is null || !SameBible(latest, existing)))
            await Repo.AddStoryBibleSnapshotAsync(BibleSnapshot(bookId, existing, source is null ? "chat-overwrite" : "before-edit"));

        var bible = existing ?? new StoryBible { BookId = bookId };
        var fields = BookContentRender.Merge(BookContentRender.Fields(bible), changes);
        bible.SettingDescription = fields["settingDescription"];
        bible.TimePeriod = fields["timePeriod"];
        bible.Themes = fields["themes"];
        bible.ToneAndStyle = fields["toneAndStyle"];
        bible.WorldRules = fields["worldRules"];
        bible.Notes = fields["notes"];
        var saved = await Repo.UpsertStoryBibleAsync(bible);
        if (amend) await Repo.AmendStoryBibleSnapshotAsync(latest!.Id, BibleSnapshot(bookId, saved, source!));
        else await Repo.AddStoryBibleSnapshotAsync(BibleSnapshot(bookId, saved, source ?? "chat-revised"));

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

    private static bool SameBible(StoryBibleSnapshot s, StoryBible b) =>
        s.SettingDescription == b.SettingDescription && s.TimePeriod == b.TimePeriod && s.Themes == b.Themes
        && s.ToneAndStyle == b.ToneAndStyle && s.WorldRules == b.WorldRules && s.Notes == b.Notes;

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

    /// <summary>
    /// Adds an outline-only chapter: at the end of <paramref name="sectionId"/>, or after the last one as a section of
    /// its own.
    /// </summary>
    public async Task<ContentChange> AddChapterAsync(int bookId, IReadOnlyDictionary<string, string> fields, int? sectionId = null)
    {
        if (sectionId is int sid) await LoadActiveSectionAsync(bookId, sid);
        var chapters = (await Repo.GetChaptersAsync(bookId)).ToList();
        var chapter = new Chapter { BookId = bookId, SectionId = sectionId, Number = (chapters.Count == 0 ? 0 : chapters.Max(c => c.Number)) + 1 };
        ApplyOutline(chapter, fields);
        if (chapter.Title.Length == 0) chapter.Title = $"Chapter {chapter.Number}";
        chapter = await Repo.AddChapterAsync(chapter);
        await MarkPhaseCompleteAsync(bookId, b => b.ChaptersStatus = PlanningPhaseStatus.Complete);
        await Notifier.NotifyChapterUpdatedAsync(bookId, chapter.Id);
        return new ContentChange($"Added Chapter {chapter.Number} \"{chapter.Title}\".", "", BookContentRender.Render(chapter), chapter.Id);
    }

    /// <summary>Sets outline fields of a chapter; its number, prose and status stay as they are.</summary>
    public async Task<ContentChange> UpdateChapterOutlineAsync(
        int bookId, int chapterId, IReadOnlyDictionary<string, string> changes, string createdBy = AgentCreatedBy.BookChat)
    {
        var chapter = await LoadActiveChapterAsync(bookId, chapterId);
        var before = BookContentRender.Render(chapter);
        ApplyOutline(chapter, BookContentRender.Merge(BookContentRender.Fields(chapter), changes));
        // Saved as a chapter version (the version list shows it; a manual edit amends a manual version).
        await Repo.AddChapterVersionAsync(new ChapterVersion
        {
            ChapterId = chapterId, BookId = bookId, Title = chapter.Title, Outline = chapter.Outline,
            Content = chapter.Content, Status = chapter.Status, PovCharacter = chapter.PovCharacter,
            CharactersInvolvedJson = chapter.CharactersInvolvedJson, PlotThreadsJson = chapter.PlotThreadsJson,
            ForeshadowingNotes = chapter.ForeshadowingNotes, PayoffNotes = chapter.PayoffNotes,
            CreatedBy = createdBy,   // the prose is unchanged: the repository moves its embeddings over
        });
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

    /// <summary>
    /// Archives a chapter (it keeps its place; the chapters after it are renumbered): any chapter not written yet, and
    /// of the written ones only the last — the story told so far is not cut out of the middle.
    /// </summary>
    public async Task<ContentChange> ArchiveLastChapterAsync(int bookId, int chapterId)
    {
        var chapter = await LoadActiveChapterAsync(bookId, chapterId);
        var last = (await Repo.GetChaptersAsync(bookId)).MaxBy(c => c.Number);
        if (last?.Id != chapterId && chapter.Content.Trim().Length > 0)
            throw new InvalidOperationException($"Chapter {chapter.Number} is written and not the last — only the last written chapter can be removed.");
        var before = BookContentRender.Render(chapter);
        await Repo.ArchiveChapterAsync(bookId, chapterId);
        try { await VectorStore.DeleteChapterChunksAsync(bookId, chapterId); }
        catch (Exception ex) { Logger.LogWarning(ex, "[Book {BookId}] Removing archived chapter chunks failed.", bookId); }
        await Notifier.NotifyChapterUpdatedAsync(bookId, chapterId);
        return new ContentChange($"Removed Chapter {chapter.Number} \"{chapter.Title}\" (archived).", before, "", chapterId);
    }

    /// <summary>Brings an archived chapter back in its place (the chapters after it are renumbered).</summary>
    public async Task<ContentChange> RestoreLastChapterAsync(int bookId, int chapterId)
    {
        var chapter = await Repo.GetChapterAsync(bookId, chapterId)
            ?? throw new InvalidOperationException($"Chapter {chapterId} not found.");
        if (!chapter.IsArchived) throw new InvalidOperationException($"Chapter {chapter.Number} is not archived.");
        await Repo.RestoreChapterAsync(bookId, chapterId);
        var restored = await LoadActiveChapterAsync(bookId, chapterId);
        await Notifier.NotifyChapterUpdatedAsync(bookId, chapterId);
        return new ContentChange($"Brought back Chapter {restored.Number}.", "", BookContentRender.Render(restored), chapterId);
    }

    // ── Chapter order: insert, move, split ──────────────────────────────────────

    /// <summary>
    /// Thrown by the structure changes below while an agent run works on the book: a run holds its own list of
    /// chapters, and moving them under it would write the wrong one.
    /// </summary>
    public sealed class BookBusyException() : InvalidOperationException(
        "An agent is working on this book. Wait until it finishes (or stop it) before changing the chapter order.");

    /// <summary>Refuses a structure change while a run occupies the book (the caller is not that run).</summary>
    public void EnsureNoActiveRun(int bookId)
    {
        if (StateService.GetStatus(bookId) is { IsActive: true }) throw new BookBusyException();
    }

    /// <summary>
    /// Adds an outline-only chapter right after <paramref name="afterChapterId"/> (null = at the start): in the anchor's
    /// section with <paramref name="sameSection"/>, otherwise as a new section of its own after the anchor's section.
    /// </summary>
    public async Task<ContentChange> InsertChapterAsync(int bookId, int? afterChapterId, IReadOnlyDictionary<string, string> fields,
        bool sameSection = false)
    {
        if (afterChapterId is int afterId) await LoadActiveChapterAsync(bookId, afterId);
        var chapter = new Chapter { BookId = bookId };
        ApplyOutline(chapter, fields);
        chapter = await Repo.InsertChapterAsync(chapter, afterChapterId, sameSection);
        if (chapter.Title.Length == 0)
        {
            chapter.Title = $"Chapter {chapter.Number}";
            await Repo.UpdateChapterAsync(chapter);
        }
        await MarkPhaseCompleteAsync(bookId, b => b.ChaptersStatus = PlanningPhaseStatus.Complete);
        await Notifier.NotifyChapterUpdatedAsync(bookId, chapter.Id);
        return new ContentChange($"Added Chapter {chapter.Number} \"{chapter.Title}\".", "", BookContentRender.Render(chapter), chapter.Id);
    }

    /// <summary>Moves a chapter to right after <paramref name="afterChapterId"/> (null = to the start).</summary>
    public async Task<ContentChange> MoveChapterAsync(int bookId, int chapterId, int? afterChapterId)
    {
        var chapter = await Repo.GetChapterAsync(bookId, chapterId)
            ?? throw new InvalidOperationException($"Chapter {chapterId} not found.");
        var before = BookContentRender.Render(chapter);
        var wasNumber = chapter.Number;
        await Repo.MoveChapterAsync(bookId, chapterId, afterChapterId);
        var moved = (await Repo.GetChapterAsync(bookId, chapterId))!;
        await Notifier.NotifyChapterUpdatedAsync(bookId, chapterId);
        var note = moved.IsArchived ? $"Moved archived chapter \"{moved.Title}\"."
            : wasNumber == moved.Number ? $"Chapter {moved.Number} stays Chapter {moved.Number}."
            : $"Chapter {wasNumber} is now Chapter {moved.Number}.";
        return new ContentChange(note, before, BookContentRender.Render(moved), chapterId);
    }

    /// <summary>
    /// Splits a chapter in two by its outline: the chapter keeps its title and gets <paramref name="firstOutline"/>,
    /// a new chapter right after it in the same section gets <paramref name="secondTitle"/> / <paramref name="secondOutline"/>
    /// (and the same POV, characters and plot threads). Both go back to Outlined to be written again — the old prose
    /// stays in the first chapter's versions. A section that had only this chapter keeps the whole outline as its own
    /// plan.
    /// </summary>
    public async Task<(Chapter First, Chapter Second)> SplitChapterAsync(
        int bookId, int chapterId, string firstOutline, string secondTitle, string secondOutline, CancellationToken ct = default)
    {
        var chapter = await LoadActiveChapterAsync(bookId, chapterId);
        if (string.IsNullOrWhiteSpace(firstOutline) || string.IsNullOrWhiteSpace(secondOutline))
            throw new InvalidOperationException("Both halves need an outline.");

        // The second half first: once the section has two chapters, the first half's outline is no longer mirrored
        // onto the section, which keeps the whole one.
        var second = await Repo.InsertChapterAsync(new Chapter
        {
            BookId = bookId, SectionId = chapter.SectionId, Title = secondTitle.Trim(), Outline = secondOutline.Trim(),
            PovCharacter = chapter.PovCharacter, CharactersInvolvedJson = chapter.CharactersInvolvedJson,
            PlotThreadsJson = chapter.PlotThreadsJson,
        }, chapterId, sameSection: true);
        if (second.Title.Length == 0)
        {
            second.Title = $"{chapter.Title} (II)";
            await Repo.UpdateChapterAsync(second);
        }

        await Repo.AddChapterVersionAsync(new ChapterVersion
        {
            ChapterId = chapterId, BookId = bookId, Title = chapter.Title, Outline = firstOutline.Trim(),
            Content = string.Empty, Status = ChapterStatus.Outlined, PovCharacter = chapter.PovCharacter,
            CharactersInvolvedJson = chapter.CharactersInvolvedJson, PlotThreadsJson = chapter.PlotThreadsJson,
            ForeshadowingNotes = chapter.ForeshadowingNotes, PayoffNotes = chapter.PayoffNotes,
            CreatedBy = AgentCreatedBy.Split,   // never amends a manual version: the old prose must stay in the history
        });
        if (!string.IsNullOrEmpty(chapter.Content))
        {
            try { await VectorStore.DeleteChapterChunksAsync(bookId, chapterId, ct); }
            catch (Exception ex) { Logger.LogWarning(ex, "[Book {BookId}] Removing chapter chunks failed.", bookId); }
        }
        var first = (await Repo.GetChapterAsync(bookId, chapterId))!;
        first.Summary = string.Empty;
        first.SummaryVersionId = null;
        await Repo.UpdateChapterAsync(first);

        await Notifier.NotifyChapterUpdatedAsync(bookId, first.Id, ct);
        await Notifier.NotifyChapterUpdatedAsync(bookId, second.Id, ct);
        return (first, second);
    }

    // ── Sections ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Sets a section's plan. A section with a single active chapter has that chapter's plan, so the chapter's outline
    /// is updated instead (as a chapter version) and the section follows it.
    /// </summary>
    public async Task<ContentChange> UpdateSectionPlanAsync(
        int bookId, int sectionId, IReadOnlyDictionary<string, string> changes, string createdBy = AgentCreatedBy.BookChat)
    {
        var section = await LoadActiveSectionAsync(bookId, sectionId);
        var before = BookContentRender.Render(section);
        var active = (await Repo.GetChaptersAsync(bookId)).Where(c => c.SectionId == sectionId).ToList();
        if (active.Count == 1)
        {
            await UpdateChapterOutlineAsync(bookId, active[0].Id, changes, createdBy);
        }
        else
        {
            var plan = new Chapter();
            plan.CopyPlanFrom(section);
            ApplyOutline(plan, BookContentRender.Merge(BookContentRender.Fields(section), changes));
            section.CopyPlanFrom(plan);
            await Repo.UpdateSectionAsync(section);
            if (active.Count > 0) await Notifier.NotifyChapterUpdatedAsync(bookId, active[0].Id);
        }
        var after = (await Repo.GetSectionAsync(bookId, sectionId))!;
        return new ContentChange($"Updated the plan of Section {after.Number}.", before, BookContentRender.Render(after));
    }

    /// <summary>Moves a section with its chapters to right after <paramref name="afterSectionId"/> (null = to the start).</summary>
    public async Task<ContentChange> MoveSectionAsync(int bookId, int sectionId, int? afterSectionId)
    {
        var section = await Repo.GetSectionAsync(bookId, sectionId)
            ?? throw new InvalidOperationException($"Section {sectionId} not found.");
        var wasNumber = section.Number;
        await Repo.MoveSectionAsync(bookId, sectionId, afterSectionId);
        var moved = (await Repo.GetSectionAsync(bookId, sectionId))!;
        await NotifySectionAsync(bookId, sectionId);
        return new ContentChange(moved.IsArchived ? $"Moved archived section \"{moved.Title}\"."
            : wasNumber == moved.Number ? $"Section {moved.Number} stays Section {moved.Number}."
            : $"Section {wasNumber} is now Section {moved.Number}.", "", "");
    }

    /// <summary>Moves a chapter into a section, after <paramref name="afterChapterId"/> of it (null = first).</summary>
    public async Task<ContentChange> MoveChapterToSectionAsync(int bookId, int chapterId, int sectionId, int? afterChapterId)
    {
        await Repo.MoveChapterToSectionAsync(bookId, chapterId, sectionId, afterChapterId);
        var moved = (await Repo.GetChapterAsync(bookId, chapterId))!;
        var section = await Repo.GetSectionAsync(bookId, sectionId);
        await Notifier.NotifyChapterUpdatedAsync(bookId, chapterId);
        return new ContentChange($"Chapter {moved.Number} is now in Section {section?.Number}.", "", "", chapterId);
    }

    public async Task<ContentChange> ArchiveSectionAsync(int bookId, int sectionId)
    {
        var section = await LoadActiveSectionAsync(bookId, sectionId);
        var chapters = (await Repo.GetChaptersAsync(bookId)).Where(c => c.SectionId == sectionId).ToList();
        await Repo.ArchiveSectionAsync(bookId, sectionId);
        foreach (var c in chapters)
        {
            try { await VectorStore.DeleteChapterChunksAsync(bookId, c.Id); }
            catch (Exception ex) { Logger.LogWarning(ex, "[Book {BookId}] Removing archived chapter chunks failed.", bookId); }
        }
        await NotifySectionAsync(bookId, sectionId);
        return new ContentChange($"Archived Section {section.Number} \"{section.Title}\" ({chapters.Count} chapter(s)).", "", "");
    }

    public async Task<ContentChange> RestoreSectionAsync(int bookId, int sectionId)
    {
        await Repo.RestoreSectionAsync(bookId, sectionId);
        var section = (await Repo.GetSectionAsync(bookId, sectionId))!;
        await NotifySectionAsync(bookId, sectionId);
        return new ContentChange($"Brought back Section {section.Number} \"{section.Title}\".", "", "");
    }

    /// <summary>
    /// Tells the UI the structure changed through one of the section's chapters: it reloads every chapter and section
    /// when that chapter's place or number changed.
    /// </summary>
    private async Task NotifySectionAsync(int bookId, int sectionId)
    {
        var chapter = (await Repo.GetChaptersAsync(bookId, includeArchived: true)).FirstOrDefault(c => c.SectionId == sectionId);
        if (chapter is not null) await Notifier.NotifyChapterUpdatedAsync(bookId, chapter.Id);
    }

    private async Task<Section> LoadActiveSectionAsync(int bookId, int sectionId)
    {
        var section = await Repo.GetSectionAsync(bookId, sectionId)
            ?? throw new InvalidOperationException($"Section {sectionId} not found.");
        return section.IsArchived ? throw new InvalidOperationException($"Section {section.Number} is archived. Restore it first.") : section;
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
        }, AgentCreatedBy.BookChat);
        await Notifier.NotifyMessagesUpdatedAsync(bookId);   // the UI re-reads clarifications on this event
        return new ContentChange("Noted the decision.", "", note.Answer, ClarificationId: note.Id);
    }

    /// <summary>
    /// Rewrites a clarification's answer (a note, or the author's answer to a planner question) — a new version of it;
    /// also the undo of that, with the text it had.
    /// </summary>
    public async Task<ContentChange> UpdateNoteAsync(int bookId, int clarificationId, string text)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new InvalidOperationException("The note is empty.");
        var note = await LoadActiveNoteAsync(bookId, clarificationId);
        var before = note.Answer;
        note.Answer = text.Trim();
        await Repo.UpdateClarificationAsync(note, AgentCreatedBy.BookChat);
        await Notifier.NotifyMessagesUpdatedAsync(bookId);
        return new ContentChange("Updated the note.", before, note.Answer, ClarificationId: note.Id);
    }

    public async Task<ContentChange> ArchiveNoteAsync(int bookId, int clarificationId)
    {
        var note = await LoadActiveNoteAsync(bookId, clarificationId);
        note.IsArchived = true;
        await Repo.UpdateClarificationAsync(note, AgentCreatedBy.BookChat);
        await Notifier.NotifyMessagesUpdatedAsync(bookId);
        return new ContentChange("Archived the note.", note.Answer, "", ClarificationId: note.Id);
    }

    /// <summary>Brings an archived note back (the undo of archive_note).</summary>
    public async Task<ContentChange> RestoreNoteAsync(int bookId, int clarificationId)
    {
        var note = await Repo.GetClarificationAsync(bookId, clarificationId)
            ?? throw new InvalidOperationException("The note no longer exists.");
        note.IsArchived = false;
        await Repo.UpdateClarificationAsync(note, AgentCreatedBy.BookChat);
        await Notifier.NotifyMessagesUpdatedAsync(bookId);
        return new ContentChange("Brought the note back.", "", note.Answer, ClarificationId: note.Id);
    }

    private async Task<Clarification> LoadActiveNoteAsync(int bookId, int clarificationId) =>
        await Repo.GetClarificationAsync(bookId, clarificationId) is { IsArchived: false } note
            ? note
            : throw new InvalidOperationException("The note no longer exists (or was archived).");

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
