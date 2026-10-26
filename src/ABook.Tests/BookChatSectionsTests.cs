using ABook.Agents;
using ABook.Core.Models;

namespace ABook.Tests;

/// <summary>The book chat's section actions and section-aware digest: normalizer checks, parsing, digest and parts.</summary>
public class BookChatSectionsTests
{
    // Section 1 = Chapter 1 (written); Section 2 = Chapters 2-3 (unwritten); Section 3 = Chapter 4 (unwritten, planned for 3).
    private static BookChatState State() => new(true, ["Mara"], [],
        [
            new BookChatChapter(10, 1, "One", "Prose.", "Arrival.", SectionId: 1),
            new BookChatChapter(11, 2, "Two", "", "Storm.", SectionId: 2),
            new BookChatChapter(12, 3, "Three", "", "Calm.", SectionId: 2),
            new BookChatChapter(13, 4, "Four", "", "Siege.", SectionId: 3),
        ],
        Sections: [new BookChatSection(1, 1, "One"), new BookChatSection(2, 2, "Sea", 2), new BookChatSection(3, 3, "Siege", 3)]);

    private static RawChatAction A(string type, int? section = null, int? chapter = null, (string, string)[]? changes = null,
        string? instructions = null) =>
        new(type, "", chapter, null, (changes ?? []).Select(c => KeyValuePair.Create(c.Item1, c.Item2)).ToList(),
            instructions, null, null, null, null, section);

    [Fact]
    public void Parse_ReadsTheSectionOfActionsAndParts()
    {
        var reply = BookChatActions.Parse("""
            {"message":"ok","conflicts":[],"actions":[{"type":"write_section","label":"","chapter":null,"section":2,"name":null,
              "changes":[],"instructions":null,"originalText":null,"replacementText":null,"text":null,"option":null}],
             "parts":[{"kind":"section_plan","chapter":null,"section":3,"name":null}, "section_plan 2"]}
            """);

        Assert.Equal(2, Assert.Single(reply!.Actions).Section);
        Assert.Equal(3, reply.Parts[0].Section);
        Assert.Equal(("section_plan", 2), (reply.Parts[1].Kind, reply.Parts[1].Chapter));
        var schema = BookChatActions.ReplySchema(true, partRequests: true);
        Assert.Contains("\"required\": [\"type\", \"label\", \"chapter\", \"section\"", schema);
        Assert.Contains("\"required\": [\"kind\", \"chapter\", \"section\", \"name\"]", schema);
        Assert.Contains(BookChatActions.PartSectionPlan, schema);
    }

    [Fact]
    public void AddSection_AtTheEnd_IsNumbered_AndAWriteOrBreakdownOfItDependsOnIt()
    {
        var (actions, dropped) = BookChatActions.Normalize(
            [
                A(BookChatActions.AddSection, changes: [("title", "Flight"), ("outline", "They run."), ("chapters", "3")]),
                A(BookChatActions.BreakDownSection, section: 4),
                A(BookChatActions.WriteChapter, chapter: 5),
            ], State());

        Assert.Empty(dropped);
        Assert.Equal("Add Section 4 (3 chapters): Flight", actions[0].Label);
        Assert.Equal((4, 5), (actions[0].Payload.SectionNumber!.Value, actions[0].Payload.ChapterNumber!.Value));
        Assert.Equal("3", actions[0].Payload.Changes["chapters"]);
        Assert.Equal(0, actions[1].DependsOnIndex);
        Assert.Null(actions[1].Payload.SectionId);
        Assert.Equal(0, actions[2].DependsOnIndex);
    }

    [Fact]
    public void AddSection_AfterAnEarlierSection_IsInserted_AndChecksItsChapterCount()
    {
        var (actions, dropped) = BookChatActions.Normalize(
            [
                A(BookChatActions.AddSection, section: 1, changes: [("title", "Interlude")]),
                A(BookChatActions.AddSection, section: 0, changes: [("title", "Prologue")]),
                A(BookChatActions.AddSection, changes: [("title", "Too big"), ("chapters", "40")]),
                A(BookChatActions.AddSection, changes: [("povCharacter", "Mara")]),   // no title or outline
            ], State());

        Assert.Equal((1, false), (actions[0].Payload.AfterSectionId!.Value, actions[0].Payload.AtStart));
        Assert.Equal("Insert a section after Section 1: Interlude", actions[0].Label);
        Assert.True(actions[1].Payload.AtStart);
        Assert.Equal(2, dropped.Count);
        Assert.Contains("\"chapters\" must be a number from 1 to", dropped[0]);
        Assert.Contains("no title or outline", dropped[1]);
    }

