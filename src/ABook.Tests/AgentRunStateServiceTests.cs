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

    [Fact]
    public void GetLiveStream_ReturnsOnlyTheCallStreamingNow()
    {
        var state = NewService();
        var bible = state.GetOrCreateStreamBuffer(1, null, nameof(AgentRole.StoryBibleAgent));
        bible.Begin(live: true);
        bible.Append("# Bible");
        bible.End();
        var writer = state.GetOrCreateStreamBuffer(1, 4, nameof(AgentRole.Writer));
        writer.Begin(live: true);
        writer.Append("Once upon");

        var live = state.GetLiveStream(1);
        Assert.Equal(new LiveStream(nameof(AgentRole.Writer), 4, "Once upon"), live);
        Assert.Null(state.GetLiveStream(2));

        writer.End();
        Assert.Null(state.GetLiveStream(1));
    }

    [Fact]
    public void GetLiveStream_IgnoresCallsNotForwardedToUi()
    {
        var state = NewService();
        var checker = state.GetOrCreateStreamBuffer(1, null, nameof(AgentRole.ContinuityChecker));
        checker.Begin(live: false);
        checker.Append("{\"issues\": []}");
        Assert.Null(state.GetLiveStream(1));
    }

    [Fact]
    public void TryStartRun_ClearsPreviousRunsProgress()
    {
        var state = NewService();
        state.SetLastProgress(1, "Workflow complete!");
        state.TryStartRun(1, AgentRole.Planner, null, out _);
        Assert.Null(state.GetLastProgress(1));
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

    [Fact]
    public void HideFrom_KeepsContentButLimitsVisibleText()
    {
        var b = new StreamBuffer();
        b.Begin(live: true);
        b.Append("Prose.\n");
        b.HideFrom(b.Length);
        b.Append("## Editorial Notes\n");
        b.HideFrom(0); // only the first stop point counts
        Assert.Equal("Prose.\n", b.VisibleText());
        Assert.Equal("Prose.\n## Editorial Notes\n", b.ToString());

        b.Begin(live: true);
        b.Append("Next call");
        Assert.Equal("Next call", b.VisibleText());
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
