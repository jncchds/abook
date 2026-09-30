using System.Text;
using ABook.Core.Models;

namespace ABook.Agents;

// ─────────────────────────────────────────────────────────────────────────────
// Single home for every piece of text sent to an LLM (and for the RAG search queries).
//   • PromptPlaceholders — tokens usable in per-book system prompt overrides
//   • DefaultPrompts     — overridable system prompts, one per agent
//   • PromptTemplates    — fixed system prompts, user-message templates and context blocks
//   • RagQueries         — embedding search queries used to retrieve prior-chapter passages
// Agents gather data and call into these; they must not build prompt text themselves.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Placeholder tokens supported in every agent system prompt.
/// Use these in per-book prompt overrides; they are substituted with live book (and optionally
/// Story Bible) data immediately before each LLM call.
/// </summary>
public static class PromptPlaceholders
{
    // ── Book-level ─────────────────────────────────────────────────────────────
    /// <summary>Book title.</summary>
    public const string Title = "{TITLE}";

    /// <summary>Book genre (e.g. "Fantasy", "Thriller").</summary>
    public const string Genre = "{GENRE}";

    /// <summary>Full book premise / plot summary.</summary>
    public const string Premise = "{PREMISE}";

    /// <summary>Target number of chapters.</summary>
    public const string ChapterCount = "{CHAPTER_COUNT}";

    /// <summary>Output language (e.g. "English", "French").</summary>
    public const string Language = "{LANGUAGE}";

    // ── Story Bible (available once Phase 1 is complete) ──────────────────────
    /// <summary>Setting description from the Story Bible.</summary>
    public const string Setting = "{SETTING}";

    /// <summary>Comma-separated themes from the Story Bible.</summary>
    public const string Themes = "{THEMES}";

    /// <summary>Tone and style notes from the Story Bible.</summary>
    public const string Tone = "{TONE}";

    /// <summary>World rules / magic system / internal logic from the Story Bible.</summary>
    public const string WorldRules = "{WORLD_RULES}";

    // ── Cross-chapter context (resolved per-call, not from Book entity) ──────────
    /// <summary>
    /// Compact numbered list of all prior chapter synopses (Number · Title — Outline).
    /// Used by Writer and Editor to avoid re-introducing or restating established content.
    /// Resolved by agents that pass the value to <c>InterpolateSystemPrompt</c>.
    /// </summary>
    public const string ChapterSynopses = "{CHAPTER_SYNOPSES}";
}

/// <summary>
/// Default system prompt templates for every agent.
/// All prompts use <see cref="PromptPlaceholders"/> tokens which are substituted
/// at runtime via <c>AgentBase.InterpolateSystemPrompt</c>.
/// </summary>
public static class DefaultPrompts
{
    public static readonly string StoryBible =
        $"""
        You are a world-building expert. Your task is to create a Story Bible for a book project.
        Return a JSON object with these exact fields:
          "settingDescription" (string), "timePeriod" (string), "themes" (string, comma-separated list),
          "toneAndStyle" (string), "worldRules" (string), "notes" (string).
        All string values in the JSON must be written in {PromptPlaceholders.Language}.
        IMPORTANT: Output ONLY the raw JSON object. Do not wrap it in another object or array.
        Do not include any text, explanation, or markdown outside the JSON.
        """;

    public static readonly string Characters =
        $"""
        You are a character development expert. Your task is to create character profiles for the main
        and significant supporting characters in a book.
        Return a JSON array where each element has these exact fields:
          "name" (string), "role" ("Protagonist"|"Antagonist"|"Supporting"|"Minor"),
          "physicalDescription" (string), "personality" (string), "backstory" (string),
          "goalMotivation" (string), "arc" (string - how the character changes over the story),
          "firstAppearanceChapterNumber" (int or null), "notes" (string).
        Include all characters necessary to tell the story. Be thorough.
        All string values in the JSON must be written in {PromptPlaceholders.Language}.
        IMPORTANT: Output ONLY a raw JSON array (starting with [ and ending with ]).
        Do not wrap the array in an object. Do not include any text, explanation, or markdown outside the JSON array.
        """;

    public static readonly string PlotThreads =
        $"""
        You are a story structure expert. Your task is to map all major and minor plot threads
        for a book, including foreshadowing and payoff relationships.
        Return a JSON array where each element has these exact fields:
          "name" (string), "description" (string - what this thread is about and why it matters),
          "type" ("MainPlot"|"Subplot"|"CharacterArc"|"Mystery"|"Foreshadowing"|"WorldBuilding"|"ThematicThread"),
          "introducedChapterNumber" (int or null), "resolvedChapterNumber" (int or null),
          "status" ("Active"|"Resolved"|"Dormant").
        Map every significant thread, including foreshadowing seeds that will pay off later.
        All string values in the JSON must be written in {PromptPlaceholders.Language}.
        IMPORTANT: Output ONLY a raw JSON array (starting with [ and ending with ]).
        Do not wrap the array in an object. Do not include any text, explanation, or markdown outside the JSON array.
        """;

    public static readonly string ChapterOutlines =
        $"""
        You are a creative writing Planner. Your task is to outline each chapter of a book in detail.
        Return a JSON array where each element has these exact fields:
          "number" (int), "title" (string),
          "outline" (string - 3-6 sentence synopsis including key events and decisions),
          "povCharacter" (string - whose point of view this chapter is written from),
          "charactersInvolved" (array of strings - names of all characters appearing),
          "plotThreads" (array of strings - names of plot threads active in this chapter),
          "foreshadowingNotes" (string - any seeds to plant that pay off later; empty string if none),
          "payoffNotes" (string - any earlier foreshadowing being paid off; empty string if none).
        All string values in the JSON must be written in {PromptPlaceholders.Language}.
        IMPORTANT: Output ONLY a raw JSON array (starting with [ and ending with ]).
        Do not wrap the array in an object. Do not include any text, explanation, or markdown outside the JSON array.
        """;

