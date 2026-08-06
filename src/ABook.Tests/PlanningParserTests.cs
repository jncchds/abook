using ABook.Agents;
using ABook.Core.Models;

namespace ABook.Tests;

/// <summary>
/// Covers the parsers of the three JSON planning agents, with an emphasis on responses that were
/// cut short mid-stream — those must still yield everything the author already saw.
/// </summary>
public class PlanningParserTests
{
    // ── Characters ──────────────────────────────────────────────────────────────

    [Fact]
    public void Characters_CompleteArray_AllParsed()
    {
        var raw = """
            [{"name":"Ana","role":"Protagonist","arc":"grows up","firstAppearanceChapterNumber":1},
             {"name":"Bo","role":"Antagonist"}]
            """;
        var cards = CharactersAgent.Parse(7, raw);

        Assert.Equal(2, cards.Count);
        Assert.Equal(7, cards[0].BookId);
        Assert.Equal(CharacterRole.Protagonist, cards[0].Role);
        Assert.Equal(1, cards[0].FirstAppearanceChapterNumber);
        Assert.Equal(CharacterRole.Antagonist, cards[1].Role);
    }

    [Fact]
    public void Characters_TruncatedStream_KeepsCompletedCards()
    {
        var raw = """[{"name":"Ana","role":"Protagonist"},{"name":"Bo","role":"Anta""";
        var cards = CharactersAgent.Parse(7, raw);

        Assert.Single(cards);
        Assert.Equal("Ana", cards[0].Name);
    }

    [Fact]
    public void Characters_UnknownRole_FallsBackToSupporting()
    {
        var cards = CharactersAgent.Parse(1, """[{"name":"Ana","role":"Sidekick"}]""");
        Assert.Equal(CharacterRole.Supporting, cards[0].Role);
    }

    [Fact]
    public void Characters_NamelessEntry_Skipped()
    {
        var cards = CharactersAgent.Parse(1, """[{"role":"Minor"},{"name":"Ana","role":"Minor"}]""");
        Assert.Single(cards);
        Assert.Equal("Ana", cards[0].Name);
    }

    [Fact]
    public void Characters_NothingUsable_Throws()
    {
        Assert.Throws<FormatException>(() => CharactersAgent.Parse(1, "[]"));
        Assert.Throws<FormatException>(() => CharactersAgent.Parse(1, "the model said nothing"));
    }

    // ── Plot threads ────────────────────────────────────────────────────────────

    [Fact]
    public void PlotThreads_CompleteArray_AllParsed()
    {
        var raw = """
            [{"name":"The heist","type":"MainPlot","status":"Active","introducedChapterNumber":1,"resolvedChapterNumber":9},
             {"name":"Ana's guilt","type":"CharacterArc","status":"Dormant"}]
            """;
        var threads = PlotThreadsAgent.Parse(3, raw);

        Assert.Equal(2, threads.Count);
        Assert.Equal(PlotThreadType.MainPlot, threads[0].Type);
        Assert.Equal(9, threads[0].ResolvedChapterNumber);
        Assert.Equal(PlotThreadStatus.Dormant, threads[1].Status);
        Assert.Null(threads[1].IntroducedChapterNumber);
    }

    [Fact]
    public void PlotThreads_TruncatedStream_KeepsCompletedThreads()
    {
        var raw = """[{"name":"The heist","type":"MainPlot"},{"name":"Ana's gui""";
        var threads = PlotThreadsAgent.Parse(3, raw);

        Assert.Single(threads);
        Assert.Equal("The heist", threads[0].Name);
    }

    [Fact]
    public void PlotThreads_UnknownTypeAndStatus_FallBackToDefaults()
    {
        var threads = PlotThreadsAgent.Parse(1, """[{"name":"X","type":"Whatever","status":"Unclear"}]""");
        Assert.Equal(PlotThreadType.Subplot, threads[0].Type);
        Assert.Equal(PlotThreadStatus.Active, threads[0].Status);
    }

    [Fact]
    public void PlotThreads_NothingUsable_Throws() =>
        Assert.Throws<FormatException>(() => PlotThreadsAgent.Parse(1, "[]"));

    // ── Chapter outlines ────────────────────────────────────────────────────────

    [Fact]
    public void Chapters_CompleteArray_AllParsed()
    {
        var raw = """
            [{"number":1,"title":"Arrival","outline":"Ana arrives.","povCharacter":"Ana",
              "charactersInvolved":["Ana","Bo"],"plotThreads":["The heist"],
              "foreshadowingNotes":"the key","payoffNotes":""},
             {"number":2,"title":"Departure","outline":"Bo leaves."}]
            """;
        var chapters = PlannerAgent.ParseChapterOutlines(5, raw);

        Assert.Equal(2, chapters.Count);
        Assert.Equal(5, chapters[0].BookId);
        Assert.Equal("Arrival", chapters[0].Title);
        Assert.Equal("""["Ana","Bo"]""", chapters[0].CharactersInvolvedJson);
        Assert.Equal(ChapterStatus.Outlined, chapters[0].Status);
        Assert.Equal("[]", chapters[1].CharactersInvolvedJson);
    }

    [Fact]
    public void Chapters_TruncatedStream_KeepsCompletedOutlines()
    {
        var raw = """
            [{"number":1,"title":"Arrival","outline":"Ana arrives.","charactersInvolved":["Ana"]},
             {"number":2,"title":"Depart
            """;
        var chapters = PlannerAgent.ParseChapterOutlines(5, raw);

        Assert.Single(chapters);
        Assert.Equal(1, chapters[0].Number);
    }

    [Fact]
    public void Chapters_MissingNumber_FallsBackToPosition()
    {
        var chapters = PlannerAgent.ParseChapterOutlines(1, """[{"title":"A"},{"title":"B"}]""");
        Assert.Equal(1, chapters[0].Number);
        Assert.Equal(2, chapters[1].Number);
    }

    [Fact]
    public void Chapters_NonNumericNumber_FallsBackToPosition()
    {
        var chapters = PlannerAgent.ParseChapterOutlines(1, """[{"number":"one","title":"A"}]""");
        Assert.Equal(1, chapters[0].Number);
    }

    [Fact]
    public void Chapters_NothingUsable_Throws() =>
        Assert.Throws<FormatException>(() => PlannerAgent.ParseChapterOutlines(1, "[]"));
}
