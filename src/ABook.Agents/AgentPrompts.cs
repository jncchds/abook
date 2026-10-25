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

    /// <summary>
    /// The visual style set in the image-generation settings (LLM configuration or preset).
    /// Resolved by the illustration planner.
    /// </summary>
    public const string VisualStyle = "{VISUAL_STYLE}";

    /// <summary>
    /// Whether the image model applies negative prompts at the configured CFG (Z-Image Turbo at CFG 1.0
    /// ignores them). Resolved by the illustration planner.
    /// </summary>
    public const string NegativePrompts = "{NEGATIVE_PROMPTS}";
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
          "firstAppearanceChapterNumber" (int or null - the number of the section of the book, in order,
            where the character first appears), "notes" (string).
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
          "introducedChapterNumber" (int or null), "resolvedChapterNumber" (int or null) - the numbers of the
            sections of the book (its parts, in order) where the thread starts and is resolved,
          "status" ("Active"|"Resolved"|"Dormant").
        Map every significant thread, including foreshadowing seeds that will pay off later.
        All string values in the JSON must be written in {PromptPlaceholders.Language}.
        IMPORTANT: Output ONLY a raw JSON array (starting with [ and ending with ]).
        Do not wrap the array in an object. Do not include any text, explanation, or markdown outside the JSON array.
        """;

    public static readonly string ChapterOutlines =
        $"""
        You are a creative writing Planner. Your task is to outline the book section by section, in detail.
        A section is one part of the story, in order. Most sections are told in a single chapter; a section
        with more events than one chapter can hold is told in several chapters.
        Return a JSON array where each element (one section) has these exact fields:
          "number" (int - the section's number, 1, 2, 3, …), "title" (string),
          "outline" (string - 3-6 sentence synopsis including key events and decisions),
          "povCharacter" (string - whose point of view this section is written from),
          "charactersInvolved" (array of strings - names of all characters appearing),
          "plotThreads" (array of strings - names of plot threads active in this section),
          "foreshadowingNotes" (string - any seeds to plant that pay off later; empty string if none),
          "payoffNotes" (string - any earlier foreshadowing being paid off; empty string if none),
          "chapters" (int - how many chapters this section needs: 1 for most, 2 or 3 only for a section
            whose events clearly need more room).
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
        Planned sections (parts of the story): {PromptPlaceholders.ChapterCount}
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

    public static readonly string Illustrator =
        $"""
        You are the art director and illustrator of a book. You write the prompts a text-to-image model
        renders into the book's illustrations, its cover and reference portraits of its characters.
        Book: {PromptPlaceholders.Title} | Genre: {PromptPlaceholders.Genre}
        Premise: {PromptPlaceholders.Premise}
        Setting: {PromptPlaceholders.Setting}
        Tone & style: {PromptPlaceholders.Tone}
        Art direction: {PromptPlaceholders.VisualStyle}

        How to write image prompts:
        - Write every prompt in English as flowing natural-language prose, not as a list of tags.
        - The book's art direction is added to every prompt automatically, so every image shares one style.
          Describe only what the image shows — subject and action, composition and camera framing, setting,
          the lighting and mood of the moment. Do not restate, vary or contradict the art style.
        - Be concrete and visual: the image model knows nothing about the story, so never rely on names or
          plot references — describe only what is seen.
        - Suit the imagery to the book's genre and tone. You decide what to depict and how it is depicted.
        - Character consistency: whenever a character is shown, include their canonical visual description
          word for word and never contradict it. Dress them in their usual outfit; change clothing only when
          the story says so, and then use the same registered outfit in every image where they wear it.
        - Object and place consistency: every recurring prop, outfit, place, creature or vehicle has one
          canonical description — repeat it word for word in every image that shows it.
        - Never rely on the negative prompt. Each image must come out right from its prompt alone: say what
          is there instead of what is not ("an empty street at dawn", not "no people"; "a plain grey backdrop",
          not "no clutter"), and never mention an unwanted element in the prompt, even to exclude it.
        - Negative prompts: {PromptPlaceholders.NegativePrompts}
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

    /// <summary>
    /// The premise with the author's active clarifications appended. Use it wherever the premise reaches
    /// a prompt, so decisions made in the clarification round are never lost.
    /// </summary>
    public static string Premise(Book book)
    {
        var answered = book.Clarifications
            .Where(c => !c.IsArchived && !string.IsNullOrWhiteSpace(c.Answer))
            .OrderBy(c => c.CreatedAt).ThenBy(c => c.Id)
            .ToList();
        if (answered.Count == 0) return book.Premise;
        return $"{book.Premise}\n\nAuthor clarifications:\n" +
               string.Join("\n", answered.Select(c => QuestionAndAnswer(c.Question.Trim(), c.Answer.Trim()).TrimEnd()));
    }

    private static string Ancestor(string ancestorReference) =>
        ancestorReference.Length > 0 ? $"\n{ancestorReference}" : "";

    /// <summary>One line per chapter: "N. **Title** — gist". Used for the synopsis spine.</summary>
    public static string ChapterSynopses(IEnumerable<Chapter> chapters) =>
        string.Join("\n", chapters.Select(c => $"{c.Number}. **{c.Title}** — {Gist(c)}"));

    /// <summary>
    /// What a chapter is about, on one line: the summary of its written prose when there is one (Interactive
    /// books), otherwise its outline — the plan, which written prose may have drifted from.
    /// </summary>
    public static string Gist(Chapter c) =>
        string.IsNullOrWhiteSpace(c.Summary) ? c.Outline : OneLine(c.Summary);

    private static string OneLine(string text) =>
        string.Join(' ', text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    /// <summary>
    /// The chapter count as prompts state it: the author's target, or "open-ended" for a book with no
    /// target (Interactive books grow chapter by chapter).
    /// </summary>
    public static string ChapterCount(Book book) =>
        book.TargetChapterCount > 0 ? book.TargetChapterCount.ToString() : "open-ended";

    /// <summary>The closing paragraphs of the previous chapter, handed to the Writer for continuity.</summary>
    public static string PreviousChapterEnding(Chapter previous, IEnumerable<string> tailParagraphs) =>
        $"[End of Chapter {previous.Number}: {previous.Title}]\n\n" + string.Join("\n\n", tailParagraphs);

    /// <summary>Joins retrieved RAG chunks into one passage block.</summary>
    public static string RagPassages(IEnumerable<(int ChapterNumber, string Text)> chunks) =>
        string.Join("\n\n---\n\n", chunks.Select(c => $"[Chapter {c.ChapterNumber}]\n{c.Text}"));

    /// <summary>Joins passage blocks retrieved by separate RAG queries.</summary>
    public static string JoinRagGroups(IEnumerable<string> groups) => string.Join("\n\n===\n\n", groups);

    /// <summary>One answered clarifying question, as appended to the premise.</summary>
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
            sb.AppendLine($"- Premise: {Premise(a.Book)}");

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
        If the premise ends with "Author clarifications", those questions are already answered — never ask
        them again, nor anything their answers settle.

        If the premise includes "Continuation context from previous books":
        - Do NOT re-ask worldbuilding questions already established by the prior book.
        - Focus on continuation decisions only: e.g. where this book picks up after the prior book's
          ending, what happened to key characters between books, or what this book's unique focus is.
        - Treat all facts from the prior book as established — only ask about genuine ambiguities.
        """;

    public static string QuestionGatheringUser(Book book, string ancestorReference)
    {
        var context = $"Title: {book.Title}\nGenre: {book.Genre}\nPremise: {Premise(book)}\nTarget chapters: {ChapterCount(book)}";
        return string.IsNullOrEmpty(ancestorReference) ? context : $"{context}\n\n{ancestorReference}";
    }

    // ── Planning phases ───────────────────────────────────────────────────────

    public static string StoryBibleUser(Book book, string ancestorReference) =>
        $"""
        Book title: {book.Title}
        Genre: {book.Genre}
        Premise: {Premise(book)}
        Target section count (parts of the story, most told in one chapter): {ChapterCount(book)}
        {Ancestor(ancestorReference)}

        Create the Story Bible for this book.
        """;

    public static string CharactersUser(Book book, StoryBible bible, string ancestorReference, string existingBlock) =>
        $"""
        Book title: {book.Title}
        Genre: {book.Genre}
        Premise: {Premise(book)}
        Target section count (parts of the story, most told in one chapter): {ChapterCount(book)}

        Story Bible:
        Setting: {bible.SettingDescription}
        Time period: {bible.TimePeriod}
        Themes: {bible.Themes}
        Tone & style: {bible.ToneAndStyle}
        World rules: {bible.WorldRules}
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
        string ancestorReference, string existingBlock) =>
        $"""
        Book title: {book.Title}
        Genre: {book.Genre}
        Premise: {Premise(book)}
        Target section count (parts of the story, most told in one chapter): {ChapterCount(book)}

        Story Bible:
        Setting: {bible.SettingDescription}
        Time period: {bible.TimePeriod}
        Themes: {bible.Themes}
        World rules: {bible.WorldRules}

        Characters:
        {CharacterSummary(characters)}
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
        string ancestorReference, string existingBlock)
    {
        var threadSummary = string.Join("\n", threads.Select(t =>
            $"- {t.Name} ({t.Type}, introduced in section {t.IntroducedChapterNumber?.ToString() ?? "?"}): {t.Description}"));
        return $"""
            Book title: {book.Title}
            Genre: {book.Genre}
            Premise: {Premise(book)}
            Target section count (parts of the story, most told in one chapter): {ChapterCount(book)}

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
            {Ancestor(ancestorReference)}
            {existingBlock}
            Create {book.TargetChapterCount} detailed section outlines for this book.
            """;
    }

    /// <summary>Re-run block listing the saved section outlines (as JSON) the model must keep, revise or extend.</summary>
    public static string ExistingChapterOutlines(string json) =>
        $"""

        These section outlines are already saved for this book:
        {json}

        Return the complete, updated outline set: repeat every section you are keeping (unchanged or
        revised, keeping its exact "number" so it can be matched), and add any section that is missing.
        Sections you leave out will be removed. A section marked "alreadyWritten": true has its prose
        written — do not change its premise, only correct outright errors in its outline. "chapters" is
        how many chapters a section has now; keep it unless the section clearly needs more room.

        """;

    // ── Section breakdown ─────────────────────────────────────────────────────

    public static readonly string SectionBreakdownSystem =
        """
        You are a creative writing Planner. A section of a book (one part of its story) needs more room than one
        chapter: divide it into consecutive chapters that together tell exactly the section's events, in order,
        each with a clear purpose and a natural ending point. Do not add events the section's outline does not
        imply, and do not take events from the sections before or after it.
        Return a JSON array, one element per chapter in order, each with these exact fields:
          "title" (string), "outline" (string - 3-6 sentence synopsis of this chapter's events and decisions),
          "povCharacter" (string), "charactersInvolved" (array of strings), "plotThreads" (array of strings),
          "foreshadowingNotes" (string; empty string if none), "payoffNotes" (string; empty string if none).
        IMPORTANT: Output ONLY a raw JSON array. Do not include any text, explanation, or markdown outside it.
        """;

    /// <param name="sections">The book's active sections in order (the one to break down among them).</param>
    /// <param name="chapterCount">How many chapters to make; null = decide (2-4).</param>
    public static string SectionBreakdownUser(
        Book book, StoryBible? bible, IEnumerable<CharacterCard> characters, IReadOnlyList<Section> sections,
        Section section, int? chapterCount, string ancestorReference)
    {
        var plan = string.Join("\n", sections.Select(s =>
            (s.Id == section.Id ? "→ " : "  ") + $"Section {s.Number}: {s.Title} — {OneLine(s.Outline)}"));
        return $"""
            Book title: {book.Title}
            Genre: {book.Genre}
            Premise: {Premise(book)}
            Write every string value in {book.Language}.
            {(bible is null ? "" : $"\nStory Bible:\nSetting: {bible.SettingDescription}\nTone & style: {bible.ToneAndStyle}\nWorld rules: {bible.WorldRules}\n")}
            Characters:
            {CharacterSummary(characters)}
            {Ancestor(ancestorReference)}
            The book's sections (→ marks the one to divide):
            {plan}

            Section {section.Number} to divide: "{section.Title}"
            Outline: {section.Outline}
            {(section.PovCharacter.Length > 0 ? $"Point of view: {section.PovCharacter}" : "")}
            {(section.ForeshadowingNotes.Length > 0 ? $"Foreshadowing to plant: {section.ForeshadowingNotes}" : "")}
            {(section.PayoffNotes.Length > 0 ? $"Payoff due: {section.PayoffNotes}" : "")}
            Characters involved: {string.Join(", ", ParseNames(section.CharactersInvolvedJson))}
            Plot threads: {string.Join(", ", ParseNames(section.PlotThreadsJson))}

            {(chapterCount is int n ? $"Divide it into exactly {n} chapters." : "Divide it into as many chapters as its events need (2 to 4).")}
            """;
    }

    private static IEnumerable<string> ParseNames(string json)
    {
        try { return System.Text.Json.JsonSerializer.Deserialize<List<string>>(json) ?? []; }
        catch (System.Text.Json.JsonException) { return []; }
    }

    /// <summary>
    /// Where a chapter of a section with several chapters stands within it: the section's overall plan and its other
    /// chapters, so the chapter tells its own share and hands over to the next. Empty for a one-chapter section.
    /// </summary>
    public static string SectionContext(Section section, IReadOnlyList<Chapter> sectionChapters, Chapter chapter)
    {
        if (sectionChapters.Count < 2) return "";
        var sb = new StringBuilder();
        sb.AppendLine($"\n## Section {section.Number}: {section.Title}");
        sb.AppendLine($"This chapter is part {sectionChapters.ToList().FindIndex(c => c.Id == chapter.Id) + 1} of {sectionChapters.Count} of this section. Write only its own share of the section's events.");
        if (!string.IsNullOrWhiteSpace(section.Outline)) sb.AppendLine($"Section outline: {section.Outline}");
        sb.AppendLine("Chapters of the section:");
        foreach (var c in sectionChapters)
            sb.AppendLine($"{(c.Id == chapter.Id ? "→" : "-")} Chapter {c.Number}: {c.Title} — {Gist(c)}");
        return sb.ToString();
    }

    // ── Writer ────────────────────────────────────────────────────────────────

    /// <summary>Appends the per-chapter context to the (possibly overridden) Writer system prompt.</summary>
    public static string WriterSystem(string basePrompt, Book book, Chapter chapter, string contextBlock, string previousEnding) =>
        $"""
        {basePrompt}
        Current chapter: {chapter.Number} — "{chapter.Title}"
        {contextBlock}
        {(previousEnding.Length > 0 ? $"\nThe previous chapter ended with:\n{previousEnding}\nContinue the story naturally from this point." : "")}
        """;

    public static string WriterUser(Chapter chapter, string synopsesBlock, string? authorInstructions = null, string sectionContext = "") =>
        $"""
        {(synopsesBlock.Length > 0 ? $"## Story So Far — Chapter Synopses\nRefer to this to avoid re-introducing or restating anything already established in prior chapters.\n{synopsesBlock}\n\n" : "")}{(sectionContext.Length > 0 ? sectionContext + "\n" : "")}Write the full content for:
        Chapter {chapter.Number}: {chapter.Title}
        Outline: {chapter.Outline}
        {(chapter.PovCharacter.Length > 0 ? $"Point of view: {chapter.PovCharacter}" : "")}
        {(chapter.ForeshadowingNotes.Length > 0 ? $"Plant in this chapter (foreshadowing): {chapter.ForeshadowingNotes}" : "")}
        {(chapter.PayoffNotes.Length > 0 ? $"Pay off in this chapter: {chapter.PayoffNotes}" : "")}
        {(string.IsNullOrWhiteSpace(authorInstructions) ? "" : $"Author's instructions for this chapter (follow them): {authorInstructions.Trim()}")}

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

    // ── Book chat (Interactive books) ─────────────────────────────────────────

    /// <summary>Most characters of book digest sent with each chat request; older chapters shrink to fit.</summary>
    public const int BookChatDigestBudget = 24_000;
    /// <summary>Chat messages sent verbatim with each request — older ones only reach the model through the rolling memory.</summary>
    public const int BookChatHistoryMessages = 6;
    /// <summary>Longest a resent chat message may be; longer ones are cut.</summary>
    public const int BookChatHistoryMessageChars = 1_500;
    /// <summary>Messages that fall out of the window before the rolling memory is rewritten to cover them.</summary>
    public const int BookChatMemoryBatch = 6;

    /// <summary>The question a lasting decision from the book chat is filed under as a clarification.</summary>
    public const string BookChatNoteQuestion = "Decision from the book chat";

    public static string BookChatSystem(Book book, bool bibleLocked, IReadOnlyList<string> actionTypes)
    {
        var catalogue = string.Join("\n", actionTypes.Select(t => $"  - {t}: {BookChatActionHelp(t)}"));
        var stage = bibleLocked ? BookChatBibleLockedRules : BookChatFoundationRules;
        return $$"""
            You are the co-author of the book "{{book.Title}}" ({{book.Genre}}), written in {{book.Language}}. The author tells you
            what they want — a new character, a plot thread, a chapter, a change — in any order, and you build the book together.
            You never change the book yourself: you propose changes, and the author applies the ones they want.

            Every reply is one JSON object with exactly these keys:
            - "message": what you say to the author, in the language they write in. Short and concrete. When a request is
              unclear, ask instead of guessing.
            - "conflicts": every way the request contradicts the book as it stands — the Story Bible, characters, plot threads,
              the author's notes, or what written chapters established. Each has "description", "canon" (quote or name the
              fact it contradicts) and "chapter" (its number, or null). Empty when there are none.
            - "actions": the changes you propose, at most {{BookChatActions.MaxActions}}. Each action has every key — "type", "label"
              (a short description for the author), "chapter", "name", "changes" (a list of {"field", "value"}), "instructions",
              "originalText", "replacementText", "text", "option" — with the ones it does not use set to null ([] for "changes").
            The action types:
            {{catalogue}}

            Rules:
            - Nothing changes until the author applies it: in your message, speak of your changes as proposals ("I propose…",
              "Here is…"), never as done.
            - Propose only what the author asked for or agreed to, plus what is needed to keep the book consistent with it.
              Never invent characters, plot threads or chapters on your own.
            - New chapters normally go at the end. A chapter not written yet may be inserted earlier (add_chapter with
              "chapter" = the chapter it goes after, 0 = at the start), moved (move_chapter) or removed (archive_chapter);
              later chapters are renumbered. Written chapters are never moved, and only the last one can be removed.
            - A chapter is written from its outline: to write a new chapter, propose add_chapter with its outline and, if the
              author wants it written now, write_chapter for the same number in the same reply. The writer writes prose,
              never you — do not put chapter prose in your message.
            - When the request conflicts with the book: list the conflict, explain it in your message, and offer the fixes as
              alternatives — option "A": adapt the request so it fits the book; option "B": change the book (outlines,
              characters, threads, or revise/patch written chapters) so the request fits. Give every action of an alternative
              the same "option"; leave "option" null for actions that are not alternatives.
            - Propose add_note for a lasting decision of the author (style, tone, rules, things to avoid) so it guides every
              later chapter.
            - When the author asks to run a check or a summary again, propose check_outline, check_chapter or
              summarize_chapter for those chapters — do not check them yourself in your message. These change nothing in
              the book; their findings come back as new proposals. To redo a chapter's prose, use write_chapter (from its
              outline) or revise_chapter. Do not propose checks the author did not ask for.
            - Refer to chapters by number and to characters and plot threads by their exact names.
            {{stage}}

            Example reply:
            {"message":"Mara is in the cast. Should she appear in Chapter 2?","conflicts":[],"actions":[{"type":"upsert_character","label":"Add Mara, a smuggler","chapter":null,"name":"Mara","changes":[{"field":"role","value":"Supporting"},{"field":"personality","value":"Wry, loyal to her crew"}],"instructions":null,"originalText":null,"replacementText":null,"text":null,"option":null}]}
            """;
    }

    private static string BookChatActionHelp(string type) => type switch
    {
        BookChatActions.UpdatePremise => "\"text\" = the new premise.",
        BookChatActions.UpdateStoryBible => "\"changes\" among settingDescription, timePeriod, themes, toneAndStyle, worldRules, notes.",
        BookChatActions.UpsertCharacter => "\"name\"; \"changes\" among role (Protagonist|Antagonist|Supporting|Minor), physicalDescription, " +
                                           "personality, backstory, goalMotivation, arc, firstAppearanceChapterNumber, notes. Adds the character, or updates the one with that name.",
        BookChatActions.ArchiveCharacter => "\"name\" — removes the character (kept in the archive).",
        BookChatActions.UpsertPlotThread => "\"name\"; \"changes\" among description, type (MainPlot|Subplot|CharacterArc|Mystery|Foreshadowing|" +
                                            "WorldBuilding|ThematicThread), status (Active|Resolved|Dormant), introducedChapterNumber, resolvedChapterNumber.",
        BookChatActions.ArchivePlotThread => "\"name\" — removes the plot thread (kept in the archive).",
        BookChatActions.AddChapter => "\"changes\" among title, outline (3-6 sentences: the key events and decisions), povCharacter, " +
                                      "charactersInvolved and plotThreads (comma-separated names), foreshadowingNotes, payoffNotes. " +
                                      "Added after the last chapter; with \"chapter\" = N it is inserted after Chapter N instead (0 = at the start).",
        BookChatActions.UpdateChapterOutline => "\"chapter\"; \"changes\" with the same fields as add_chapter.",
        BookChatActions.ArchiveChapter => "\"chapter\" — removes a chapter not written yet, or the last written one.",
        BookChatActions.MoveChapter => "\"chapter\" (not written yet); \"changes\" = [{\"field\":\"after\",\"value\":\"N\"}] — moves it after Chapter N (0 = to the start).",
        BookChatActions.WriteChapter => "\"chapter\"; optional \"instructions\" for the writer. Writes (or rewrites) the chapter's prose from its outline.",
        BookChatActions.ReviseChapter => "\"chapter\"; \"instructions\" = what to change in the written prose.",
        BookChatActions.PatchChapter => "\"chapter\"; \"originalText\" = text copied exactly from the chapter (a whole sentence or more); " +
                                        "\"replacementText\" = what replaces it. For small, precise fixes.",
        BookChatActions.AddNote => "\"text\" = the decision to remember.",
        BookChatActions.CheckOutline => "\"chapter\" — re-runs the continuity check of the chapter's outline against the book; " +
                                        "a corrected outline comes back as a new proposal.",
        BookChatActions.CheckChapter => "\"chapter\" (written) — re-runs the continuity check of the chapter's prose; " +
                                        "the fixes come back as new proposals.",
        BookChatActions.SummarizeChapter => "\"chapter\" (written) — rewrites the chapter's summary from its prose and proposes " +
                                            "the new characters and plot-thread changes it finds.",
        _ => "",
    };

    private const string BookChatFoundationRules =
        """
        - The book is in its foundation stage: settle the premise and the Story Bible with the author (update_premise,
          update_story_bible). When they feel settled, suggest that the author press "Lock bible & start writing" — from
          then on they cannot change. Writing the first chapter locks them too.
        """;

    public const string BookChatBibleLockedRules =
        """
        - The premise and the Story Bible are locked: they are fixed canon and you cannot change them. When a request
          contradicts them, list the conflict (quote the Story Bible in "canon") and offer to adapt the request so it fits.
          If the author insists, tell them the Story Bible can be unlocked in Book Settings.
        """;

    /// <summary>
    /// The book as it stands, within <paramref name="budget"/> characters: premise (with the author's notes), Story
    /// Bible, one line per character and thread (full cards for <paramref name="focusNames"/>), and the chapters with
    /// what each tells. Older chapters shrink to their title when the book outgrows the budget.
    /// </summary>
    public static string BookChatDigest(
        Book book, bool bibleLocked, StoryBible? bible, IReadOnlyList<CharacterCard> characters, IReadOnlyList<PlotThread> threads,
        IReadOnlyList<Chapter> chapters, IReadOnlyCollection<string> focusNames, int budget = BookChatDigestBudget)
    {
        var head = new StringBuilder();
        head.AppendLine("## The book as it stands");
        head.AppendLine($"Title: {book.Title} | Genre: {book.Genre} | Language: {book.Language}");
        head.AppendLine($"Stage: {(bibleLocked ? "writing (premise and Story Bible locked)" : "foundation (premise and Story Bible open)")}");
        head.AppendLine($"Premise: {Premise(book)}");

        head.AppendLine("\n## Story Bible");
        if (bible is null) head.AppendLine("(none yet)");
        else
            foreach (var (field, value) in BookContentRender.Fields(bible))
                if (!string.IsNullOrWhiteSpace(value)) head.AppendLine($"{field}: {value}");

        head.AppendLine("\n## Characters");
        if (characters.Count == 0) head.AppendLine("(none yet)");
        var focus = focusNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var c in characters)
        {
            if (focus.Contains(c.Name)) { AppendCharacterCard(c, head); continue; }
            head.Append($"- {c.Name} ({c.Role})");
            if (!string.IsNullOrWhiteSpace(c.GoalMotivation)) head.Append($": {c.GoalMotivation}");
            if (!string.IsNullOrWhiteSpace(c.PhysicalDescription)) head.Append($" | Looks: {c.PhysicalDescription}");
            head.AppendLine();
        }

        head.AppendLine("\n## Plot threads");
        if (threads.Count == 0) head.AppendLine("(none yet)");
        foreach (var t in threads)
            head.AppendLine($"- {t.Name} ({t.Type}, {t.Status}): {t.Description}");

        head.AppendLine("\n## Chapters");
        if (chapters.Count == 0) head.AppendLine("(none yet)");

        string Line(Chapter c, bool brief) =>
            brief
                ? $"{c.Number}. {c.Title} [{(c.Content.Length > 0 ? "written" : "outline only")}]"
                : c.Content.Length > 0
                    ? $"{c.Number}. {c.Title} [written] — {Gist(c)}"
                    : $"{c.Number}. {c.Title} [outline only] — planned: {c.Outline}";

        // Shrink the oldest chapters first; the latest five always keep their full line.
        var ordered = chapters.OrderBy(c => c.Number).ToList();
        var brief = new bool[ordered.Count];
        string Lines() => string.Join("\n", ordered.Select((c, i) => Line(c, brief[i])));
        var text = Lines();
        for (int i = 0; i < ordered.Count - 5 && head.Length + text.Length > budget; i++)
        {
            brief[i] = true;
            text = Lines();
        }
        var digest = head + text;
        return digest.Length <= budget ? digest : digest[..budget] + "\n…";
    }

    /// <summary>A resent assistant turn: what it said and what became of what it proposed — never the proposals' content.</summary>
    public static string BookChatPastReply(string message, IEnumerable<(string Label, string Status)> proposals)
    {
        var list = proposals.Select(p => $"{p.Label} ({p.Status.ToLowerInvariant()})").ToList();
        return list.Count == 0 ? message : $"{message}\n[Proposed: {string.Join("; ", list)}]";
    }

    /// <summary>A resent book event (bible locked, chapter written …).</summary>
    public static string BookChatPastEvent(string content) => $"[Book event] {content}";

    /// <summary>A resent revision request: the author's note on the suggestions of an earlier reply.</summary>
    public static string BookChatPastRevision(string content) => $"[Asked to revise the suggestions of your earlier reply] {content}";

    /// <summary>The digest and the rolling memory, sent as the first author turn of every request.</summary>
    public static string BookChatContext(string digest, string memory) =>
        memory.Trim().Length > 0
            ? $"{digest}\n\n## Our conversation so far (summary of earlier messages)\n{memory.Trim()}"
            : digest;

    /// <summary>The author's latest message, with the passages of the written chapters it brings up.</summary>
    public static string BookChatTurn(string ragPassages, string authorText) =>
        ragPassages.Trim().Length > 0
            ? $"Passages from the written chapters that may be relevant:\n{ragPassages}\n\n---\n\n{authorText}"
            : authorText;

    /// <summary>What a revision request without text asks for.</summary>
    public const string BookChatReviseDefault = "Look at these suggestions again and fix whatever is wrong with them.";

    /// <summary>
    /// The author's turn of a revision ("Anything else?" on a reply's suggestions): every suggestion of that reply in
    /// full, and the author's note. The answer is the complete corrected list, which replaces the open ones.
    /// </summary>
    public static string BookChatReviseTurn(
        string ragPassages, string replyMessage, IEnumerable<(string Status, string Type, string Label, string PayloadJson, string? Note)> suggestions,
        string authorText)
    {
        var list = string.Join("\n", suggestions.Select((p, i) =>
            $"{i + 1}. [{p.Status.ToLowerInvariant()}] {p.Type}: {p.Label}\n   {p.PayloadJson}" +
            (p.Note is { Length: > 0 } note ? $"\n   (note: {note})" : "")));
        var turn = $$"""
            In your reply "{{replyMessage}}" you suggested these changes:

            {{list}}

            Revise them. Your "actions" are the complete new list and replace every suggestion above that is still open
            (pending, failed or queued): keep the ones that are still right (improved where they can be), fix the wrong
            ones, drop what no longer fits, and add everything my note below asks for. Applied ones are already done — do
            not propose them again. In "message", say briefly what you changed.

            My note: {{authorText}}
            """;
        return BookChatTurn(ragPassages, turn);
    }

    /// <summary>What the first reply of a new Interactive book answers instead of an author message.</summary>
    public const string BookChatKickoff =
        """
        I just created this book. Read the premise, propose a Story Bible for it with update_story_bible (set all six
        fields), and ask me up to three questions about anything the premise leaves open that would change the book.
        """;

    /// <summary>The Editor's instructions for the next-step revision offered after a written chapter's outline changed.</summary>
    public const string BookChatReviseToOutline =
        "The chapter outline was updated after this chapter was written. Revise the chapter so it follows the updated " +
        "outline; keep everything the outline does not change.";

    public const string BookChatRepairRequest =
        """
        Your last reply was not a valid JSON object of the required shape. Give the same answer again as one JSON object
        with exactly the keys "message", "conflicts" and "actions" — nothing before or after it.
        """;

    public const string BookChatMemorySystem =
        """
        You keep the memory of a conversation between an author and their co-author about the book they are writing.
        Rewrite the memory so it also covers the new messages: the author's decisions and preferences, open questions,
        and what was proposed and rejected. At most 200 words, in the language the author writes in. Output only the memory.
        """;

    public static string BookChatMemoryUser(string memory, IEnumerable<string> transcript) =>
        $"""
        Current memory:
        {(memory.Trim().Length > 0 ? memory.Trim() : "(empty)")}

        New messages:
        {string.Join("\n", transcript)}
        """;

    // ── Chapter summary (what a written chapter actually tells) ───────────────

    public static string ChapterSummarySystem(Book book) =>
        $"""
        You keep the records of the book "{book.Title}" ({book.Genre}). You read one written chapter and record what
        it actually tells, so later chapters stay consistent with it. Write every value in {book.Language}.
        Return a JSON object with exactly these fields:
          "summary": what happens in the chapter, 80-150 words, in order — who does what, where, and how it ends.
             Describe the prose as written, not what it was meant to be.
          "facts": up to 8 short, concrete facts this chapter establishes that later chapters must respect —
             names, appearances, relationships, places, objects, injuries or deaths, secrets revealed, dates and times.
          "newCharacters": named characters who appear in the chapter but are not in the known list — each with
             "name", "role" (Protagonist, Antagonist, Supporting or Minor) and "description" (one sentence).
          "threadChanges": known plot threads whose state the chapter changes — each with "name" (exactly as
             listed), "status" (Active, Resolved or Dormant) and "note" (one sentence on what changed).
        Use empty arrays when there is nothing to report. Output only the JSON object.
        """;

    public static string ChapterSummaryUser(Chapter chapter, IEnumerable<string> characterNames, IEnumerable<string> threadNames)
    {
        var cast = string.Join(", ", characterNames);
        var threads = string.Join(", ", threadNames);
        return $"""
            Known characters: {(cast.Length > 0 ? cast : "(none yet)")}
            Known plot threads: {(threads.Length > 0 ? threads : "(none yet)")}

            Chapter {chapter.Number}: {chapter.Title}
            {chapter.Content}
            """;
    }

    /// <summary>The stored summary: the account of the chapter, then the facts it establishes.</summary>
    public static string ChapterSummaryText(string summary, IReadOnlyList<string> facts) =>
        facts.Count == 0
            ? summary.Trim()
            : $"{summary.Trim()}\nEstablished facts:\n" + string.Join("\n", facts.Select(f => $"- {f.Trim()}"));

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
                sb.AppendLine($"- Ch.{c.Number} \"{c.Title}\": {Gist(c)}");
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
        (string.IsNullOrWhiteSpace(c.Summary)
            ? $"Chapter {c.Number}: {c.Title}\nOutline: {c.Outline}\n"
            : $"Chapter {c.Number}: {c.Title}\nSummary: {c.Summary.Trim()}\n") +
        $"Content excerpt: {c.Content?[..Math.Min(excerptChars, c.Content?.Length ?? 0)]}...";

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

    // ── Illustrations ─────────────────────────────────────────────────────────

    /// <summary>Stand-in for an unset visual style, so the planner still keeps one style across the book.</summary>
    /// <summary>
    /// Repeated in every illustration request (not only the overridable system prompt): the image model may
    /// ignore negative prompts entirely (Z-Image Turbo at CFG 1.0), so nothing may depend on them.
    /// </summary>
    public const string NoNegativesRule =
        """
        Do not rely on negative prompts — the image model may ignore them entirely. Every prompt must produce
        the right image on its own: describe what IS there instead of what is not ("an empty street at dawn",
        not "no people"; "clean-shaven", not "no beard"; "a plain grey backdrop", not "no clutter"), and never
        mention an unwanted element in a prompt, even to exclude it. A negative prompt is at most an optional
        extra safeguard and may be left empty.
        """;

    /// <summary>
    /// Repeated in every scene and cover request: the image model takes no reference images, so the only
    /// thing keeping a prop, an outfit or a place identical between images is identical wording.
    /// </summary>
    public const string VisualContinuityRule =
        """
        Visual continuity — anything that can appear in more than one image must look exactly the same every time:
        - In "assets", list every recurring prop, outfit, place, creature or vehicle the image shows. For one
          already registered, copy its name and description exactly. For a new one, give it a short unique name
          and write its canonical description: concrete English prose of shape, size, materials, colours,
          markings and condition — no art style, no action, no surroundings. Outfit descriptions start with
          "wearing". From then on that description is the asset's only wording.
        - Paste the description of every listed asset word for word into the prompt.
        - Clothing: a character wears their usual outfit unless the text says otherwise. When they wear
          something else, use (or register) an "outfit" asset named after them, e.g. "Mara — wedding gown",
          paste it in place of their usual outfit, and keep it on in every image of that stretch of the story.
        - Keep places continuous: an image set somewhere already shown reuses that place's asset — the same
          layout, furniture and light sources.
        """;

    /// <summary>Resolves <see cref="PromptPlaceholders.NegativePrompts"/> for the configured CFG.</summary>
    public static string NegativePromptSupport(double cfg) => cfg <= 1.0
        ? "the image model ignores them at the current settings (CFG 1.0), so they have no effect at all. " +
          "Leave them empty or keep them minimal; everything must be achieved through the prompt."
        : "the image model applies them, but only weakly — use them as an optional extra safeguard.";

    public const string VisualStyleUnset =
        "not set — choose one that suits the genre and tone";

    /// <summary>
    /// The canonical appearance of each character, as the illustrator must repeat it, followed by the name
    /// of their usual-outfit asset (when they have one).
    /// </summary>
    public static string CharacterVisuals(IEnumerable<CharacterCard> characters, IEnumerable<VisualAsset>? assets = null)
    {
        var outfits = (assets ?? []).Where(a => a.CharacterCardId.HasValue)
            .GroupBy(a => a.CharacterCardId!.Value).ToDictionary(g => g.Key, g => g.First());
        var lines = characters
            .Where(c => !string.IsNullOrWhiteSpace(c.VisualPrompt))
            .Select(c => $"- {c.Name}: {c.VisualPrompt.Trim()}"
                + (outfits.TryGetValue(c.Id, out var o) ? $" (usual outfit: asset \"{o.Name}\")" : ""))
            .ToList();
        return lines.Count == 0 ? "(no character visuals yet)" : string.Join("\n", lines);
    }

    /// <summary>The book's registered visual assets, each with the description the illustrator must repeat.</summary>
    public static string VisualAssets(IEnumerable<VisualAsset> assets)
    {
        var lines = assets
            .Where(a => !string.IsNullOrWhiteSpace(a.Description))
            .Select(a => $"- \"{a.Name}\" ({a.Kind.ToString().ToLowerInvariant()}): {a.Description.Trim()}")
            .ToList();
        return lines.Count == 0 ? "(none yet)" : string.Join("\n", lines);
    }

    /// <summary>Full prompts of the illustrations just before a chapter, so outfits and places carry over.</summary>
    public static string PreviousIllustrations(IEnumerable<(int ChapterNumber, string Prompt)> illustrations)
    {
        var lines = illustrations.Select(i => $"- Chapter {i.ChapterNumber}: {i.Prompt.Trim()}").ToList();
        return lines.Count == 0 ? "(none)" : string.Join("\n", lines);
    }

    public static string CharacterVisualsUser(Book book, IEnumerable<CharacterCard> characters, string otherVisuals)
    {
        var sb = new StringBuilder();
        foreach (var c in characters)
        {
            sb.AppendLine($"### {c.Name} ({c.Role})");
            if (!string.IsNullOrWhiteSpace(c.PhysicalDescription)) sb.AppendLine($"Appearance: {c.PhysicalDescription}");
            if (!string.IsNullOrWhiteSpace(c.Personality)) sb.AppendLine($"Personality: {c.Personality}");
            if (!string.IsNullOrWhiteSpace(c.Backstory)) sb.AppendLine($"Backstory: {c.Backstory}");
            if (!string.IsNullOrWhiteSpace(c.Notes)) sb.AppendLine($"Notes: {c.Notes}");
            sb.AppendLine();
        }
        return $"""
            Write the canonical visual description of each of these characters of "{book.Title}" ({book.Genre}).

            {sb.ToString().TrimEnd()}

            Visual descriptions of the book's other characters, for contrast (do not return these):
            {otherVisuals}

            Return a JSON array with one object per character above, each with these exact fields:
              "name" (string — exactly as given above),
              "visualPrompt" (string — English prose describing only the character's body: apparent age,
                build, skin, face, hair, eyes and permanent distinguishing features. No clothing, no name, no art
                style, no pose or background. It is pasted word for word into every image showing this
                character — including their reference portrait, which uses the same composition for the whole
                cast — so it must be self-contained and faithful to the character's description),
              "usualOutfit" (string — English prose starting with "wearing", describing only the clothes,
                footwear and accessories the character usually wears: garments, cut, materials, colours and
                details. It becomes their outfit in every image unless the story dresses them differently).
            {NoNegativesRule}
            IMPORTANT: Output ONLY the raw JSON array. No markdown fences, no explanation outside the JSON.
            """;
    }

    /// <summary>Asks for the book's art direction, written once and added to every image.</summary>
    public static string StyleSheetUser(Book book, StoryBible? bible, string brief) =>
        $"""
        Write the art direction for all illustrations of "{book.Title}" ({book.Genre}).
        Premise: {Premise(book)}
        {(bible is null ? "" : $"Setting: {bible.SettingDescription}\nTime period: {bible.TimePeriod}\nTone & style: {bible.ToneAndStyle}")}
        The author's brief for the visual style: {brief}

        It is added word for word to every image of the book — portraits, cover and scenes — so that they all
        look like one consistent set by the same artist.
        Return a JSON object with these exact fields:
          "style" (string — English prose describing only the look: medium, technique, line and texture,
            colour palette, general lighting and mood. No subject matter, no characters, no composition),
          "portraitFraming" (string — the composition used for every character's reference portrait, identical
            for the whole cast: framing, pose, camera angle, backdrop and lighting. No appearance, no art style),
          "negativePrompt" (string — an optional extra safeguard listing what no image should contain; the
            style itself must already keep every image on track without it. May be empty).
        {NoNegativesRule}
        IMPORTANT: Output ONLY the raw JSON object. No markdown fences, no explanation outside the JSON.
        """;

    /// <summary>Numbers paragraphs "[N] text" so the planner can say where each image goes.</summary>
    public static string NumberParagraphs(IReadOnlyList<string> paragraphs) =>
        string.Join("\n\n", paragraphs.Select((p, i) => $"[{i + 1}] {p}"));

    public static string ChapterIllustrationsUser(
        Book book, Chapter chapter, IReadOnlyList<string> paragraphs, string characterVisuals, string otherIllustrations,
        int imagesPerChapter = ImageGenDefaults.ImagesPerChapter, string visualAssets = "(none yet)",
        string previousIllustrations = "(none)") =>
        $"""
        Plan the illustrations for Chapter {chapter.Number}: "{chapter.Title}" of "{book.Title}".
        Plan {imagesPerChapter} illustration{(imagesPerChapter == 1 ? "" : "s")} for this chapter, choosing its most striking, pivotal moments.
        That number is a guide, not a hard limit: plan more when the chapter genuinely needs them — for example
        several distinct turning points that {imagesPerChapter} image{(imagesPerChapter == 1 ? "" : "s")} cannot carry — and fewer, or none, when the
        chapter has less worth illustrating.
        Each image is placed right after the paragraph it illustrates.

        Characters' canonical visual descriptions (copy word for word into any prompt that shows them):
        {characterVisuals}

        Registered visual assets — recurring props, outfits, places, creatures and vehicles (copy name and
        description word for word whenever an image shows one):
        {visualAssets}

        The most recent illustrations before this chapter (keep outfits, objects and places continuous with them
        unless the story has changed them):
        {previousIllustrations}

        Illustrations already planned for other chapters (avoid repeating the same image):
        {otherIllustrations}

        Chapter text, paragraphs numbered [N]:
        {NumberParagraphs(paragraphs)}

        Return a JSON object {"{"}"illustrations": [...]{"}"} where each element has these exact fields, in this order:
          "paragraphNumber" (integer — the [N] of the paragraph the image follows),
          "anchorText" (string — a short phrase copied exactly, character for character, from that paragraph),
          "caption" (string — a one-line caption in {book.Language}),
          "characters" (array of strings — names of every character shown),
          "focusCharacter" (string — name of the character the image centres on, or empty),
          "assets" (array of objects {"{"}"name", "kind", "description"{"}"} — every recurring asset shown;
            kind is one of "object", "outfit", "location", "creature", "vehicle", "other"),
          "prompt" (string — the full image prompt),
          "negativePrompt" (string — optional extra safeguard; may be empty),
          "orientation" ("portrait" | "landscape" | "square").
        {VisualContinuityRule}
        {NoNegativesRule}
        IMPORTANT: Output ONLY the raw JSON object. No markdown fences, no explanation outside the JSON.
        """;

    public static string OtherIllustrations(IEnumerable<(int ChapterNumber, string Caption)> illustrations)
    {
        var lines = illustrations.Select(i => $"- Chapter {i.ChapterNumber}: {i.Caption}").ToList();
        return lines.Count == 0 ? "(none)" : string.Join("\n", lines);
    }

    public static string CoverUser(Book book, StoryBible? bible, string synopses, string characterVisuals,
        string visualAssets = "(none yet)") =>
        $"""
        Design the cover illustration of "{book.Title}" ({book.Genre}).
        Premise: {Premise(book)}
        {(bible is null ? "" : $"Themes: {bible.Themes}\nTime period: {bible.TimePeriod}")}

        Chapter synopses:
        {synopses}

        Characters' canonical visual descriptions (copy word for word into the prompt for anyone shown):
        {characterVisuals}

        Registered visual assets — recurring props, outfits, places, creatures and vehicles (copy name and
        description word for word whenever the cover shows one):
        {visualAssets}

        Whether the cover shows the title lettering is your choice; if it does, spell it exactly "{book.Title}".
        Return a JSON object with these exact fields, in this order:
          "characters" (array of strings — names of every character shown),
          "focusCharacter" (string — name of the character the cover centres on, or empty),
          "assets" (array of objects {"{"}"name", "kind", "description"{"}"} — every recurring asset shown;
            kind is one of "object", "outfit", "location", "creature", "vehicle", "other"),
          "prompt" (string), "negativePrompt" (string — optional extra safeguard; may be empty),
          "orientation" ("portrait" | "landscape" | "square").
        {VisualContinuityRule}
        {NoNegativesRule}
        IMPORTANT: Output ONLY the raw JSON object. No markdown fences, no explanation outside the JSON.
        """;

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