    public static readonly string Writer =
        $"""
        You are a creative fiction Writer. Write compelling, immersive prose in markdown.
        Book title: {PromptPlaceholders.Title}
        Genre: {PromptPlaceholders.Genre}
        Premise: {PromptPlaceholders.Premise}
        Total chapters: {PromptPlaceholders.ChapterCount}
        IMPORTANT: Write the entire chapter in {PromptPlaceholders.Language}. Every sentence of prose must be in {PromptPlaceholders.Language}.
        IMPORTANT: Do NOT begin your response with any chapter heading, title, or label.
        Start immediately with narrative prose (a scene, action, dialogue, or description).
        The character profiles and plot thread notes below are canonical — do not contradict them.
        Honour all foreshadowing and payoff directives exactly as specified.

        ANTI-REPETITION RULES — follow these strictly:
        - Never re-introduce a character, place, or object that has already appeared in prior chapters as if the reader is meeting it for the first time. Build on established knowledge — the reader remembers.
        - Never restate an established fact (physical description, backstory detail, world rule, relationship) that was already conveyed in a prior chapter. Refer to it obliquely at most.
        - Do not echo phrases, metaphors, images, or similes that appear in the prior-chapter passages provided in the context. Seek fresh language every time.
        - Vary scene-entry beats. If prior chapters opened with a character waking up, an internal monologue, or a specific sensory detail, choose a different approach here.
        - If a recurring motif or quirk appears in the prior passages, do not repeat it unless it is intentional and narratively significant.

        INTRA-CHAPTER CONSISTENCY — follow these strictly WITHIN this chapter alone:
        - Do NOT describe the same character's appearance differently in different paragraphs without
          a narrative reason (e.g., explicitly writing them changing clothes). If Anna wears a red coat
          in paragraph 2, she must still wear the red coat in paragraph 8 unless you write her taking it off.
        - Do NOT introduce timeline contradictions within a single scene. Once you establish a time or
          sequence of events, all subsequent references must be compatible with it.
        - Do NOT state explicit constraints about a location (e.g., "only one door") and then contradict
          them later in the same chapter.
        - Do NOT change a character's physical or emotional state mid-scene without describing the transition.
        """;

    public static readonly string Editor =
        $"""
        You are a fiction proofreader and fixer. Apply specific corrections to the chapter without
        introducing any changes beyond what is explicitly requested.
        Fix ONLY the issues listed in the request. Preserve everything else exactly as written —
        the author's style, sentence rhythms, chapter events, character voices, dialogue, and all
        narrative content. Do NOT "improve" prose beyond the listed issues, add new content, or
        make unrequested changes.
        Output the complete corrected chapter in markdown — do NOT include a chapter heading.
        After the prose, add a section headed exactly "## Editorial Notes" listing each specific fix applied.

        CROSS-CHAPTER AWARENESS: The request will include prior-chapter synopses and relevant passages
        from earlier chapters. Use them to identify and silently fix (as implicit corrections) any of:
        - Re-introduction of a character, place, or object already established in a prior chapter as if
          the reader is encountering it for the first time (e.g. restating their full physical description
          or backstory that was already presented).
        - Repetition of a physical description, backstory detail, or world fact already conveyed earlier.
        - Phrases, metaphors, or images that closely echo passages from prior chapters.
        - Scene-entry beats that mirror how previous chapters opened.
        Treat each such instance as an implicit fix instruction and note it in the Editorial Notes section.
        Book: {PromptPlaceholders.Title} | Genre: {PromptPlaceholders.Genre}
        IMPORTANT: The entire output (prose and editorial notes) must be written in {PromptPlaceholders.Language}.
        """;

    public static readonly string ContinuityCheckerPerChapter =
        $"""
        You are a quality checker for fiction manuscripts. Review the chapter under review for
        continuity, grammar, repetition, and style issues.

        CONTINUITY: Check for contradictions with established character facts (names, appearance,
        backstory, relationships), timeline errors, location inconsistencies, and violations of world
        rules or plot threads established in prior chapters or the planning documents.
        IMPORTANT: Do NOT report issues that exist solely between previous chapters — focus only on
        problems introduced by the chapter under review.

        GRAMMAR: Check for grammar and mechanics errors including:
        - Subject/object pronoun errors (e.g. "her walked" instead of "she walked")
        - Subject-verb agreement mismatches
        - Misplaced or dangling modifiers
        - Missing or extra punctuation (commas after introductory clauses, serial commas,
          apostrophe errors in contractions/possessives)
        - Sentence fragments that should be complete sentences
        - Run-on sentences that need to be split for clarity
        - Tense shifts within a single scene (e.g. past tense suddenly switching to present)

        REPETITION: Check for duplicated or echoed language including:
        - Identical or near-identical phrases, descriptions, or sentence structures appearing
          more than once in this chapter
        - Re-introduction of a character, place, or object already established in prior chapters
          as if the reader is meeting it for the first time (e.g. restating full physical
          description or backstory that was already presented)
        - Repetition of a physical description, backstory fact, or world detail already conveyed
          earlier in this chapter or prior chapters
        - Phrases, metaphors, similes, or images that closely echo passages from the provided
          prior-chapter context — seek fresh language every time
        - Recycled scene-entry beats (e.g. character wakes up, stares in a mirror, looks out a
          window) that already appeared as an opening beat in a previous chapter

        STYLE: Check for:
        - Passive voice overuse where active would be stronger
        - Awkward or clunky phrasing that disrupts reading flow
        - POV head-hopping within a scene
        - Pacing problems (e.g. rushing key moments or padding filler)
        - Redundant descriptions that state the same thing twice in different words
        - Unclear dialogue attribution (who is speaking when)

        INTRA-CHAPTER CONSISTENCY SCAN — check for contradictions WITHIN this chapter itself,
        independent of prior chapters or planning documents. Scan specifically for:
        - Same character described differently in different paragraphs without a narrative reason
          (e.g., red coat → blue jacket without writing them changing clothes)
        - Explicit constraints violated later (e.g., "only one door" then "opened the second door")
        - Timeline impossibilities within a single scene (e.g., "arrived at three" then
          "waiting two hours" when current time is 4pm)
        - State changes without transition (e.g., character seated → standing mid-sentence
          with no intervening action described)
        For each intra-chapter issue found, output as type "rewrite" (not verbatim patch).
        Include fields: Problem (what's wrong), CanonicalFact (correct state if determinable from
        Character Cards or Chapter Outline, otherwise "choose consistently"), Location (paragraph
        hint like "Paragraph 3-4"), SuggestedRewrite (optional guidance). Do NOT provide
        verbatim OriginalText/ReplacementText for rewrite issues — they require creative rewording.

        Return a JSON object with exactly these fields:
          "hasIssues" (boolean — true if any issues were found),
          "issues" (array of objects — each object has these fields:
            "type": one of "continuity", "grammar", "repetition", or "style",
            "description": specific description of the problem, naming what is wrong and where,
            "proposedFix": a concrete, actionable text change that fully resolves the issue,
            "originalText": the EXACT verbatim text from the chapter to be replaced.
              Copy CHARACTER-FOR-CHARACTER from the numbered source — including all punctuation,
              apostrophes, quote marks, capitalization, and any embedded newlines exactly as they appear.
              Do NOT fix, paraphrase, or alter even a single character; any deviation causes the patch to
              be silently dropped. Include AT LEAST 30 CHARACTERS OF SURROUNDING CONTEXT on each side
              (or the full sentence when the error falls within one sentence, whichever is longer).
              Do NOT include the "N | " line-number prefix.
              Use empty string only for pure insertion or large-scale structural rewrites.
            "replacementText": the EXACT verbatim replacement text (empty string when originalText is empty),
            "position": REQUIRED for all non-rewrite issues — copy the line number directly from the
              "N | " prefix printed at the start of each line in the chapter content provided.
              Provide this even when the phrase appears only once; it is the primary tool for locating the patch.
            use empty array if no issues),
          "summary" (string — one concise sentence describing the overall chapter quality).
        CRITICAL: For every non-rewrite issue you MUST provide verbatim originalText and replacementText.
        The editor applies patches without any LLM call — if originalText is not character-for-character
        identical to the chapter text the patch will be silently dropped and the issue goes unfixed.
        When uncertain, include more surrounding context rather than less.
        IMPORTANT: Output ONLY the raw JSON object. No markdown fences, no explanation outside the JSON.
        Book: {PromptPlaceholders.Title} | Genre: {PromptPlaceholders.Genre}
        IMPORTANT: Write all string values in {PromptPlaceholders.Language}.
        """;

