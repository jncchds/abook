using System.Reflection;
using ABook.Agents;
using ABook.Core.Interfaces;
using ABook.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace ABook.Tests;

/// <summary>Queued chapter writes: each waits for the chapter before it to be written, checked and reviewed.</summary>
public class ProposalQueueTests
{
    public class FakeRepo : DispatchProxy
    {
        public List<ChatProposal> Proposals = [];

        public static (IBookRepository Repo, FakeRepo Fake) Create()
        {
            var repo = Create<IBookRepository, FakeRepo>();
            return (repo, (FakeRepo)(object)repo);
        }

        protected override object? Invoke(MethodInfo? method, object?[]? args) => method!.Name switch
        {
            nameof(IBookRepository.GetProposalsByStatusAsync) =>
                Task.FromResult<IReadOnlyList<ChatProposal>>(Proposals.Where(p => p.Status == (ChatProposalStatus)args![1]!).ToList()),
            nameof(IBookRepository.GetProposalAsync) => Task.FromResult(Proposals.FirstOrDefault(p => p.Id == (int)args![1]!)),
            nameof(IBookRepository.GetOpenProposalsSinceAsync) => Task.FromResult<IReadOnlyList<ChatProposal>>(Proposals
                .Where(p => p.CreatedAt >= (DateTime)args![1]! && p.Status is ChatProposalStatus.Pending or ChatProposalStatus.Failed)
                .ToList()),
            nameof(IBookRepository.UpdateProposalAsync) => Task.CompletedTask,
            _ => throw new NotSupportedException(method.Name),
        };
    }

    public class NullNotifier : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? method, object?[]? args) => Task.CompletedTask;
    }

    private static readonly DateTime T0 = new(2026, 10, 25, 10, 0, 0, DateTimeKind.Utc);

    private static ProposalExecutor Create(params ChatProposal[] proposals)
    {
        var (repo, fake) = FakeRepo.Create();
        fake.Proposals = [.. proposals];
        return new ProposalExecutor(repo, DispatchProxy.Create<IBookNotifier, NullNotifier>(), null!, null!, null!,
            null!, null!, null!, null!, null!, NullLogger<ProposalExecutor>.Instance);
    }

    private static ChatProposal Write(int id, ChatProposalStatus status, int? after = null, DateTime? appliedAt = null) => new()
    {
        Id = id, BookId = 1, Type = BookChatActions.WriteChapter, Label = $"Write {id}", Status = status,
        QueuedAfterProposalId = after, AppliedAt = appliedAt, CreatedAt = T0.AddMinutes(-10),
    };

    private static ChatProposal FollowUp(int id, ChatProposalStatus status) => new()
    {
        Id = id, BookId = 1, Type = BookChatActions.PatchChapter, Label = $"Fix {id}", Status = status, CreatedAt = T0.AddMinutes(5),
    };

    [Fact]
    public async Task Queued_WaitsWhileThePreviousChapterIsNotWrittenYet()
    {
        var executor = Create(Write(1, ChatProposalStatus.Applying), Write(2, ChatProposalStatus.Queued, after: 1));
        Assert.Null(await executor.NextQueuedAsync(1));
    }

    [Fact]
    public async Task Queued_WaitsForTheFollowUpsOfThePreviousChapter_ThenStarts()
    {
        var fix = FollowUp(10, ChatProposalStatus.Pending);
        var executor = Create(Write(1, ChatProposalStatus.Applied, appliedAt: T0), Write(2, ChatProposalStatus.Queued, after: 1), fix);
        Assert.Null(await executor.NextQueuedAsync(1));

        fix.Status = ChatProposalStatus.Rejected;
        Assert.Equal(2, (await executor.NextQueuedAsync(1))?.Id);
    }

    [Fact]
    public async Task Queued_StartsRightAwayWhenTheCheckFoundNothing()
    {
        var executor = Create(Write(1, ChatProposalStatus.Applied, appliedAt: T0), Write(2, ChatProposalStatus.Queued, after: 1));
        Assert.Equal(2, (await executor.NextQueuedAsync(1))?.Id);
    }

    [Fact]
    public async Task OlderOpenSuggestions_DoNotHoldTheQueue()
    {
        var older = FollowUp(10, ChatProposalStatus.Pending);
        older.CreatedAt = T0.AddMinutes(-30);
        var executor = Create(Write(1, ChatProposalStatus.Applied, appliedAt: T0), Write(2, ChatProposalStatus.Queued, after: 1), older);
        Assert.Equal(2, (await executor.NextQueuedAsync(1))?.Id);
    }

    [Theory]
    [InlineData(ChatProposalStatus.Failed)]
    [InlineData(ChatProposalStatus.Rejected)]
    [InlineData(ChatProposalStatus.Undone)]
    public async Task Queued_GoesBackToPending_WhenThePreviousOneDidNotGoThrough(ChatProposalStatus status)
    {
        var next = Write(2, ChatProposalStatus.Queued, after: 1);
        var executor = Create(Write(1, status), next);
        Assert.Null(await executor.NextQueuedAsync(1));
        Assert.Equal(ChatProposalStatus.Pending, next.Status);
        Assert.Contains("did not go through", next.ResultNote);
    }

    [Fact]
    public void QueuedSuggestions_CanBeStartedEarly() =>
        Assert.True(ProposalExecutor.CanApply(Write(2, ChatProposalStatus.Queued, after: 1)));
}
