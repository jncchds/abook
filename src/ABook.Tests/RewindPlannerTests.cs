using System.Text.Json;
using ABook.Core.Interfaces;
using ABook.Core.Models;
using ABook.Infrastructure.Data;
using ABook.Infrastructure.Rewind;
using static ABook.Infrastructure.Rewind.RewindPlanner;

namespace ABook.Tests;

public class RewindPlannerTests
{
    private static string Json(object o) => JsonSerializer.Serialize(o);

    private static ContentRevision Rev(string type, int id, object data, ContentRevisionOp op = ContentRevisionOp.Update) =>
        new() { EntityType = type, EntityId = id, Op = op, DataJson = Json(data) };

    private static object Chapter(int id, string content, bool archived = false, string updated = "2026-01-01") => new
    {
        Id = id, BookId = 1, Number = id, Title = $"T{id}", Outline = "o", Content = content, Status = 4,
        IsArchived = archived, Summary = "", SummaryVersionId = (int?)null, UpdatedAt = updated,
    };

    [Fact]
    public void ChangedChapter_IsRestored_WithWhatGoesBack()
    {
        var then = new Dictionary<RowKey, ContentRevision> { [new("Chapter", 1)] = Rev("Chapter", 1, Chapter(1, "old")) };
        var now = new Dictionary<RowKey, string> { [new("Chapter", 1)] = Json(Chapter(1, "new", updated: "2026-02-02")) };

        var change = Assert.Single(Plan(then, now));
        Assert.Equal(RewindChangeKind.Restore, change.Kind);
        Assert.Equal(["text"], change.Fields);
        Assert.Equal("Chapter 1: T1", change.Label);
    }

    [Fact]
    public void OnlyTimestampsChanged_NothingToDo()
    {
        var then = new Dictionary<RowKey, ContentRevision> { [new("Chapter", 1)] = Rev("Chapter", 1, Chapter(1, "same")) };
        var now = new Dictionary<RowKey, string> { [new("Chapter", 1)] = Json(Chapter(1, "same", updated: "2027-01-01")) };
        Assert.Empty(Plan(then, now));
    }

    [Fact]
    public void ChapterCreatedAfterTheMoment_IsArchived_UnlessItAlreadyIs()
    {
        var now = new Dictionary<RowKey, string>
        {
            [new("Chapter", 2)] = Json(Chapter(2, "later")),
            [new("Chapter", 3)] = Json(Chapter(3, "later", archived: true)),
        };
        var change = Assert.Single(Plan(new Dictionary<RowKey, ContentRevision>(), now));
        Assert.Equal((RewindChangeKind.Archive, 2), (change.Kind, change.Key.Id));
    }

    [Fact]
    public void OnlyTheArchiveFlagDiffers_SaysArchivedOrBroughtBack()
    {
        var then = new Dictionary<RowKey, ContentRevision>
        {
            [new("Chapter", 1)] = Rev("Chapter", 1, Chapter(1, "a")),
            [new("Chapter", 2)] = Rev("Chapter", 2, Chapter(2, "b", archived: true)),
        };
        var now = new Dictionary<RowKey, string>
        {
            [new("Chapter", 1)] = Json(Chapter(1, "a", archived: true)),
            [new("Chapter", 2)] = Json(Chapter(2, "b")),
        };
        var changes = Plan(then, now);
        Assert.Equal([(RewindChangeKind.Recreate, 1), (RewindChangeKind.Archive, 2)], changes.Select(c => (c.Kind, c.Key.Id)));
        Assert.All(changes, c => Assert.Empty(c.Fields));
    }

    [Fact]
    public void RowDeletedAfterTheMoment_IsRecreated_ButNotOneDeletedBefore()
    {
        var then = new Dictionary<RowKey, ContentRevision>
        {
            [new("CharacterCard", 5)] = Rev("CharacterCard", 5, new { Id = 5, BookId = 1, Name = "Mortimer", IsArchived = false }),
            [new("CharacterCard", 6)] = Rev("CharacterCard", 6, new { Id = 6, Name = "Gone" }, ContentRevisionOp.Delete),
        };
        var change = Assert.Single(Plan(then, new Dictionary<RowKey, string>()));
        Assert.Equal((RewindChangeKind.Recreate, "Character Mortimer"), (change.Kind, change.Label));
    }