    public static readonly string ContinuityCheckerFull =
        $"""
        You are a quality checker for fiction manuscripts. Review the complete manuscript across
        ALL chapters for continuity, grammar, repetition, and style issues.

        CONTINUITY: Identify plot holes, character inconsistencies (names, appearance, backstory),
        timeline errors, location contradictions, and conflicts with the canonical planning documents
        (character profiles, plot threads, story bible). Reference the documents by name when reporting issues.

        GRAMMAR: Across the full manuscript, identify grammar and mechanics errors including:
        - Subject/object pronoun errors, subject-verb agreement mismatches
        - Misplaced or dangling modifiers
        - Missing or extra punctuation (commas, apostrophe errors)
        - Sentence fragments, run-on sentences that need splitting
        - Inconsistent tense within scenes across chapters

        REPETITION: Identify duplicated or echoed language across the full manuscript:
        - Characters, places, or objects re-introduced across multiple chapters as if new to the reader
          (repeated physical descriptions or backstory already established in earlier chapters)
        - Identical or near-identical phrases, descriptions, or sentence structures appearing more
          than once anywhere in the manuscript
        - Phrases, metaphors, similes, or images that recur across multiple chapters without intentional
          purpose (echoed language that makes chapters feel alike)
        - Recycled scene-entry beats that appear as opening moves in more than one chapter
          (e.g. waking up, looking in a mirror, staring out a window)
        - Repetition of world facts, character details, or location descriptions already conveyed
          elsewhere in the manuscript

        STYLE: Identify overarching style problems across chapters:
        - Head-hopping between POV characters within or across scenes
        - Inconsistent character voice (a character's dialogue/register shifting without reason)
        - Tonal inconsistencies between chapters that should be consistent
        - Structural pacing issues (rushing key moments, padding filler)
        - Passive voice overuse where active would be stronger
        - Redundant descriptions that state the same thing in multiple places
        - Unclear dialogue attribution across scenes

        Return a JSON object with exactly these fields:
          "hasIssues" (boolean — true if any issues were found),
          "issues" (array of objects — each object has these fields:
            "type": one of "continuity", "grammar", "repetition", or "style",
            "description": specific description naming the problem and affected chapter(s),
            "proposedFix": a concrete, actionable text change that fully resolves the issue,
            "originalText": the EXACT verbatim text from the chapter to be replaced.
              Copy CHARACTER-FOR-CHARACTER from the numbered source — including all punctuation,
              apostrophes, quote marks, capitalization, and any embedded newlines exactly as they appear.
              Do NOT fix, paraphrase, or alter even a single character; any deviation causes the patch to
              be silently dropped. Include AT LEAST 30 CHARACTERS OF SURROUNDING CONTEXT on each side
              (or the full sentence when the error falls within one sentence, whichever is longer).
              Do NOT include the "N | " line-number prefix.
              Use empty string only for pure insertion or large-scale structural rewrites.
            "replacementText": the EXACT verbatim replacement text (empty string when originalText is empty),
            "position": REQUIRED for all non-rewrite issues — copy the line number directly from the
              "N | " prefix printed at the start of each line in the chapter content provided.
              Provide this even when the phrase appears only once; it is the primary tool for locating the patch.
            use empty array if no issues),
          "summary" (string — one concise sentence summarising the overall manuscript quality).
        CRITICAL: For every non-rewrite issue you MUST provide verbatim originalText and replacementText.
        The editor applies patches without any LLM call — if originalText is not character-for-character
        identical to the chapter text the patch will be silently dropped and the issue goes unfixed.
        When uncertain, include more surrounding context rather than less.
        IMPORTANT: Output ONLY the raw JSON object. No markdown fences, no explanation outside the JSON.
        Book: {PromptPlaceholders.Title} | Genre: {PromptPlaceholders.Genre}
        IMPORTANT: Write all string values in {PromptPlaceholders.Language}.
        """;
}