    [Fact]
    public void AddChapter_ToASection_GoesAtItsEnd_ReferableOnlyAtTheEndOfTheBook()
    {
        var (actions, dropped) = BookChatActions.Normalize(
            [
                A(BookChatActions.AddChapter, section: 2, changes: [("title", "Fog")]),
                A(BookChatActions.AddChapter, section: 3, changes: [("title", "Breach")]),
                A(BookChatActions.WriteChapter, chapter: 5),
                A(BookChatActions.AddChapter, section: 9, changes: [("title", "Nowhere")]),
            ], State());

        Assert.Equal("Add a chapter at the end of Section 2: Fog", actions[0].Label);
        Assert.Equal((2, 4), (actions[0].Payload.SectionId!.Value, actions[0].Payload.ChapterNumber!.Value));
        Assert.Equal("Add Chapter 5 to Section 3: Breach", actions[1].Label);
        Assert.Equal(1, actions[2].DependsOnIndex);
        Assert.Contains("no Section 9", Assert.Single(dropped));
    }

    [Fact]
    public void UpdateSectionPlan_TargetsTheSection_AndKeepsPlanFieldsOnly()
    {
        var (actions, dropped) = BookChatActions.Normalize(
            [
                A(BookChatActions.UpdateSectionPlan, section: 2, changes: [("outline", "A longer voyage."), ("chapters", "4"), ("mood", "grim")]),
                A(BookChatActions.UpdateSectionPlan, section: 2, changes: [("mood", "grim")]),
                A(BookChatActions.UpdateSectionPlan, changes: [("outline", "x")]),
            ], State());

        var action = Assert.Single(actions);
        Assert.Equal("section:2", action.Target);
        Assert.Equal(new Dictionary<string, string> { ["outline"] = "A longer voyage.", ["chapters"] = "4" }, action.Payload.Changes);
        Assert.Contains("no plan fields to change", dropped[0]);
        Assert.Contains("no section number", dropped[1]);
    }

    [Fact]
    public void MoveAndArchiveSection_OnlySectionsWithNothingWritten()
    {
        var (actions, dropped) = BookChatActions.Normalize(
            [
                A(BookChatActions.MoveSection, section: 3, changes: [("after", "0")]),
                A(BookChatActions.MoveSection, section: 1, changes: [("after", "3")]),   // written
                A(BookChatActions.MoveSection, section: 3, changes: [("after", "2")]),   // already there
                A(BookChatActions.ArchiveSection, section: 2),
                A(BookChatActions.ArchiveSection, section: 1),                           // written
            ], State());

        Assert.Equal(2, actions.Count);
        Assert.Equal(("Move Section 3 to the start", true, 3), (actions[0].Label, actions[0].Payload.AtStart, actions[0].Payload.SectionId!.Value));
        Assert.Equal("Remove Section 2: Sea (2 chapters)", actions[1].Label);
        Assert.Equal(3, dropped.Count);
        Assert.Contains("written chapters", dropped[0]);
        Assert.Contains("already there", dropped[1]);
        Assert.Contains("written chapters", dropped[2]);
    }

    [Fact]
    public void WriteSection_ListsTheUnwrittenChapters_AndNeedsABreakdownFirstWhenPlannedForMore()
    {
        var (actions, dropped) = BookChatActions.Normalize(
            [
                A(BookChatActions.WriteSection, section: 2, instructions: "Keep it tense."),
                A(BookChatActions.WriteSection, section: 1),   // all written
                A(BookChatActions.WriteSection, section: 3),   // one chapter, planned for 3
            ], State());

        var action = Assert.Single(actions);
        Assert.Equal("Write Section 2: Chapters 2, 3, one at a time", action.Label);
        Assert.Equal("Chapter 2: Two\nChapter 3: Three", action.Payload.Text);
        Assert.Equal("Keep it tense.", action.Payload.Instructions);
        Assert.Null(action.Target);
        Assert.Contains("every chapter of Section 1 is written", dropped[0]);
        Assert.Contains("break it down first", dropped[1]);
        Assert.False(BookChatActions.HasUndo(BookChatActions.WriteSection));
    }

