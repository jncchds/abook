using ABook.Core.Interfaces;
using ABook.Core.Models;
using Microsoft.Extensions.Logging;

namespace ABook.Agents;

/// <summary>Planning Phase 2: generates and persists Character Cards.</summary>
public class CharactersAgent : AgentBase
{
    public CharactersAgent(
        IBookRepository repo,
        ILlmProviderFactory llmFactory,
        IVectorStoreService vectorStore,
        IBookNotifier notifier,
        AgentRunStateService stateService,
        ILoggerFactory loggerFactory)
        : base(repo, llmFactory, vectorStore, notifier, stateService, loggerFactory) { }

    /// <summary>
    /// Generates character profiles for the book, persists them, marks the phase Complete,
    /// and returns the saved character cards.
    /// Any profiles already saved are fed back to the model so a re-run refines and extends them
    /// instead of starting over. If the call fails part-way through streaming, the profiles that
    /// finished streaming are merged into the existing set before the failure is rethrown.
    /// </summary>
    public async Task<IReadOnlyList<CharacterCard>> RunAsync(
        Book book, StoryBible bible, string qaContext, CancellationToken ct = default)
    {
        var bookId = book.Id;
        await Notifier.NotifyWorkflowProgressAsync(bookId, "Planning: Phase 2/4 - Characters...", false, ct);
        await Notifier.NotifyStatusChangedAsync(bookId, AgentRole.CharactersAgent, "Running", ct: ct);

        var (client, config) = await GetChatClientAsync(bookId);
        var messages = new List<LlmChatMessage>();
        var ancestorReference = await Repo.BuildAncestorPlanningReferenceAsync(bookId, ct);

        // Archived cards are excluded — they must never reach the model or be overwritten.
        var existing = (await Repo.GetCharacterCardsAsync(bookId)).ToList();

        var systemPrompt = !string.IsNullOrWhiteSpace(book.CharactersSystemPrompt)
            ? InterpolateSystemPrompt(book.CharactersSystemPrompt, book, bible)
            : InterpolateSystemPrompt(DefaultPrompts.Characters, book, bible);

        messages.Add(new LlmChatMessage(LlmChatRole.System, systemPrompt));
        messages.Add(new LlmChatMessage(LlmChatRole.User, $"""
            Book title: {book.Title}
            Genre: {book.Genre}
            Premise: {book.Premise}
            Target chapter count: {book.TargetChapterCount}

            Story Bible:
            Setting: {bible.SettingDescription}
            Time period: {bible.TimePeriod}
            Themes: {bible.Themes}
            Tone & style: {bible.ToneAndStyle}
            World rules: {bible.WorldRules}
            {(qaContext.Length > 0 ? $"\nAuthor guidance so far:\n{qaContext}" : "")}
            {(ancestorReference.Length > 0 ? $"\n{ancestorReference}" : "")}
            {BuildExistingBlock(existing)}
            Create detailed character profiles for this book.
            """));

        var (raw, failure) = await StreamJsonResponseAsync(
            client, config, messages, bookId, null, AgentRole.CharactersAgent, JsonSchemas.Characters, ct);

        List<CharacterCard> characters = [];
        Exception? parseError = null;
        try { characters = Parse(bookId, raw); }
        catch (Exception ex)
        {
            parseError = ex;
            Logger.LogError(ex, "[Book {BookId}] CharactersAgent: could not parse Character Cards JSON.", bookId);
        }

        // A stream can also end cleanly on a truncated array — typically the model hitting MaxTokens.
        if (failure is null && parseError is null && !PartialJson.IsComplete(raw))
        {
            parseError = new FormatException(
                "The response was cut off before the character list was closed (the model likely hit its token limit).");
            Logger.LogWarning("[Book {BookId}] CharactersAgent: response truncated — salvaged {Count} profile(s).",
                bookId, characters.Count);
        }

        if (failure is not null || parseError is not null)
        {
            await KeepPartialAsync(bookId, characters, parseError);
            Rethrow(failure ?? parseError!);
        }

        // Snapshot existing characters before deleting so they are preserved in history
        if (existing.Count > 0)
        {
            await Repo.AddCharactersSnapshotAsync(new ABook.Core.Models.CharactersSnapshot
            {
                BookId = bookId,
                DataJson = System.Text.Json.JsonSerializer.Serialize(existing),
                Reason = "agent-overwrite",
                Source = "agent-overwrite"
            });
        }

        await Repo.DeleteCharacterCardsAsync(bookId);
        var savedCards = await Repo.AddCharacterCardsBatchAsync(characters);
        await SaveVersionsAsync(bookId, savedCards);

        // Save the newly generated characters as a snapshot so they appear in history immediately
        await Repo.AddCharactersSnapshotAsync(new ABook.Core.Models.CharactersSnapshot
        {
            BookId = bookId,
            DataJson = System.Text.Json.JsonSerializer.Serialize(characters),
            Reason = "agent-generated",
            Source = "agent-generated"
        });

        book.CharactersStatus = PlanningPhaseStatus.Complete;
        await Repo.UpdateAsync(book);
        await Notifier.NotifyStatusChangedAsync(bookId, AgentRole.CharactersAgent, "Done", ct: ct);
        await Notifier.NotifyWorkflowProgressAsync(bookId,
            $"Planning: {characters.Count} character{(characters.Count == 1 ? "" : "s")} saved. (complete)", false, ct);
        return characters;
    }