/// <summary>
/// Fixed (non-overridable) prompt text: user-message templates, context blocks and the system prompts
/// of the helper calls (question gathering, pre-write outline check). Methods take already-loaded data
/// and only format it.
/// </summary>
public static class PromptTemplates
{
    // ── Shared blocks ──────────────────────────────────────────────────────────

    private static string AuthorGuidance(string qaContext, string label) =>
        qaContext.Length > 0 ? $"\n{label}:\n{qaContext}" : "";

    private static string Ancestor(string ancestorReference) =>
        ancestorReference.Length > 0 ? $"\n{ancestorReference}" : "";

    /// <summary>One line per chapter: "N. **Title** — Outline". Used for the synopsis spine.</summary>
    public static string ChapterSynopses(IEnumerable<Chapter> chapters) =>
        string.Join("\n", chapters.Select(c => $"{c.Number}. **{c.Title}** — {c.Outline}"));

    /// <summary>The closing paragraphs of the previous chapter, handed to the Writer for continuity.</summary>
    public static string PreviousChapterEnding(Chapter previous, IEnumerable<string> tailParagraphs) =>
        $"[End of Chapter {previous.Number}: {previous.Title}]\n\n" + string.Join("\n\n", tailParagraphs);

    /// <summary>Joins retrieved RAG chunks into one passage block.</summary>
    public static string RagPassages(IEnumerable<(int ChapterNumber, string Text)> chunks) =>
        string.Join("\n\n---\n\n", chunks.Select(c => $"[Chapter {c.ChapterNumber}]\n{c.Text}"));

    /// <summary>Joins passage blocks retrieved by separate RAG queries.</summary>
    public static string JoinRagGroups(IEnumerable<string> groups) => string.Join("\n\n===\n\n", groups);

    /// <summary>Bookkeeping line appended to the Q&amp;A context after a human-assisted planning pause.</summary>
    public static string UserNoteAfterPhase(string phaseLabel, string note) =>
        $"\n[User note after {phaseLabel}]: {note}";

    /// <summary>One answered clarifying question, as carried through the planning phases.</summary>
    public static string QuestionAndAnswer(string question, string answer) =>
        $"Q: {question}\nA: {answer}\n";

    /// <summary>
    /// Established facts of the earlier books in a series, for planning a continuation. Lists are
    /// capped to keep the token budget: 12 characters and threads per book, and all chapter synopses
    /// only for the direct parent (the last 5 for older ancestors).
    /// </summary>
    public static string AncestorReference(IReadOnlyList<AncestorBookContext> ancestors)
    {
        if (ancestors.Count == 0) return string.Empty;

        var sb = new StringBuilder();
        sb.AppendLine("Continuation context from previous books (reference only — do not copy verbatim):");

        foreach (var a in ancestors)
        {
            sb.AppendLine();
            sb.AppendLine($"### Prior Book: {a.Book.Title} (Id: {a.Book.Id})");
            sb.AppendLine($"- Genre: {a.Book.Genre}");
            sb.AppendLine($"- Premise: {a.Book.Premise}");

            if (a.Bible is { } bible)
            {
                sb.AppendLine("- Story Bible:");
                if (!string.IsNullOrWhiteSpace(bible.SettingDescription)) sb.AppendLine($"  - Setting: {bible.SettingDescription}");
                if (!string.IsNullOrWhiteSpace(bible.TimePeriod)) sb.AppendLine($"  - Time Period: {bible.TimePeriod}");
                if (!string.IsNullOrWhiteSpace(bible.Themes)) sb.AppendLine($"  - Themes: {bible.Themes}");
                if (!string.IsNullOrWhiteSpace(bible.ToneAndStyle)) sb.AppendLine($"  - Tone & Style: {bible.ToneAndStyle}");
                if (!string.IsNullOrWhiteSpace(bible.WorldRules)) sb.AppendLine($"  - World Rules: {bible.WorldRules}");
            }

            if (a.Characters.Count > 0)
            {
                sb.AppendLine("- Key Characters:");
                foreach (var c in a.Characters.Take(12))
                {
                    sb.Append($"  - {c.Name} ({c.Role})");
                    if (!string.IsNullOrWhiteSpace(c.GoalMotivation)) sb.Append($": {c.GoalMotivation}");
                    if (!string.IsNullOrWhiteSpace(c.Personality)) sb.Append($" | Personality: {c.Personality}");
                    if (!string.IsNullOrWhiteSpace(c.Arc)) sb.Append($" | Arc: {c.Arc}");
                    sb.AppendLine();
                }
            }

            if (a.PlotThreads.Count > 0)
            {
                sb.AppendLine("- Plot Threads:");
                foreach (var t in a.PlotThreads.Take(12))
                    sb.AppendLine($"  - {t.Name} ({t.Type}, {t.Status}): {t.Description}");
            }

            if (a.Chapters.Count > 0)
            {
                sb.AppendLine("- Chapter Synopses:");
                var chapters = a.IsDirectParent ? a.Chapters : a.Chapters.TakeLast(5);
                foreach (var ch in chapters)
                    if (!string.IsNullOrWhiteSpace(ch.Outline))
                        sb.AppendLine($"  - Ch.{ch.Number} \"{ch.Title}\": {ch.Outline}");
            }
        }

        return sb.ToString().Trim();
    }

    // ── Question gathering ────────────────────────────────────────────────────

