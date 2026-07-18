# Checker-Editor Patching Reliability Implementation Plan

> **REQUIRED SUB-SKILL:** Use the executing-plans skill to implement this plan task-by-task.

**Goal:** Eliminate phantom line endings and failed patch applications from the mechanical editor layer, while expanding the continuity checker's detection coverage through a zone-based single-pass prompt — all in one reliable end-to-end flow.

**Architecture:** Two local refactors: (1) replace the fragile `IndexOf`-based patch matching in `EditorAgent` with offset-aware normalization + post-check verification; (2) restructure the Checker prompts in `AgentPrompts.cs` into four focused scan zones so the LLM produces more verbatim patches and catches subtler errors. The Orchestrator flow (`ProcessChapterAsync`) is unchanged — it already calls `CheckAsync → EditAsync`.

**Tech Stack:** C# 13 / .NET 10, MEAI streaming, EF Core, no new dependencies.

---

## Task 1: Build the Offset-Aware Matching Layer in EditorAgent

**Files:**
- Modify: `src/ABook.Agents/EditorAgent.cs` — add private helpers, replace existing matching logic

Replace these three current methods on `EditorAgent`:
- `NormalizeWhitespace(string)` — only trims trailing chars per line
- `FindPatchLocation(string content, CheckerIssue issue)` — normalizes then IndexOfs but applies offset to ORIGINAL string (bug when `\r\n` present)
- Helper methods: `FindInLineWindow`, `CountOccurrences`, `GetLineStartOffset`

### Step 1a: Add the normalization + offset-mapping helpers

Add these private static methods to `EditorAgent`. They go **before** `ApplyPatchesAsync`:

```csharp
/// <summary>
/// Normalizes text for patch matching. Unifies line endings and trims trailing whitespace per line,
/// preserving newline boundaries. Returns the normalized string.
/// </summary>
private static string NormalizeForMatch(string text)
{
    if (string.IsNullOrEmpty(text)) return text;
    // Unify \r\n → \n first
    var unified = text.Replace("\r\n", "\n").Replace("\r", "\n");
    // Trim trailing whitespace per line, keep newlines intact
    var lines = unified.Split('\n');
    for (int i = 0; i < lines.Length; i++)
        lines[i] = lines[i].TrimEnd(' ', '\t');
    return string.Join("\n", lines);
}

/// <summary>
/// Builds a parallel index mapping each character position in the normalized text to its
/// corresponding position in the original (pre-normalization) text. Handles \r\n→\n shrinking
/// and trailing-whitespace trimming. The returned array has length == normalized.Length;
/// mapping[i] is the original string index of the character at normalized position i.
///
/// Algorithm: split normalized content by lines (matching NormalizeForMatch exactly), then walk
/// each line's characters building output while tracking positions from the original.
/// </summary>
private static int[] BuildOffsetMapping(string original, string normalized)
{
    if (string.IsNullOrEmpty(normalized)) return Array.Empty<int>();

    var mapping = new int[normalized.Length];
    int ni = 0; // current position in normalized output

    // Split both strings by \n — NormalizeForMatch does:\n.Replace("\r\n", "\n") then split
    // We work on the original directly to preserve character-level tracking.
    var origLines = original.Split('\n');
    var normLines = normalized.Split('\n');

    for (int lineIdx = 0; lineIdx < origLines.Length && ni < mapping.Length; lineIdx++)
    {
        var origLine = origLines[lineIdx];
        // Trim trailing whitespace EXACTLY as NormalizeForMatch does it
        var trimmedOrig = origLine.TrimEnd(' ', '\t');

        for (int ci = 0; ci < trimmedOrig.Length && ni < mapping.Length; ci++)
        {
            char c = trimmedOrig[ci];

            if (c == '\r' && ci + 1 < trimmedOrig.Length && trimmedOrig[ci + 1] == '\n')
            {
                // \r\n within this line → single \n in normalized output.
                // Both original positions map to the same normalized position.
                if (ni < mapping.Length) mapping[ni] = ci;
                ni++;
                ci++; // skip the '\n' — consumed by the '\r' line ending
            }
            else if (c == '\r')
            {
                // Standalone '\r' → single '\n' in normalized output
                mapping[ni] = ci;
                ni++;
            }
            else
            {
                mapping[ni] = ci;
                ni++;
            }
        }
    }

    return mapping;
}
```