    /// <summary>
    /// Renders the profiles already saved for this book so a re-run builds on them.
    /// Empty when the book has none.
    /// </summary>
    private static string BuildExistingBlock(IReadOnlyList<CharacterCard> existing)
    {
        if (existing.Count == 0) return string.Empty;

        var json = SerializeForPrompt(existing.Select(c => new
        {
            name = c.Name,
            role = c.Role.ToString(),
            physicalDescription = c.PhysicalDescription,
            personality = c.Personality,
            backstory = c.Backstory,
            goalMotivation = c.GoalMotivation,
            arc = c.Arc,
            firstAppearanceChapterNumber = c.FirstAppearanceChapterNumber,
            notes = c.Notes,
        }));

        return $"""

            These character profiles are already saved for this book:
            {json}

            Return the complete, updated set: repeat every profile you are keeping (unchanged or revised,
            keeping its exact "name" so it can be matched), and add any character the story still needs.
            Profiles you leave out will be removed.

            """;
    }

    /// <summary>
    /// Keeps the profiles that finished streaming before the run failed. They are merged into the
    /// existing set — never replacing it — so nothing the author has already seen is lost, and the
    /// phase is deliberately left incomplete so the next run continues from here.
    /// </summary>
    private async Task KeepPartialAsync(int bookId, IReadOnlyList<CharacterCard> salvaged, Exception? parseError)
    {
        if (salvaged.Count == 0)
        {
            await ReportErrorAsync(bookId, null, AgentRole.CharactersAgent,
                parseError is not null
                    ? $"No characters could be saved: {parseError.Message} Try again."
                    : "The Characters run failed before any complete profile arrived. Nothing was saved — try again.");
            return;
        }

        var saved = await Repo.MergeCharacterCardsAsync(bookId, salvaged);
        await SaveVersionsAsync(bookId, saved);
        await Repo.AddCharactersSnapshotAsync(new ABook.Core.Models.CharactersSnapshot
        {
            BookId = bookId,
            DataJson = System.Text.Json.JsonSerializer.Serialize(saved),
            Reason = "agent-partial",
            Source = "agent-partial"
        });

        Logger.LogWarning("[Book {BookId}] CharactersAgent: kept {Count} character(s) salvaged from the interrupted run.",
            bookId, saved.Count);
        await ReportErrorAsync(bookId, null, AgentRole.CharactersAgent,
            $"The Characters run was interrupted. Kept the {saved.Count} profile{(saved.Count == 1 ? "" : "s")} " +
            "received before the failure — run it again to complete the set.");
        await Notifier.NotifyWorkflowProgressAsync(bookId,
            $"Planning: kept {saved.Count} character{(saved.Count == 1 ? "" : "s")} from the interrupted run.",
            false, CancellationToken.None);
    }

    private Task SaveVersionsAsync(int bookId, IReadOnlyList<CharacterCard> cards) =>
        Repo.AddCharacterVersionsBatchAsync(cards.Select(card => new ABook.Core.Models.CharacterCardVersion
        {
            CharacterCardId = card.Id,
            BookId = bookId,
            Name = card.Name,
            Role = card.Role,
            PhysicalDescription = card.PhysicalDescription,
            Personality = card.Personality,
            Backstory = card.Backstory,
            GoalMotivation = card.GoalMotivation,
            Arc = card.Arc,
            FirstAppearanceChapterNumber = card.FirstAppearanceChapterNumber,
            Notes = card.Notes,
            CreatedBy = AgentCreatedBy.Characters,
        }));

    internal static List<CharacterCard> Parse(int bookId, string raw)
    {
        // Salvaging keeps every element that finished streaming and drops only a truncated tail,
        // so a response cut short still yields the characters the author already saw.
        var json = PartialJson.SalvageArray(raw);
        if (string.IsNullOrWhiteSpace(json))
            throw new FormatException("Character cards response contained no JSON data.");
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var cards = new List<CharacterCard>();
        foreach (var el in doc.RootElement.EnumerateArray())
        {
            string Get(string k) => el.TryGetProperty(k, out var v) ? v.GetString() ?? "" : "";
            var name = Get("name");
            if (string.IsNullOrWhiteSpace(name)) continue;   // unusable without a merge key
            var roleStr = Get("role");
            var role = Enum.TryParse<CharacterRole>(roleStr, true, out var r) ? r : CharacterRole.Supporting;
            int? firstCh = el.TryGetProperty("firstAppearanceChapterNumber", out var fv)
                && fv.ValueKind == System.Text.Json.JsonValueKind.Number ? fv.GetInt32() : null;
            cards.Add(new CharacterCard
            {
                BookId = bookId,
                Name = name,
                Role = role,
                PhysicalDescription = Get("physicalDescription"),
                Personality = Get("personality"),
                Backstory = Get("backstory"),
                GoalMotivation = Get("goalMotivation"),
                Arc = Get("arc"),
                FirstAppearanceChapterNumber = firstCh,
                Notes = Get("notes")
            });
        }
        // An empty result must not be allowed to wipe the book's existing cards.
        if (cards.Count == 0)
            throw new FormatException("Character cards response contained no usable character.");
        return cards;
    }
}