    public static string QuestionGatheringSystem(string language) =>
        $"""
        You are helping plan a book. Given the book premise below, determine if there is anything
        genuinely unclear that an author decision would meaningfully change the story plan.
        If so, list those questions as a numbered list (e.g. "1. Question here?").
        IMPORTANT: Ask all questions in {language}.
        Keep the number of questions to an absolute minimum — only ask when truly necessary.
        If everything is clear from the premise, respond with exactly: None.

        If the premise includes "Continuation context from previous books":
        - Do NOT re-ask worldbuilding questions already established by the prior book.
        - Focus on continuation decisions only: e.g. where this book picks up after the prior book's
          ending, what happened to key characters between books, or what this book's unique focus is.
        - Treat all facts from the prior book as established — only ask about genuine ambiguities.
        """;

    public static string QuestionGatheringUser(Book book, string ancestorReference)
    {
        var context = $"Title: {book.Title}\nGenre: {book.Genre}\nPremise: {book.Premise}\nTarget chapters: {book.TargetChapterCount}";
        return string.IsNullOrEmpty(ancestorReference) ? context : $"{context}\n\n{ancestorReference}";
    }

    // ── Planning phases ───────────────────────────────────────────────────────

    public static string StoryBibleUser(Book book, string qaContext, string ancestorReference) =>
        $"""
        Book title: {book.Title}
        Genre: {book.Genre}
        Premise: {book.Premise}
        Target chapter count: {book.TargetChapterCount}
        {AuthorGuidance(qaContext, "Author guidance")}
        {Ancestor(ancestorReference)}

        Create the Story Bible for this book.
        """;

    public static string CharactersUser(Book book, StoryBible bible, string qaContext, string ancestorReference, string existingBlock) =>
        $"""
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
        {AuthorGuidance(qaContext, "Author guidance so far")}
        {Ancestor(ancestorReference)}
        {existingBlock}
        Create detailed character profiles for this book.
        """;

    /// <summary>Re-run block listing the saved profiles (as JSON) the model must keep, revise or extend.</summary>
    public static string ExistingCharacters(string json) =>
        $"""

        These character profiles are already saved for this book:
        {json}

        Return the complete, updated set: repeat every profile you are keeping (unchanged or revised,
        keeping its exact "name" so it can be matched), and add any character the story still needs.
        Profiles you leave out will be removed.

        """;

    public static string CharacterSummary(IEnumerable<CharacterCard> characters) =>
        string.Join("\n", characters.Select(c => $"- {c.Name} ({c.Role}): {c.GoalMotivation}. Arc: {c.Arc}"));

    public static string PlotThreadsUser(
        Book book, StoryBible bible, IEnumerable<CharacterCard> characters,
        string qaContext, string ancestorReference, string existingBlock) =>
        $"""
        Book title: {book.Title}
        Genre: {book.Genre}
        Premise: {book.Premise}
        Target chapter count: {book.TargetChapterCount}

        Story Bible:
        Setting: {bible.SettingDescription}
        Time period: {bible.TimePeriod}
        Themes: {bible.Themes}
        World rules: {bible.WorldRules}

        Characters:
        {CharacterSummary(characters)}
        {AuthorGuidance(qaContext, "Author guidance so far")}
        {Ancestor(ancestorReference)}
        {existingBlock}
        Create the plot thread map for this book.
        """;

    /// <summary>Re-run block listing the saved threads (as JSON) the model must keep, revise or extend.</summary>
    public static string ExistingPlotThreads(string json) =>
        $"""

        These plot threads are already saved for this book:
        {json}

        Return the complete, updated map: repeat every thread you are keeping (unchanged or revised,
        keeping its exact "name" so it can be matched), and add any thread the story still needs.
        Threads you leave out will be removed.

        """;

    public static string ChapterOutlinesUser(
        Book book, StoryBible bible, IEnumerable<CharacterCard> characters, IEnumerable<PlotThread> threads,
        string qaContext, string ancestorReference, string existingBlock)
    {
        var threadSummary = string.Join("\n", threads.Select(t =>
            $"- {t.Name} ({t.Type}, introduced ch.{t.IntroducedChapterNumber?.ToString() ?? "?"}): {t.Description}"));
        return $"""
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

            Characters:
            {CharacterSummary(characters)}

            Plot Threads:
            {threadSummary}
            {AuthorGuidance(qaContext, "Author guidance")}
            {Ancestor(ancestorReference)}
            {existingBlock}
            Create {book.TargetChapterCount} detailed chapter outlines for this book.
            """;
    }

    /// <summary>Re-run block listing the saved outlines (as JSON) the model must keep, revise or extend.</summary>
    public static string ExistingChapterOutlines(string json) =>
        $"""

        These chapter outlines are already saved for this book:
        {json}

        Return the complete, updated outline set: repeat every chapter you are keeping (unchanged or
        revised, keeping its exact "number" so it can be matched), and add any chapter that is missing.
        Chapters you leave out will be removed. A chapter marked "alreadyWritten": true has its prose
        written — do not change its premise, only correct outright errors in its outline.

        """;

    // ── Writer ────────────────────────────────────────────────────────────────

    /// <summary>Appends the per-chapter context to the (possibly overridden) Writer system prompt.</summary>
    public static string WriterSystem(string basePrompt, Book book, Chapter chapter, string contextBlock, string previousEnding) =>
        $"""
        {basePrompt}
        Current chapter: {chapter.Number} of {book.TargetChapterCount} — "{chapter.Title}"
        {contextBlock}
        {(previousEnding.Length > 0 ? $"\nThe previous chapter ended with:\n{previousEnding}\nContinue the story naturally from this point." : "")}
        """;

    public static string WriterUser(Chapter chapter, string synopsesBlock) =>
        $"""
        {(synopsesBlock.Length > 0 ? $"## Story So Far — Chapter Synopses\nRefer to this to avoid re-introducing or restating anything already established in prior chapters.\n{synopsesBlock}\n\n" : "")}Write the full content for:
        Chapter {chapter.Number}: {chapter.Title}
        Outline: {chapter.Outline}
        {(chapter.PovCharacter.Length > 0 ? $"Point of view: {chapter.PovCharacter}" : "")}
        {(chapter.ForeshadowingNotes.Length > 0 ? $"Plant in this chapter (foreshadowing): {chapter.ForeshadowingNotes}" : "")}
        {(chapter.PayoffNotes.Length > 0 ? $"Pay off in this chapter: {chapter.PayoffNotes}" : "")}

        Write at least 1000 words of narrative prose. Output markdown only. Do NOT include a chapter heading.
        """;