    [Fact]
    public void BreakDownSection_IsAStep_TakesThePlannedCount_AndRefusesWrittenSections()
    {
        var (actions, dropped) = BookChatActions.Normalize(
            [
                A(BookChatActions.BreakDownSection, section: 3),
                A(BookChatActions.BreakDownSection, section: 2, changes: [("chapters", "1")]),
                A(BookChatActions.BreakDownSection, section: 1),
            ], State());

        var action = Assert.Single(actions);
        Assert.True(BookChatActions.IsStep(action.Payload.Type));
        Assert.Equal("Break Section 3 down into 3 chapters", action.Label);
        Assert.Null(action.Target);
        Assert.Contains("from 2 to", dropped[0]);
        Assert.Contains("can no longer be broken down", dropped[1]);
    }

    [Fact]
    public void Digest_ListsChaptersUnderTheirSections()
    {
        var book = new Book { Title = "T", Genre = "G", Language = "English", Premise = "P" };
        var sections = new List<Section>
        {
            new() { Id = 1, Number = 1, Title = "One" },
            new() { Id = 2, Number = 2, Title = "Sea", Outline = "The voyage.", PlannedChapters = 3 },
            new() { Id = 3, Number = 3, Title = "Siege", PlannedChapters = 2 },
        };
        var chapters = new List<Chapter>
        {
            new() { Id = 10, Number = 1, SectionId = 1, Title = "One", Content = "Prose.", Summary = "She came." },
            new() { Id = 11, Number = 2, SectionId = 2, Title = "Two", Outline = "Storm." },
            new() { Id = 12, Number = 3, SectionId = 2, Title = "Three", Outline = "Calm." },
            new() { Id = 13, Number = 4, SectionId = 3, Title = "Four", Outline = "Siege." },
        };

        var digest = PromptTemplates.BookChatDigest(book, true, null, [], [], chapters, [], sections: sections);

        Assert.Contains("## Sections and chapters", digest);
        Assert.Contains("Section 1:\n  1. One [written] — She came.", digest);
        Assert.Contains("Section 2: Sea (2 chapters, planned for 3) — plan: The voyage.\n  2. Two [outline only] — planned: Storm.\n  3. Three", digest);
        Assert.Contains("Section 3 (1 chapter, planned for 2 — its plan is the chapter's outline):\n  4. Four", digest);
        // Without sections the digest is the flat chapter list it always was.
        Assert.Contains("## Chapters\n1. One [written]", PromptTemplates.BookChatDigest(book, true, null, [], [], chapters, []));
    }

    [Fact]
    public void ResolveParts_ServesASectionPlan()
    {
        var sections = new List<Section> { new() { Id = 2, Number = 1, Title = "Sea", Outline = "The voyage.", PlannedChapters = 3 } };
        var chapters = new List<Chapter>
        {
            new() { Id = 11, Number = 1, SectionId = 2, Title = "Two", Outline = "Storm." },
            new() { Id = 12, Number = 2, SectionId = 2, Title = "Three", Outline = "Calm." },
        };

        var (parts, missing, labels) = BookChatAgent.ResolveParts(
            [new("section_plan", null, null, 1), new("section_plan", 1, null), new("section_plan", null, null, 7)],
            chapters, [], [], sections: sections);

        Assert.Equal(["the plan of Section 1"], labels);   // asked for twice (by "section" and by "chapter")
        Assert.Contains("outline: The voyage.", parts[0].Body);
        Assert.Contains("chapters: 3", parts[0].Body);
        Assert.Contains("- Chapter 2: Three [outline only] — Calm.", parts[0].Body);
        Assert.Contains(missing, m => m.StartsWith("section_plan 7:"));
    }
}