    [Fact]
    public void Book_RestoresContentFields_NotItsSettings()
    {
        var then = new Dictionary<RowKey, ContentRevision>
        {
            [new("Book", 1)] = Rev("Book", 1, new { Id = 1, Premise = "A", WriterSystemPrompt = "x", ChaptersStatus = 0 }),
        };
        var now = new Dictionary<RowKey, string> { [new("Book", 1)] = Json(new { Id = 1, Premise = "B", WriterSystemPrompt = "y", ChaptersStatus = 1 }) };

        var change = Assert.Single(Plan(then, now));
        Assert.Equal(["premise", "chapters status"], change.Fields);
        Assert.True(IsRestored("Book", "Premise"));
        Assert.False(IsRestored("Book", "WriterSystemPrompt"));
    }

    [Fact]
    public void ProposalMadeAfterTheMoment_IsRejected_UnlessAlreadyClosed()
    {
        var now = new Dictionary<RowKey, string>
        {
            [new("ChatProposal", 1)] = Json(new { Id = 1, Label = "Add Mortimer", Status = (int)ChatProposalStatus.Applied }),
            [new("ChatProposal", 2)] = Json(new { Id = 2, Label = "Old", Status = (int)ChatProposalStatus.Rejected }),
        };
        var change = Assert.Single(Plan(new Dictionary<RowKey, ContentRevision>(), now));
        Assert.Equal((RewindChangeKind.Archive, 1), (change.Kind, change.Key.Id));
    }

    [Fact]
    public void Changes_ComeBookFirst_ProposalsLast()
    {
        var then = new Dictionary<RowKey, ContentRevision>
        {
            [new("ChatProposal", 1)] = Rev("ChatProposal", 1, new { Id = 1, Label = "x", Status = 0 }),
            [new("Book", 1)] = Rev("Book", 1, new { Id = 1, Premise = "A" }),
            [new("Chapter", 1)] = Rev("Chapter", 1, Chapter(1, "a")),
        };
        var now = new Dictionary<RowKey, string>
        {
            [new("ChatProposal", 1)] = Json(new { Id = 1, Label = "x", Status = 2 }),
            [new("Book", 1)] = Json(new { Id = 1, Premise = "B" }),
            [new("Chapter", 1)] = Json(Chapter(1, "b")),
        };
        Assert.Equal(["Book", "Chapter", "ChatProposal"], Plan(then, now).Select(c => c.Key.Type));
    }

    [Theory]
    [InlineData("Content", "text")]
    [InlineData("IsArchived", "archived")]
    [InlineData("PhysicalDescription", "physical description")]
    [InlineData("ChatMemoryThroughMessageId", "chat memory")]
    public void FieldWords_AreReadable(string field, string words) => Assert.Equal(words, FieldWords(field));

    [Fact]
    public void Times_AreLoggedAsTheDatabaseKeepsThem()
    {
        var inMemory = new DateTime(2026, 10, 24, 13, 35, 37, DateTimeKind.Utc).AddTicks(1234567);
        var stored = (DateTime)ContentJournal.AsStored(inMemory)!;
        Assert.Equal(inMemory.AddTicks(-7), stored);
        Assert.Equal(DateTimeKind.Utc, stored.Kind);
        Assert.Equal("x", ContentJournal.AsStored("x"));
    }

    [Fact]
    public void Hash_IgnoresUpdatedAt_ButNotContent()
    {
        var a = ContentJournal.Hash(Json(new { Content = "x", UpdatedAt = "1" }));
        Assert.Equal(a, ContentJournal.Hash(Json(new { Content = "x", UpdatedAt = "2" })));
        Assert.NotEqual(a, ContentJournal.Hash(Json(new { Content = "y", UpdatedAt = "1" })));
    }
}