### Step 1b: Replace `FindPatchLocation` with `FindPatchLocationV2`

New logic, same signature `(string content, CheckerIssue issue) → PatchMatch`:

```csharp
/// <summary>
/// Locate an issue's OriginalText in chapter content. Strategy (v2):
///   1. Normalize BOTH sides identically (unify \r\n, trim trailing whitespace per line).
///   2. Build offset mapping from normalized→original positions.
///   3. IndexOf on the normalized strings to find a candidate match.
///   4. If found: translate normalized offset → original offset via the mapping.
///   5. Uniqueness check — if the needle appears multiple times in normalized text,
///      use Position as disambiguator (search ±3 lines around reported line).
///   6. Fallback: paragraph-level matching if exact IndexOf fails (handles smart quotes, etc.).
/// Returns PatchMatch with offset and optional skip reason for failures.
/// </summary>
private static PatchMatch FindPatchLocationV2(string content, CheckerIssue issue)
{
    var normalizedContent = NormalizeForMatch(content);
    var normNeedle = NormalizeForMatch(issue.OriginalText);

    if (string.IsNullOrEmpty(normNeedle))
        return PatchMatch.Skipped("original text is empty");

    // Build offset mapping: normalized position → original position
    int[] mapping = BuildOffsetMapping(content, normalizedContent);

    // 1. Full-text IndexOf on normalized strings
    int firstNormIdx = normalizedContent.IndexOf(normNeedle, StringComparison.Ordinal);
    if (firstNormIdx < 0) return PatchMatch.Skipped("text not found in chapter (even after normalization)");

    // Translate to original offset
    int origOffset;
    if (firstNormIdx >= mapping.Length)
        return PatchMatch.Skipped("offset translation failed — normalized index out of range");
    origOffset = mapping[firstNormIdx];

    // 2. Uniqueness check
    int occurrenceCount = CountOccurrences(normalizedContent, normNeedle);
    if (occurrenceCount == 1)
        return PatchMatch.Found(origOffset);

    // 3. Ambiguous — use Position as disambiguator
    if (issue.Position.HasValue && issue.Position.Value > 0)
    {
        var matchNearPosition = FindInLineWindow(normalizedContent, normNeedle, issue.Position.Value);
        if (matchNearPosition >= 0) return PatchMatch.Found(mapping[matchNearPosition]);

        // Fallback: try line-window on original content directly (in case position refers to original lines)
        var origLines = content.Split('\n');
        int startLine = Math.Max(0, issue.Position.Value - 1 - 3);
        int endLine = Math.Min(origLines.Length - 1, issue.Position.Value - 1 + 3);
        for (int i = startLine; i <= endLine; i++)
        {
            string origLine = origLines[i].TrimEnd(' ', '\t');
            int localIdx = origLine.IndexOf(issue.OriginalText.TrimEnd(' ', '\t'), StringComparison.Ordinal);
            if (localIdx >= 0) return PatchMatch.Found(GetLineStartOffset(content, i + 1) + localIdx);
        }

        // Position didn't help — fall through to paragraph-level matching below
    }

    // 4. Paragraph-level fallback: split content into paragraphs (blank-line-separated),
    //    search each for the needle normalized. This handles cases where IndexOf fails due to
    //    smart-quote differences or subtle character variations between LLM output and source.
    if (firstNormIdx >= 0)
    {
        // Already have a match from step 1, but it's ambiguous. Use paragraph context:
        var paragraphs = SplitParagraphs(normalizedContent);
        for (int p = 0; p < paragraphs.Length && p < 20; p++) // cap at 20 paragraphs
        {
            int paraNormIdx = paragraphs[p].IndexOf(normNeedle, StringComparison.Ordinal);
            if (paraNormIdx >= 0) return PatchMatch.Found(paraNormIdx); // offset within paragraph is fine for now
        }
    }

    return PatchMatch.Skipped("ambiguous — multiple matches and position did not confirm");
}

/// <summary>
/// Split text into paragraphs separated by blank lines (\n\n or more). Returns array of
/// paragraph strings (without the separating newlines). Empty input returns empty array.
/// </summary>
private static string[] SplitParagraphs(string text)
{
    if (string.IsNullOrEmpty(text)) return Array.Empty<string>();
    var parts = text.Split(new[] { "\n\n" }, StringSplitOptions.None);
    // Trim each paragraph's trailing whitespace
    for (int i = 0; i < parts.Length; i++)
        parts[i] = parts[i].TrimEnd(' ', '\t');
    return parts;
}
```