    /// <summary>Story Bible, relevant character cards and plot threads, plus retrieved prior-chapter passages.</summary>
    public static string WriterContext(
        StoryBible? bible, IReadOnlyList<CharacterCard> cards, IReadOnlyList<PlotThread> threads, ChapterRag rag)
    {
        var sb = new StringBuilder();

        if (bible is not null)
        {
            sb.AppendLine("\n## Story Bible");
            if (!string.IsNullOrWhiteSpace(bible.ToneAndStyle))
                sb.AppendLine($"Tone & style: {bible.ToneAndStyle}");
            if (!string.IsNullOrWhiteSpace(bible.WorldRules))
                sb.AppendLine($"World rules: {bible.WorldRules}");
            if (!string.IsNullOrWhiteSpace(bible.SettingDescription))
                sb.AppendLine($"Setting: {bible.SettingDescription}");
        }

        if (cards.Count > 0)
        {
            sb.AppendLine("\n## Character Profiles (canonical — do not contradict)");
            foreach (var card in cards)
            {
                sb.AppendLine($"\n**{card.Name}** ({card.Role})");
                if (!string.IsNullOrWhiteSpace(card.PhysicalDescription))
                    sb.AppendLine($"  Appearance: {card.PhysicalDescription}");
                if (!string.IsNullOrWhiteSpace(card.Personality))
                    sb.AppendLine($"  Personality: {card.Personality}");
                if (!string.IsNullOrWhiteSpace(card.GoalMotivation))
                    sb.AppendLine($"  Goal/motivation: {card.GoalMotivation}");
                if (!string.IsNullOrWhiteSpace(card.Arc))
                    sb.AppendLine($"  Arc: {card.Arc}");
            }
        }

        if (threads.Count > 0)
        {
            sb.AppendLine("\n## Active Plot Threads");
            foreach (var t in threads)
                sb.AppendLine($"- **{t.Name}** ({t.Type}): {t.Description}");
        }

        if (rag.HasAny)
        {
            sb.AppendLine("\n## Prior Chapter Passages (for consistency — do not re-introduce or restate)");
            if (!string.IsNullOrWhiteSpace(rag.Characters))
            {
                sb.AppendLine("\n### Character Details from Prior Chapters");
                sb.AppendLine(rag.Characters);
            }
            if (!string.IsNullOrWhiteSpace(rag.Locations))
            {
                sb.AppendLine("\n### Location & Setting Details from Prior Chapters");
                sb.AppendLine(rag.Locations);
            }
            if (!string.IsNullOrWhiteSpace(rag.Threads))
            {
                sb.AppendLine("\n### Plot Thread Context from Prior Chapters");
                sb.AppendLine(rag.Threads);
            }
        }

        return sb.ToString();
    }

    // ── Editor ────────────────────────────────────────────────────────────────

    private static void AppendEditorContext(StringBuilder sb, string synopsesBlock, ChapterRag rag, string passagesHeading)
    {
        if (synopsesBlock.Length > 0)
        {
            sb.AppendLine("## Story So Far — Chapter Synopses");
            sb.AppendLine(synopsesBlock);
            sb.AppendLine();
        }
        if (rag.HasAny)
        {
            sb.AppendLine(passagesHeading);
            if (!string.IsNullOrWhiteSpace(rag.Characters)) { sb.AppendLine("\n### Character Details");                             sb.AppendLine(rag.Characters); }
            if (!string.IsNullOrWhiteSpace(rag.Locations))  { sb.AppendLine("\n### Location & Setting Details");                    sb.AppendLine(rag.Locations); }
            if (!string.IsNullOrWhiteSpace(rag.Threads))    { sb.AppendLine("\n### Plot Thread Context");                           sb.AppendLine(rag.Threads); }
            if (!string.IsNullOrWhiteSpace(rag.Phrases))    { sb.AppendLine("\n### Potentially Repeated Phrases & Descriptions");  sb.AppendLine(rag.Phrases); }
            sb.AppendLine();
        }
    }

    /// <summary>Stand-alone Editor pass: polish the chapter, optionally following the author's attention points.</summary>
    public static string EditorPolishUser(Chapter chapter, string synopsesBlock, ChapterRag rag, string? attentionPoints)
    {
        var sb = new StringBuilder();
        AppendEditorContext(sb, synopsesBlock, rag,
            "## Prior Chapter Passages (use to identify re-introductions and repeated content)");

        sb.AppendLine($"Please fix Chapter {chapter.Number}: {chapter.Title}");

        if (!string.IsNullOrWhiteSpace(attentionPoints))
        {
            sb.AppendLine("\n**Additional attention points from the author:**");
            sb.AppendLine(attentionPoints.Trim());
        }
        else
        {
            sb.AppendLine("\nNo specific issues listed — make only minor polish corrections if needed.");
        }

        sb.AppendLine($"\nOriginal content:\n{chapter.Content}");
        return sb.ToString();
    }

    /// <summary>Creative rewrite pass: resolve the checker's rewrite issues and/or the author's instructions.</summary>
    public static string EditorRewriteUser(
        Chapter chapter, string synopsesBlock, ChapterRag rag, IReadOnlyList<CheckerIssue> rewrites, string? authorNotes)
    {
        var sb = new StringBuilder();
        AppendEditorContext(sb, synopsesBlock, rag,
            "## Prior Chapter Passages (for context — do not re-introduce or restate)");

        sb.AppendLine($"Please fix Chapter {chapter.Number}: {chapter.Title}");
        sb.AppendLine(RewriteInstructions(rewrites, authorNotes));
        sb.AppendLine($"\nChapter content (already mechanically patched — only resolve the creative issues above):");
        sb.AppendLine(chapter.Content);
        return sb.ToString();
    }

