using ABook.Agents;
using ABook.Core.Models;

namespace ABook.Tests;

public class ClarificationPromptTests
{
    private static Book BookWith(params Clarification[] clarifications) => new()
    {
        Title = "Fog", Genre = "Mystery", Premise = "A lighthouse keeper vanishes.", TargetChapterCount = 3,
        Clarifications = clarifications,
    };

    private static Clarification C(string q, string a, bool archived = false, int minute = 0) =>
        new() { Question = q, Answer = a, IsArchived = archived, CreatedAt = new DateTime(2026, 10, 1, 12, minute, 0, DateTimeKind.Utc) };

    [Fact]
    public void Premise_WithoutClarifications_IsUnchanged()
    {
        Assert.Equal("A lighthouse keeper vanishes.", PromptTemplates.Premise(BookWith()));
    }

    [Fact]
    public void Premise_AppendsActiveClarificationsInOrder_AndSkipsArchivedAndBlank()
    {
        var premise = PromptTemplates.Premise(BookWith(
            C("Who narrates?", "His daughter.", minute: 2),
            C("Is it supernatural?", "No.", minute: 1),
            C("Old question", "Dropped answer", archived: true),
            C("Unanswered", "   ")));

        Assert.StartsWith("A lighthouse keeper vanishes.\n\nAuthor clarifications:\n", premise);
        Assert.True(premise.IndexOf("Is it supernatural?", StringComparison.Ordinal) < premise.IndexOf("Who narrates?", StringComparison.Ordinal));
        Assert.Contains("A: His daughter.", premise);
        Assert.DoesNotContain("Dropped answer", premise);
        Assert.DoesNotContain("Unanswered", premise);
    }

    [Fact]
    public void PlanningAndSystemPrompts_CarryTheClarifications()
    {
        var book = BookWith(C("Is it supernatural?", "No."));

        Assert.Contains("A: No.", PromptTemplates.StoryBibleUser(book, ""));
        Assert.Contains("A: No.", PromptTemplates.QuestionGatheringUser(book, ""));
        Assert.Contains("A: No.", AgentBase.InterpolateSystemPrompt(DefaultPrompts.Writer, book));
    }
}