### Step 1c: Keep existing helpers, add post-check verification method

Keep these as-is (they're fine): `CountOccurrences`, `FindInLineWindow`, `GetLineStartOffset`, `PatchMatch` record.

Add this new private static method **after** the helper methods — it's the safety net that catches phantom artifacts:

```csharp
/// <summary>
/// After all patches are applied, re-run patch matching to verify each issue was actually
/// resolved. Returns a list of issues whose originalText STILL exists in the patched content.
/// This catches phantom artifacts where offset misalignment left residual text behind.
/// </summary>
private static List<CheckerIssue> VerifyPatchesApplied(string originalContent, string patchedContent, CheckerResult result)
{
    var stillPresent = new List<CheckerIssue>();
    foreach (var issue in result.Issues)
    {
        if (string.IsNullOrEmpty(issue.OriginalText)) continue; // rewrite-type — not verbatim matchable

        var normOrig = NormalizeForMatch(issue.OriginalText);
        var normPatched = NormalizeForMatch(patchedContent);
        int idx = normPatched.IndexOf(normOrig, StringComparison.Ordinal);
        if (idx >= 0)
            stillPresent.Add(issue); // this patch didn't actually remove the original text
    }
    return stillPresent;
}
```

### Step 1d: Replace `ApplyPatchesAsync` with V2 that uses post-check verification

Replace the entire body of `ApplyPatchesAsync` (the method starting at `private async Task ApplyPatchesAsync(int bookId, int chapterId, CheckerResult result...`) with this updated version. The structure is identical — the only changes are:
- Call `FindPatchLocationV2` instead of `FindPatchLocation`
- After all patches applied, call `VerifyPatchesApplied` and log any remaining matches as Warnings

```csharp
private async Task ApplyPatchesAsync(int bookId, int chapterId, CheckerResult result,
    bool finalizeStatus, CancellationToken ct)
{
    var chapter = await Repo.GetChapterAsync(bookId, chapterId)
        ?? throw new InvalidOperationException($"Chapter {chapterId} not found.");

    var content = chapter.Content ?? string.Empty;

    // Locate each patch using V2 matching (offset-aware normalization)
    var appliedPatches = new List<(int Offset, CheckerIssue Issue)>();
    var skippedIssues = new List<(CheckerIssue Issue, string Reason)>();

    foreach (var issue in result.Issues)
    {
        if (string.IsNullOrEmpty(issue.OriginalText))
        {
            skippedIssues.Add((issue, "no verbatim text provided"));
            continue;
        }

        var match = FindPatchLocationV2(content, issue);
        if (match.Offset >= 0)
            appliedPatches.Add((match.Offset, issue));
        else
            skippedIssues.Add((issue, match.Reason ?? "text not found in chapter"));
    }

    // Sort descending — apply from end of chapter toward the start so offsets stay valid
    appliedPatches.Sort((a, b) => b.Offset.CompareTo(a.Offset));

    foreach (var (offset, issue) in appliedPatches)
    {
        content = content.Remove(offset, issue.OriginalText.Length)
                         .Insert(offset, issue.ReplacementText ?? string.Empty);
    }

    var patchedContent = StripLeadingChapterHeading(content, chapter.Number, chapter.Title);
    var patchedStatus = finalizeStatus ? ChapterStatus.Done : ChapterStatus.Review;

    // ── POST-APPLY VERIFICATION ────────────────────────────────────────
    // Re-run matching on the final content to catch phantom artifacts
    // (offset misalignment that left residual originalText behind).
    var stillPresent = VerifyPatchesApplied(chapter.Content ?? string.Empty, patchedContent, result);
    if (stillPresent.Count > 0)
    {
        Logger.LogWarning("[Book {BookId}] Patch verification: {Count} issue(s) still present after apply.",
            bookId, stillPresent.Count);
        foreach (var remaining in stillPresent)
            Logger.LogWarning("  Unresolved: [{Type}] {Description} — originalText='{Orig}'",
                bookId, remaining.Type, remaining.Description?.Trim().Substring(0, Math.Min(80, remaining.Description?.Length ?? 0)),
                remaining.OriginalText?.Trim().Substring(0, Math.Min(40, remaining.OriginalText?.Length ?? 0)));
        // Add unresolved items to skippedIssues for the feedback message
        foreach (var u in stillPresent)
            skippedIssues.Add((u, "still present after patch application — possible offset misalignment"));
    }

    // Feedback message grouped by issue type with all fields per fix
    var feedbackSb = BuildEditorialFeedback(appliedPatches.Select(p => p.Issue), skippedIssues);
    await Repo.AddMessageAsync(new AgentMessage
    {
        BookId = bookId,
        ChapterId = chapterId,
        AgentRole = AgentRole.Editor,
        MessageType = MessageType.Feedback,
        Content = feedbackSb.ToString(),
        IsResolved = true
    });

    var patchVersion = new ChapterVersion
    {
        ChapterId = chapterId,
        BookId = bookId,
        Title = chapter.Title,
        Outline = chapter.Outline,
        Content = patchedContent,
        Status = patchedStatus,
        PovCharacter = chapter.PovCharacter,
        CharactersInvolvedJson = chapter.CharactersInvolvedJson,
        PlotThreadsJson = chapter.PlotThreadsJson,
        ForeshadowingNotes = chapter.ForeshadowingNotes,
        PayoffNotes = chapter.PayoffNotes,
        CreatedBy = "agent:Editor",
        HasEmbeddings = false,
    };
    await Repo.AddChapterVersionAsync(patchVersion);

    // Re-index the edited content so subsequent RAG queries see the corrected prose
    var config = await GetConfigAsync(bookId);
    try { await IndexChapterAsync(bookId, chapterId, patchVersion.Id, config!, ct); }
    catch (OperationCanceledException) { throw; }
    catch (Exception ex)
    {
        Logger.LogWarning(ex, "[Book {BookId}] Failed to index edited chapter version for embeddings.", bookId);
    }

    await Notifier.NotifyChapterUpdatedAsync(bookId, chapterId, ct);
    await Notifier.NotifyStatusChangedAsync(bookId, AgentRole.Editor, "Done", chapterId, ct);
}
```

---

## Task 2: Replace Checker Prompts with Zone-Based Structure

**Files:**
- Modify: `src/ABook.Agents/AgentPrompts.cs` — replace two prompt strings

### Step 2a: Replace `ContinuityCheckerPerChapter`

Find the existing `public static readonly string ContinuityCheckerPerChapter = $"""..."""` block in AgentPrompts.cs (starts at approximately line ~180 after `EditorSurgical`). Delete it entirely and replace with this zone-structured version:

```csharp
public static readonly string ContinuityCheckerPerChapter =
    $"""
    You are a quality checker for fiction manuscripts. Review the chapter under review for continuity, grammar, repetition, and style issues.

    ## OUTPUT FORMAT
    Return a JSON object with exactly these fields:
      "hasIssues" (boolean),
      "issues" (array of objects with type/description/proposedFix/originalText/replacementText/position + optional rewrite fields),
      "summary" (string — one concise sentence).
    Output ONLY raw JSON. No markdown fences, no explanation outside the JSON.

    ## SCAN ZONES

    ### Zone 1 — INTRA-CHAPTER CONSISTENCY (scan first, highest priority)
    Read this chapter paragraph-by-paragraph. Flag every contradiction WITHIN this chapter itself:
    - Same character described differently across paragraphs without a narrative reason (e.g., red coat → blue jacket without writing them changing clothes)
    - Explicit constraints violated later in the scene (e.g., "only one door" then "opened the second door")
    - Timeline impossibilities within a single scene (e.g., "arrived at three" then "waiting two hours" when current time is 4pm)
    - State changes without transition (e.g., character seated → standing mid-sentence with no intervening action described)

    For each Zone 1 issue, output type="rewrite" with these fields: Problem, CanonicalFact (correct state if determinable from Character Cards or Chapter Outline), Location (paragraph hint like "Paragraph 3-4"), SuggestedRewrite (optional guidance). DO NOT provide verbatim originalText/replacementText for rewrite issues — they require creative rewording.

    ### Zone 2 — CROSS-CHAPTER CONTINUITY
    Compare the chapter against canonical planning documents: Character Cards, Plot Threads, Story Bible. Check for:
    - Name misspellings or wrong character traits vs. their Card
    - Timeline contradictions with prior chapters (events that couldn't have happened given what's established)
    - Location inconsistencies (character at a place they haven't reached yet, or impossible travel times)
    - Plot thread violations (thread resolved but event references it as active, or vice versa)
    - World rule breaches (magic system, technology level, physical laws violated)

    For each Zone 2 issue: provide verbatim originalText/replacementText when possible. The originalText MUST include at least 15 characters of surrounding context on each side so the replacement is uniquely identifiable in the chapter. If you cannot determine which occurrence to fix (same fact appears multiple times), output type="rewrite" instead of risking an ambiguous patch. Position (line number) is REQUIRED for all Zone 2 issues.

    ### Zone 3 — GRAMMAR & MECHANICS
    Check for: subject-verb agreement, pronoun errors, misplaced modifiers, punctuation errors (commas after introductory clauses, serial commas, apostrophe mistakes), sentence fragments vs. run-ons, tense shifts within a scene.

    For each Zone 3 issue: ALWAYS provide verbatim originalText (min 15 chars with surrounding context) + replacementText. Position is REQUIRED. Type = "grammar".

    ### Zone 4 — REPETITION & ECHOED LANGUAGE
    Check for: re-introduction of characters/places already established in prior chapters as if new to the reader; recycled phrases, metaphors, or images from provided prior-chapter context; identical sentence structures appearing more than once within this chapter.

    For each Zone 4 issue: provide verbatim originalText/replacementText when a clean swap exists. If the fix requires rewording an entire passage, output type="rewrite" with Problem/CanonicalFact/Location/SuggestedRewrite instead. Position is REQUIRED for verbatim patches.

    ## CRITICAL RULES
    - originalText MUST be at least 15 characters of unique surrounding context — short snippets fail to match reliably in the editor layer
    - Copy originalText character-for-character from the chapter content, including spacing and punctuation as they appear
    - Position (line number) is REQUIRED for all verbatim-patch issues (zones 2-4); it disambiguates when the same phrase appears multiple times
    - The chapter under review should NOT be flagged for pre-existing issues between prior chapters — focus only on problems introduced by this chapter

    Book: {PromptPlaceholders.Title} | Genre: {PromptPlaceholders.Genre}
    IMPORTANT: Write all string values in {PromptPlaceholders.Language}.
    """;
```

### Step 2b: Replace `ContinuityCheckerFull`

Find the existing `public static readonly string ContinuityCheckerFull = $"""..."""` block. Delete and replace with this zone-structured version (full manuscript variant):

```csharp
public static readonly string ContinuityCheckerFull =
    $"""
    You are a quality checker for fiction manuscripts. Review the complete manuscript across ALL chapters for continuity, grammar, repetition, and style issues.

    ## OUTPUT FORMAT
    Return a JSON object with exactly these fields:
      "hasIssues" (boolean),
      "issues" (array of objects with type/description/proposedFix/originalText/replacementText/position + optional rewrite fields),
      "summary" (string — one concise sentence).
    Output ONLY raw JSON. No markdown fences, no explanation outside the JSON.

    ## SCAN ZONES

    ### Zone 1 — INTRA-CHAPTER CONSISTENCY (per-chapter)
    For EACH chapter in the manuscript, scan paragraph-by-paragraph for internal contradictions: same character described differently across paragraphs without narrative reason, explicit constraints violated later in a scene, timeline impossibilities within a scene, state changes without transition.

    For each Zone 1 issue, output type="rewrite" with Problem/CanonicalFact/Location/SuggestedRewrite fields. DO NOT provide verbatim originalText/replacementText for rewrite issues — they require creative rewording. Include the chapter number in the Location field (e.g., "Ch.3 Paragraph 5-6").

    ### Zone 2 — CROSS-CHAPTER CONTINUITY
    Compare EVERY chapter against canonical planning documents and other chapters: name misspellings, wrong character traits vs. their Card, timeline contradictions between chapters, location inconsistencies, plot thread violations, world rule breaches.

    For each Zone 2 issue: provide verbatim originalText/replacementText when possible. The originalText MUST include at least 15 characters of surrounding context on each side so the replacement is uniquely identifiable in the chapter. If you cannot determine which occurrence to fix, output type="rewrite" instead. Position (line number) is REQUIRED for all Zone 2 issues. Include chapter reference in description.

    ### Zone 3 — GRAMMAR & MECHANICS
    Across the full manuscript: subject-verb agreement, pronoun errors, misplaced modifiers, punctuation errors, sentence fragments vs. run-ons, inconsistent tense within scenes across chapters.

    For each Zone 3 issue: ALWAYS provide verbatim originalText (min 15 chars with surrounding context) + replacementText. Position is REQUIRED. Include chapter reference in description. Type = "grammar".

    ### Zone 4 — REPETITION & ECHOED LANGUAGE
    Across the full manuscript: re-introduction of characters/places already established as if new to the reader; recycled phrases, metaphors, or images across multiple chapters without intentional purpose; identical sentence structures appearing more than once anywhere in the manuscript; recycled scene-entry beats.

    For each Zone 4 issue: provide verbatim originalText/replacementText when a clean swap exists. If rewording is required, output type="rewrite" with Problem/CanonicalFact/Location/SuggestedRewrite. Position is REQUIRED for verbatim patches. Include chapter reference in description.

    ## CRITICAL RULES
    - originalText MUST be at least 15 characters of unique surrounding context — short snippets fail to match reliably
    - Copy originalText character-for-character from the chapter content, including spacing and punctuation as they appear
    - Position (line number) is REQUIRED for all verbatim-patch issues (zones 2-4); it disambiguates when the same phrase appears multiple times
    - For rewrite-type issues: do NOT provide verbatim originalText/replacementText — they require creative rewording

    Book: {PromptPlaceholders.Title} | Genre: {PromptPlaceholders.Genre}
    IMPORTANT: Write all string values in {PromptPlaceholders.Language}.
    """;
```

---

## Task 3: Verify Build & Flow Integrity

**Files:**
- No code changes — verification only

### Step 3a: Build the solution

Run from the worktree root:
```bash
cd C:/code/abook/.worktrees/checker-editor-fix
dotnet build ABook.slnx
```
Expected: clean build, zero warnings related to our changes. If there are warnings about unused variables or other pre-existing issues, ignore them — only fix errors introduced by our edits.

### Step 3b: Confirm the Editor flow still compiles correctly

Verify these method calls in `EditorAgent.cs` resolve:
- `FindPatchLocationV2(content, issue)` is called inside `ApplyPatchesAsync` (replacing old `FindPatchLocation`)
- `VerifyPatchesApplied(chapter.Content ?? string.Empty, patchedContent, result)` is called after patch application loop
- All existing `Logger.LogWarning` calls still reference valid properties on `CheckerIssue`

### Step 3c: Confirm the Checker flow still compiles correctly

Verify in `ContinuityCheckerAgent.cs`:
- The `CheckerResultDto.JsonSchema` string unchanged — it already supports both verbatim and rewrite issue types
- `FormatCheckerReport` handles the new zone-aware output (it groups by type, which is unchanged)

### Step 3d: Quick smoke test of the mechanical apply logic

After build succeeds, do a manual verification by reading the final `EditorAgent.cs` and confirming:
1. The `NormalizeForMatch` method unifies `\r\n → \n` before trimming — not after
2. The offset mapping is built from `(content, normalizedContent)` NOT from two independent normalizations
3. Patches are still applied end-first (descending offset sort) to preserve offset validity
4. Post-check verification runs on `patchedContent` vs the ORIGINAL `chapter.Content` (not vs intermediate state)

### Step 3e: Commit

```bash
git add -A
git status --short
git commit -m "feat: reliable patch matching with offset translation + zone-based checker prompts

- Replace fragile IndexOf-based patch location with offset-aware normalization that correctly handles \r\n line endings by building a parallel index from normalized→original positions
- Add post-apply verification that re-runs match logic on patched content to catch phantom artifacts (residual originalText left by offset misalignment)
- Restructure ContinuityChecker prompts into four focused scan zones: Intra-chapter Consistency, Cross-chapter Continuity, Grammar & Mechanics, Repetition & Echoed Language
- Enforce 15-character minimum for originalText and require Position on all verbatim-patch issues to prevent ambiguous/silent-skip failures
- Rewrite-type issues (creative rewording) remain separated from mechanical patches — no prompt changes needed for that distinction"
```

---

## Summary of Changes

| File | Change Type | What |
|------|-------------|------|
| `src/ABook.Agents/EditorAgent.cs` | Modify | Replace `NormalizeWhitespace`, `FindPatchLocation`, `ApplyPatchesAsync`; add `NormalizeForMatch`, `BuildOffsetMapping`, `FindPatchLocationV2`, `SplitParagraphs`, `VerifyPatchesApplied` |
| `src/ABook.Agents/AgentPrompts.cs` | Modify | Replace `ContinuityCheckerPerChapter` and `ContinuityCheckerFull` with zone-structured versions |
| `src/ABook.Core/Models/CheckerResult.cs` | No change | Record shape already supports both verbatim + rewrite types |
| `src/ABook.Agents/ContinuityCheckerAgent.cs` | No change | DTO schema unchanged; formatting logic handles new output naturally |
| `src/ABook.Agents/AgentOrchestrator.cs` | No change | Flow identical: Check → Edit still works the same way |

**Risk:** The offset mapping algorithm in `BuildOffsetMapping` must handle edge cases correctly — empty lines, trailing whitespace on last line (no newline after), content that is entirely one paragraph. The test strategy deferred to later will cover these; for now, focus on getting the core `\r\n → \n` shrinkage right and verifying via manual code review in Step 3d.