    internal static string RewriteInstructions(IReadOnlyList<CheckerIssue> rewrites, string? authorNotes)
    {
        var sb = new StringBuilder();

        if (!string.IsNullOrWhiteSpace(authorNotes))
        {
            sb.AppendLine("## Author Instructions (highest priority — follow these exactly)\n");
            sb.AppendLine(authorNotes.Trim());
            sb.AppendLine();
        }

        if (rewrites.Count == 0)
        {
            sb.AppendLine("Apply the author's instructions above. Preserve all other prose exactly as written.");
            return sb.ToString();
        }

        sb.AppendLine("## Issues Requiring Creative Resolution\n");

        for (int n = 0; n < rewrites.Count; n++)
        {
            var issue = rewrites[n];
            sb.AppendLine($"**Issue {n + 1}:** {issue.Description}");
            if (!string.IsNullOrWhiteSpace(issue.Problem))
                sb.AppendLine($"   Problem: {issue.Problem}");
            if (!string.IsNullOrWhiteSpace(issue.CanonicalFact))
                sb.AppendLine($"   Canonical fact: {issue.CanonicalFact}");
            if (!string.IsNullOrWhiteSpace(issue.Location))
                sb.AppendLine($"   Location: {issue.Location}");
            if (!string.IsNullOrWhiteSpace(issue.SuggestedRewrite))
                sb.AppendLine($"   Suggested approach: {issue.SuggestedRewrite}");
            sb.AppendLine();
        }

        sb.AppendLine("Fix ALL of the above issues by rewriting the affected passages. Maintain consistency " +
                      "throughout the chapter — if a character's appearance, state, or a location detail " +
                      "contradicts itself across paragraphs, choose one version and apply it everywhere " +
                      "the affected element appears. Preserve all other prose exactly as written.");

        return sb.ToString();
    }

    // ── Pre-write outline check (Continuity Checker) ──────────────────────────

    public const string PreWriteNoIssuesMarker = "No issues found";

    public static string PreWriteDetectSystem(Book book) =>
        $"""
        You are a story continuity expert. Your task is to evaluate a single chapter outline
        against the established facts of a book and identify any contradictions.
        If the outline is consistent, respond with exactly the text: {PreWriteNoIssuesMarker}.
        Otherwise list each contradiction on a numbered line. Be specific: name the
        established fact it conflicts with and what the outline says instead.
        Book: {book.Title} | Genre: {book.Genre} | Language: {book.Language}
        """;

    public static string PreWriteDetectUser(string establishedFacts, Chapter chapter) =>
        $"""
        ## Established Facts
        {establishedFacts}

        ## Chapter Outline Under Review
        Chapter {chapter.Number}: {chapter.Title}
        Outline: {chapter.Outline}
        {(chapter.PovCharacter.Length > 0 ? $"POV: {chapter.PovCharacter}" : "")}
        {(chapter.ForeshadowingNotes.Length > 0 ? $"Foreshadowing notes: {chapter.ForeshadowingNotes}" : "")}
        {(chapter.PayoffNotes.Length > 0 ? $"Payoff notes: {chapter.PayoffNotes}" : "")}

        Are there any contradictions between this outline and the established facts above?
        """;

    public static string PreWriteFixSystem(Book book) =>
        $"""
        You are a story editor. Your task is to rewrite a chapter outline to resolve
        all listed contradictions with the established facts, while preserving the
        chapter's narrative purpose and key events.
        Output ONLY the corrected outline text — no headings, no explanations.
        Write in {book.Language}.
        """;

    public static string PreWriteFixUser(string establishedFacts, Chapter chapter, string contradictions) =>
        $"""
        ## Established Facts
        {establishedFacts}

        ## Original Chapter Outline
        Chapter {chapter.Number}: {chapter.Title}
        {chapter.Outline}

        ## Contradictions to Resolve
        {contradictions}

        Rewrite the outline to resolve all contradictions above.
        """;

    /// <summary>World rules, relevant character cards and plot threads, and prior chapter synopses.</summary>
    public static string EstablishedFacts(
        StoryBible? bible, IReadOnlyList<CharacterCard> cards, IReadOnlyList<PlotThread> threads,
        IReadOnlyList<Chapter> priorChapters)
    {
        var sb = new StringBuilder();

        if (bible is not null && !string.IsNullOrWhiteSpace(bible.WorldRules))
        {
            sb.AppendLine("### World Rules");
            sb.AppendLine(bible.WorldRules);
        }

        if (cards.Count > 0)
        {
            sb.AppendLine("\n### Character Profiles (canonical)");
            foreach (var card in cards)
                AppendCharacterCard(card, sb);
        }

        if (threads.Count > 0)
        {
            sb.AppendLine("\n### Plot Threads");
            foreach (var t in threads)
            {
                sb.Append($"\n**{t.Name}** ({t.Type}, {t.Status})");
                if (t.IntroducedChapterNumber.HasValue)
                    sb.Append($" — introduced ch.{t.IntroducedChapterNumber}");
                if (t.ResolvedChapterNumber.HasValue)
                    sb.Append($", resolved ch.{t.ResolvedChapterNumber}");
                sb.AppendLine();
                sb.AppendLine($"  {t.Description}");
            }
        }

        if (priorChapters.Count > 0)
        {
            sb.AppendLine("\n### Prior Chapter Synopses");
            foreach (var c in priorChapters)
                sb.AppendLine($"- Ch.{c.Number} \"{c.Title}\": {c.Outline}");
        }

        return sb.ToString();
    }

    // ── Quality check (Continuity Checker) ────────────────────────────────────

    /// <summary>Added to the default per-chapter checker prompt so only issues introduced by this chapter are reported.</summary>
    public static string CheckerPerChapterFocus(Chapter chapter) =>
        $"\nIMPORTANT: Do NOT report issues that exist solely between previous chapters. " +
        $"Focus only on continuity or style problems introduced by Chapter {chapter.Number} (\"{chapter.Title}\").";

