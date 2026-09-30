using ABook.Agents;
using ABook.Core.Interfaces;
using ABook.Core.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ABook.Tests;

public class AgentRunStateServiceTests
{
    private static AgentRunStateService NewService(int maxConcurrentRuns = 3) =>
        new(new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            NullLogger<AgentRunStateService>.Instance)
        { MaxConcurrentRuns = maxConcurrentRuns };

    [Fact]
    public void TryStartRun_SecondStartForSameBook_IsBusy_AndKeepsFirstToken()
    {
        var state = NewService();
        Assert.Equal(RunStartResult.Success, state.TryStartRun(1, AgentRole.Writer, 5, out var first));
        Assert.Equal(RunStartResult.BookBusy, state.TryStartRun(1, AgentRole.Writer, 5, out _));

        // The refused start must not have replaced (or disposed) the running run's token source.
        Assert.True(state.CancelRun(1));
        Assert.True(first.IsCancellationRequested);
    }

    [Fact]
    public void TryStartRun_ConcurrentStarts_ExactlyOneWins()
    {
        var state = NewService();
        var results = new RunStartResult[16];
        Parallel.For(0, results.Length, i => results[i] = state.TryStartRun(7, AgentRole.Planner, null, out _));
        Assert.Equal(1, results.Count(r => r == RunStartResult.Success));
    }

    [Fact]
    public void TryStartRun_RespectsGlobalCapacity()
    {
        var state = NewService(maxConcurrentRuns: 1);
        Assert.Equal(RunStartResult.Success, state.TryStartRun(1, AgentRole.Writer, null, out _));
        Assert.Equal(RunStartResult.AtCapacity, state.TryStartRun(2, AgentRole.Writer, null, out _));
    }

    [Fact]
    public void TryStartRun_AfterTerminalState_Succeeds()
    {
        var state = NewService();
        state.TryStartRun(1, AgentRole.Writer, null, out var token);
        state.SetStatus(1, new AgentRunStatus(AgentRole.Writer, RunStates.Done, null));
        state.ReleaseRunCts(1, token);
        Assert.Equal(RunStartResult.Success, state.TryStartRun(1, AgentRole.Writer, null, out _));
    }

    [Fact]
    public void CancelRun_ActiveStatusWithoutLiveRun_ForcesCancelled()
    {
        var state = NewService();
        state.SetStatus(3, new AgentRunStatus(AgentRole.Editor, RunStates.WaitingForInput, 9));

        Assert.True(state.CancelRun(3));
        Assert.Equal(RunStates.Cancelled, state.GetStatus(3)!.State);
        Assert.Equal(RunStartResult.Success, state.TryStartRun(3, AgentRole.Writer, null, out _));
    }

    [Fact]
    public void CancelRun_NothingRunning_ReturnsFalse()
    {
        Assert.False(NewService().CancelRun(42));
    }
}

public class StreamBufferTests
{
    [Fact]
    public void TailFromLineStart_ShortBuffer_ReturnsAll()
    {
        var b = new StreamBuffer();
        b.Append("## Notes");
        Assert.Equal("## Notes", b.TailFromLineStart(512));
    }

    [Fact]
    public void TailFromLineStart_DropsPartialLeadingLine()
    {
        var b = new StreamBuffer();
        b.Append(new string('x', 100) + " ## not a heading\n## Editorial Notes\n");
        var tail = b.TailFromLineStart(40);
        Assert.Equal("## Editorial Notes\n", tail);
    }

    [Fact]
    public void Set_ReplacesContent()
    {
        var b = new StreamBuffer();
        b.Append("  draft  ");
        b.Set("final");
        Assert.Equal("final", b.ToString());
        Assert.Equal(5, b.Length);
    }
}

public class PromptTemplatesTests
{
    private static Book NewBook(int id, string title) => new() { Id = id, Title = title, Genre = "Fantasy", Premise = "P" };

    [Fact]
    public void AncestorReference_NoAncestors_IsEmpty()
    {
        Assert.Equal(string.Empty, PromptTemplates.AncestorReference([]));
    }

    [Fact]
    public void AncestorReference_OlderAncestorGetsOnlyLastFiveChapters()
    {
        var chapters = Enumerable.Range(1, 8)
            .Select(n => new Chapter { Number = n, Title = $"T{n}", Outline = $"O{n}" }).ToList();
        var ancestors = new[]
        {
            new AncestorBookContext(NewBook(2, "Parent"), null, [], [], chapters, IsDirectParent: true),
            new AncestorBookContext(NewBook(1, "Grandparent"), null, [], [], chapters, IsDirectParent: false),
        };

        var text = PromptTemplates.AncestorReference(ancestors);
        var grandparentPart = text[text.IndexOf("Grandparent", StringComparison.Ordinal)..];

        Assert.Contains("Ch.1 \"T1\"", text[..text.IndexOf("Grandparent", StringComparison.Ordinal)]);
        Assert.DoesNotContain("Ch.3 \"T3\"", grandparentPart);
        Assert.Contains("Ch.4 \"T4\"", grandparentPart);
    }

    [Fact]
    public void WriterUser_OmitsEmptyOptionalFields()
    {
        var chapter = new Chapter { Number = 2, Title = "Storm", Outline = "It rains." };
        var text = PromptTemplates.WriterUser(chapter, synopsesBlock: "");
        Assert.Contains("Chapter 2: Storm", text);
        Assert.DoesNotContain("Point of view:", text);
        Assert.DoesNotContain("Story So Far", text);
    }
}
