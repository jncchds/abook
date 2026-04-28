#pragma warning disable SKEXP0001, SKEXP0010, SKEXP0070

using ABook.Core.Interfaces;
using ABook.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel.ChatCompletion;

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
    /// </summary>
    public async Task<IReadOnlyList<CharacterCard>> RunAsync(
        Book book, StoryBible bible, string qaContext, CancellationToken ct = default)
    {
        var bookId = book.Id;
        await Notifier.NotifyWorkflowProgressAsync(bookId, "Planning: Phase 2/4 - Characters...", false, ct);
        await Notifier.NotifyStatusChangedAsync(bookId, AgentRole.CharactersAgent, "Running", ct);

        var (kernel, config) = await GetKernelAsync(bookId);
        var history = new ChatHistory();

        var systemPrompt = !string.IsNullOrWhiteSpace(book.CharactersSystemPrompt)
            ? InterpolateSystemPrompt(book.CharactersSystemPrompt, book, bible)
            : InterpolateSystemPrompt(DefaultPrompts.Characters, book, bible);

        history.AddSystemMessage(systemPrompt);
        history.AddUserMessage($"""
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

            Create detailed character profiles for this book.
            """);

        var raw = await StreamResponseAsync(kernel, config, history, bookId, null, AgentRole.CharactersAgent, ct, jsonMode: true);

        List<CharacterCard> characters;
        try { characters = Parse(bookId, raw); }
        catch (Exception ex)
        {
            Logger.LogError(ex, "[Book {BookId}] CharactersAgent: could not parse Character Cards JSON.", bookId);
            await ReportErrorAsync(bookId, null, AgentRole.CharactersAgent,
                "Character Cards JSON could not be parsed. Try again.", ct);
            throw;
        }

        await Repo.DeleteCharacterCardsAsync(bookId);
        foreach (var card in characters)
            await Repo.AddCharacterCardAsync(card);
        book.CharactersStatus = PlanningPhaseStatus.Complete;
        await Repo.UpdateAsync(book);
        await Notifier.NotifyStatusChangedAsync(bookId, AgentRole.CharactersAgent, "Done", ct);
        await Notifier.NotifyWorkflowProgressAsync(bookId,
            $"Planning: {characters.Count} character{(characters.Count == 1 ? "" : "s")} saved. (complete)", false, ct);
        return characters;
    }

    private static List<CharacterCard> Parse(int bookId, string raw)
    {
        var json = ExtractJson(raw, '[', ']');
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var cards = new List<CharacterCard>();
        foreach (var el in doc.RootElement.EnumerateArray())
        {
            string Get(string k) => el.TryGetProperty(k, out var v) ? v.GetString() ?? "" : "";
            var roleStr = Get("role");
            var role = Enum.TryParse<CharacterRole>(roleStr, true, out var r) ? r : CharacterRole.Supporting;
            int? firstCh = el.TryGetProperty("firstAppearanceChapterNumber", out var fv)
                && fv.ValueKind == System.Text.Json.JsonValueKind.Number ? fv.GetInt32() : null;
            cards.Add(new CharacterCard
            {
                BookId = bookId,
                Name = Get("name"),
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
        return cards;
    }
}