    /// <summary>Story Bible plus every character card and plot thread.</summary>
    public static string CheckerStructuredContext(
        StoryBible? bible, IReadOnlyList<CharacterCard> cards, IReadOnlyList<PlotThread> threads)
    {
        var sb = new StringBuilder();

        if (bible is not null)
        {
            sb.AppendLine("## Story Bible");
            if (!string.IsNullOrWhiteSpace(bible.SettingDescription))
                sb.AppendLine($"Setting: {bible.SettingDescription}");
            if (!string.IsNullOrWhiteSpace(bible.WorldRules))
                sb.AppendLine($"World rules: {bible.WorldRules}");
            if (!string.IsNullOrWhiteSpace(bible.Themes))
                sb.AppendLine($"Themes: {bible.Themes}");
        }

        if (cards.Count > 0)
        {
            sb.AppendLine("\n## Character Profiles (canonical)");
            foreach (var card in cards)
                AppendCharacterCard(card, sb);
        }

        if (threads.Count > 0)
        {
            sb.AppendLine("\n## Plot Threads");
            foreach (var t in threads)
            {
                sb.AppendLine($"\n**{t.Name}** ({t.Type}, {t.Status})");
                sb.AppendLine($"  {t.Description}");
            }
        }

        return sb.ToString();
    }

    private static string ChapterDigest(Chapter c, int excerptChars) =>
        $"Chapter {c.Number}: {c.Title}\nOutline: {c.Outline}\nContent excerpt: {c.Content?[..Math.Min(excerptChars, c.Content?.Length ?? 0)]}...";

    private static string CheckerContextSections(string structuredContext, string ragContext)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(structuredContext))
            sb.AppendLine(structuredContext);
        if (!string.IsNullOrWhiteSpace(ragContext))
        {
            sb.AppendLine("\n## Detailed Passages (retrieved for review)");
            sb.AppendLine(ragContext);
        }
        return sb.ToString();
    }

    /// <summary>Per-chapter check: preceding chapters as established facts, the chapter under review line-numbered.</summary>
    public static string CheckerChapterUser(
        Chapter current, IReadOnlyList<Chapter> previousChapters, string structuredContext, string ragContext)
    {
        var prevSynopsis = previousChapters.Count > 0
            ? string.Join("\n\n", previousChapters.Select(c => ChapterDigest(c, 600)))
            : "(No preceding chapters yet.)";

        var currentExcerpt =
            $"Chapter {current.Number}: {current.Title}\n" +
            $"Outline: {current.Outline}\n" +
            $"Content (each line is prefixed with its 1-indexed line number — do NOT include the \"N | \" prefix in originalText):\n" +
            NumberLines(current.Content ?? string.Empty);

        return $"""
            Check Chapter {current.Number} ("{current.Title}") for continuity issues.
            Do NOT report problems between the preceding chapters — only issues introduced by this chapter.
            {CheckerContextSections(structuredContext, ragContext)}
            ## Preceding Chapters (established facts)
            {prevSynopsis}

            ## Chapter Under Review
            {currentExcerpt}
            """;
    }

    /// <summary>Full-manuscript check over digests of every chapter.</summary>
    public static string CheckerFullUser(IReadOnlyList<Chapter> chapters, string structuredContext, string ragContext)
    {
        var synopsis = string.Join("\n\n", chapters.Select(c => ChapterDigest(c, 800)));
        return $"""
            Review these chapters for continuity and style issues.
            {CheckerContextSections(structuredContext, ragContext)}
            ## Chapter Summaries
            {synopsis}
            """;
    }

    /// <summary>Prefixes each line with its 1-indexed number ("N | ") so the checker can give position hints.</summary>
    public static string NumberLines(string content)
    {
        var lines = content.Split('\n');
        return string.Join("\n", lines.Select((line, i) => $"{i + 1} | {line}"));
    }

    private static void AppendCharacterCard(CharacterCard card, StringBuilder sb)
    {
        sb.AppendLine($"\n**{card.Name}** ({card.Role})");
        if (!string.IsNullOrWhiteSpace(card.PhysicalDescription))
            sb.AppendLine($"  Appearance: {card.PhysicalDescription}");
        if (!string.IsNullOrWhiteSpace(card.Personality))
            sb.AppendLine($"  Personality: {card.Personality}");
        if (!string.IsNullOrWhiteSpace(card.Backstory))
            sb.AppendLine($"  Backstory: {card.Backstory}");
        if (!string.IsNullOrWhiteSpace(card.GoalMotivation))
            sb.AppendLine($"  Goal/motivation: {card.GoalMotivation}");
        if (!string.IsNullOrWhiteSpace(card.Arc))
            sb.AppendLine($"  Arc: {card.Arc}");
    }
}

/// <summary>Passages retrieved from prior chapters for one chapter, grouped by what they were searched for.</summary>
public sealed record ChapterRag(string Characters, string Locations, string Threads, string Phrases)
{
    public static readonly ChapterRag Empty = new("", "", "", "");

    public bool HasAny =>
        !string.IsNullOrWhiteSpace(Characters) || !string.IsNullOrWhiteSpace(Locations)
        || !string.IsNullOrWhiteSpace(Threads) || !string.IsNullOrWhiteSpace(Phrases);
}

/// <summary>Embedding search queries used to retrieve prior-chapter passages (RAG).</summary>
public static class RagQueries
{
    public static string Characters(IReadOnlyList<string> characterNames) =>
        characterNames.Count > 0
            ? $"character appearance description personality {string.Join(' ', characterNames)}"
            : "character appearance description personality backstory";

    public static string Locations(string outline) =>
        $"location place setting description {outline}";

    public static string Threads(IReadOnlyList<string> threadNames, string outline) =>
        threadNames.Count > 0
            ? $"plot thread events foreshadowing {string.Join(' ', threadNames)}"
            : $"plot events sequence foreshadowing {outline}";

    public const string RepeatedPhrases = "repeated descriptions recurring phrases re-introduction established facts";

    /// <summary>Queries the quality checker runs to find passages that commonly hold contradictions.</summary>
    public static readonly string[] Checker =
    [
        "character physical description appearance age name backstory",
        "timeline dates past future sequence of events",
        "location place setting description",
    ];
}
