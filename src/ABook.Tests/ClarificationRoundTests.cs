using System.Reflection;
using ABook.Agents;
using ABook.Api.Services;
using ABook.Core.Interfaces;
using ABook.Core.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ABook.Tests;

public class ClarificationRoundTests
{
    private static AgentRunStateService NewState() =>
        new(new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            NullLogger<AgentRunStateService>.Instance);

    /// <summary>In-memory stand-in for the few repository calls <see cref="ClarificationService"/> makes.</summary>
    private class FakeRepo : DispatchProxy
    {
        public Book Book = new() { Id = 1 };
        public List<Clarification> Items = [];

        public static (IBookRepository Repo, FakeRepo Fake) Create()
        {
            var repo = Create<IBookRepository, FakeRepo>();
            return (repo, (FakeRepo)(object)repo);
        }

        protected override object? Invoke(MethodInfo? method, object?[]? args) => method!.Name switch
        {
            nameof(IBookRepository.GetClarificationsAsync) =>
                Task.FromResult<IReadOnlyList<Clarification>>(Items.Where(c => (bool)args![1]! || !c.IsArchived).ToList()),
            nameof(IBookRepository.UpdateClarificationAsync) => Task.CompletedTask,
            nameof(IBookRepository.GetByIdAsync) => Task.FromResult<Book?>(Book),
            nameof(IBookRepository.UpdateAsync) => Task.CompletedTask,
            nameof(IBookRepository.ArchiveClarificationsAsync) => ArchiveAll(),
            _ => throw new NotSupportedException(method.Name),
        };

        private Task ArchiveAll()
        {
            Items.ForEach(c => c.IsArchived = true);
            return Task.CompletedTask;
        }
    }

    private static Clarification Open(int id) => new() { Id = id, BookId = 1, Question = $"Q{id}?" };

    [Fact]
    public async Task AnsweringTheLastOpenQuestion_CompletesTheRound_AndResumesTheWaitingRun()
    {
        var (repo, fake) = FakeRepo.Create();
        fake.Items = [Open(1), Open(2)];
        var state = NewState();
        var service = new ClarificationService(repo, state);
        var waiting = state.WaitForClarificationsAsync(1);

        await service.UpdateAsync(fake.Items[0], null, "First answer", null);
        Assert.False(waiting.IsCompleted);
        Assert.Equal(PlanningPhaseStatus.NotStarted, fake.Book.ClarificationsStatus);

        await service.UpdateAsync(fake.Items[1], null, "Second answer", null);
        Assert.True(await waiting.WaitAsync(TimeSpan.FromSeconds(1)));
        Assert.Equal(PlanningPhaseStatus.Complete, fake.Book.ClarificationsStatus);
    }

    [Fact]
    public async Task SkippingTheLastOpenQuestion_AlsoCompletesTheRound()
    {
        var (repo, fake) = FakeRepo.Create();
        fake.Items = [Open(1)];
        var state = NewState();
        var waiting = state.WaitForClarificationsAsync(1);

        await new ClarificationService(repo, state).UpdateAsync(fake.Items[0], null, null, archived: true);

        Assert.True(await waiting.WaitAsync(TimeSpan.FromSeconds(1)));
        Assert.Equal(PlanningPhaseStatus.Complete, fake.Book.ClarificationsStatus);
    }

    [Fact]
    public async Task EditingAnAnsweredClarification_LeavesTheRoundAlone()
    {
        var (repo, fake) = FakeRepo.Create();
        fake.Items = [new Clarification { Id = 1, BookId = 1, Question = "Q?", Answer = "A" }];

        await new ClarificationService(repo, NewState()).UpdateAsync(fake.Items[0], null, "Better answer", null);

        Assert.Equal(PlanningPhaseStatus.NotStarted, fake.Book.ClarificationsStatus);
    }

    [Fact]
    public async Task ArchiveAll_ReleasesTheWaitingRun_WithoutCompletingTheRound()
    {
        var (repo, fake) = FakeRepo.Create();
        fake.Items = [Open(1)];
        var state = NewState();
        var waiting = state.WaitForClarificationsAsync(1);

        await new ClarificationService(repo, state).ArchiveAllAsync(1);

        Assert.False(await waiting.WaitAsync(TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task Stop_CancelsTheWait()
    {
        var state = NewState();
        var waiting = state.WaitForClarificationsAsync(1);

        state.CancelRun(1);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
    }

    [Fact]
    public void OpenMeansActiveAndUnanswered()
    {
        Assert.True(Open(1).IsOpen);
        Assert.False(new Clarification { Answer = "A" }.IsOpen);
        Assert.False(new Clarification { IsArchived = true }.IsOpen);
    }
}
