using System.Reflection;
using ABook.Agents;
using ABook.Core.Interfaces;
using ABook.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace ABook.Tests;

/// <summary>
/// Applying and undoing the book chat's section proposals through <see cref="ProposalExecutor"/> and the real
/// <see cref="BookContentService"/>, on an in-memory book (<see cref="Store"/>).
/// </summary>
public class BookChatSectionApplyTests
{
    /// <summary>
    /// An in-memory repository with just enough of the structure rules: chapters sit with their section, numbers are
    /// counts of active rows, a section is archived when none of its chapters is active, and a section with one active
    /// chapter mirrors that chapter's plan.
    /// </summary>
    public class Store : DispatchProxy
    {
        public Book Book = new() { Id = 1, Mode = BookMode.Interactive, StoryBibleLockedAt = DateTime.UtcNow, Title = "T", Language = "English" };
        public List<Section> Sections = [];
        public List<Chapter> Chapters = [];
        public List<BookChatMessage> Messages = [];
        public List<ChatProposal> Proposals = [];
        private int _nextId = 100;

        public static (IBookRepository Repo, Store Store) Create()
        {
            var repo = Create<IBookRepository, Store>();
            return (repo, (Store)(object)repo);
        }

        /// <summary>Adds a section with chapters (title, prose) at the end.</summary>
        public Section AddSection(string title, params (string Title, string Content)[] chapters)
        {
            var section = new Section { Id = _nextId++, BookId = 1, Title = title, SortOrder = Sections.Count + 1, Outline = $"{title} plan." };
            Sections.Add(section);
            foreach (var (t, content) in chapters)
                Chapters.Add(new Chapter { Id = _nextId++, BookId = 1, SectionId = section.Id, Title = t, Outline = $"{t} outline.", Content = content,
                                           SortOrder = Chapters.Count + 1, Status = content.Length > 0 ? ChapterStatus.Done : ChapterStatus.Outlined });
            Renumber();
            return section;
        }

        private IEnumerable<Chapter> Ordered() =>
            Chapters.OrderBy(c => Sections.First(s => s.Id == c.SectionId).SortOrder).ThenBy(c => c.SortOrder);

        public void Renumber()
        {
            var i = 0;
            foreach (var s in Sections.OrderBy(s => s.SortOrder)) s.SortOrder = ++i;
            var ordered = Ordered().ToList();
            var n = 0;
            for (var k = 0; k < ordered.Count; k++)
            {
                ordered[k].SortOrder = k + 1;
                if (!ordered[k].IsArchived) ordered[k].Number = ++n;
            }
            var m = 0;
            foreach (var s in Sections.OrderBy(s => s.SortOrder))
            {
                var active = Chapters.Where(c => c.SectionId == s.Id && !c.IsArchived).ToList();
                s.IsArchived = active.Count == 0;
                if (!s.IsArchived) s.Number = ++m;
                if (active.Count == 1) s.CopyPlanFrom(active[0]);
            }
        }

        private Section NewSection(Chapter chapter, int sortOrder)
        {
            foreach (var s in Sections.Where(s => s.SortOrder >= sortOrder)) s.SortOrder++;
            var section = new Section { Id = _nextId++, BookId = 1, SortOrder = sortOrder };
            section.CopyPlanFrom(chapter);
            Sections.Add(section);
            chapter.SectionId = section.Id;
            return section;
        }

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            object? Result()
            {
                switch (method!.Name)
                {
                    case nameof(IBookRepository.GetByIdAsync): return Book;
                    case nameof(IBookRepository.GetChaptersAsync):
                        return Ordered().Where(c => (bool)args![1]! || !c.IsArchived).ToList().AsEnumerable();
                    case nameof(IBookRepository.GetChapterAsync): return Chapters.FirstOrDefault(c => c.Id == (int)args![1]!);
                    case nameof(IBookRepository.AddChapterAsync):
                    {
                        var chapter = (Chapter)args![0]!;
                        chapter.Id = _nextId++;
                        if (chapter.SectionId is int sid) chapter.SortOrder = Chapters.Where(c => c.SectionId == sid).Select(c => c.SortOrder).DefaultIfEmpty().Max() + 1;
                        else NewSection(chapter, Sections.Count + 1);
                        Chapters.Add(chapter);
                        Renumber();
                        return chapter;
                    }
                    case nameof(IBookRepository.InsertChapterAsync):
                    {
                        var chapter = (Chapter)args![0]!;
                        var after = args[1] is int afterId ? Chapters.First(c => c.Id == afterId) : null;
                        if (chapter.SectionId is not null || (bool)args[2]!) throw new NotSupportedException("insert into a section");
                        chapter.Id = _nextId++;
                        NewSection(chapter, after is null ? 1 : Sections.First(s => s.Id == after.SectionId).SortOrder + 1);
                        Chapters.Add(chapter);
                        Renumber();
                        return chapter;
                    }
                    case nameof(IBookRepository.UpdateChapterAsync) or nameof(IBookRepository.UpdateSectionAsync)
                        or nameof(IBookRepository.UpdateAsync):
                        Renumber();
                        return null;
                    case nameof(IBookRepository.AddChapterVersionAsync):
                        Renumber();
                        return args![0];
                    case nameof(IBookRepository.GetActiveChapterVersionIdsAsync): return (IReadOnlyDictionary<int, int>)new Dictionary<int, int>();
                    case nameof(IBookRepository.GetSectionsAsync):
                        return (IReadOnlyList<Section>)Sections.Where(s => (bool)args![1]! || !s.IsArchived).OrderBy(s => s.SortOrder).ToList();
                    case nameof(IBookRepository.GetSectionAsync): return Sections.FirstOrDefault(s => s.Id == (int)args![1]!);
                    case nameof(IBookRepository.MoveSectionAsync):
                    {
                        var list = Sections.OrderBy(s => s.SortOrder).ToList();
                        var moving = list.First(s => s.Id == (int)args![1]!);
                        list.Remove(moving);
                        var index = args![2] is int afterId ? list.FindIndex(s => s.Id == afterId) + 1 : 0;
                        list.Insert(index, moving);
                        for (var k = 0; k < list.Count; k++) list[k].SortOrder = k + 1;
                        Renumber();
                        return null;
                    }
                    case nameof(IBookRepository.ArchiveSectionAsync):
                        foreach (var c in Chapters.Where(c => c.SectionId == (int)args![1]! && !c.IsArchived)) { c.IsArchived = true; c.ArchivedWithSection = true; }
                        Renumber();
                        return null;
                    case nameof(IBookRepository.RestoreSectionAsync):
                        foreach (var c in Chapters.Where(c => c.SectionId == (int)args![1]! && c.ArchivedWithSection)) { c.IsArchived = false; c.ArchivedWithSection = false; }
                        Renumber();
                        return null;
                    case nameof(IBookRepository.ArchiveChapterAsync):
                        Chapters.First(c => c.Id == (int)args![1]!).IsArchived = true;
                        Renumber();
                        return null;
                    case nameof(IBookRepository.GetCharacterCardsAsync): return Enumerable.Empty<CharacterCard>();
                    case nameof(IBookRepository.GetPlotThreadsAsync): return Enumerable.Empty<PlotThread>();
                    case nameof(IBookRepository.AddChatMessageAsync):
                    {
                        var message = (BookChatMessage)args![0]!;
                        message.Id = _nextId++;
                        Messages.Add(message);
                        foreach (var p in message.Proposals)
                        {
                            p.Id = _nextId++;
                            p.MessageId = message.Id;
                            p.CreatedAt = DateTime.UtcNow;
                            Proposals.Add(p);
                        }
                        return message;
                    }
                    case nameof(IBookRepository.GetProposalAsync): return Proposals.FirstOrDefault(p => p.Id == (int)args![1]!);
                    case nameof(IBookRepository.GetProposalsAsync):
                    {
                        var ids = ((IEnumerable<int>)args![1]!).ToHashSet();
                        return (IReadOnlyList<ChatProposal>)Proposals.Where(p => ids.Contains(p.Id)).OrderBy(p => p.MessageId).ThenBy(p => p.Ordinal).ToList();
                    }
                    case nameof(IBookRepository.GetProposalsByStatusAsync):
                        return (IReadOnlyList<ChatProposal>)Proposals.Where(p => p.Status == (ChatProposalStatus)args![1]!).ToList();
                    case nameof(IBookRepository.GetOpenProposalsSinceAsync):
                        return (IReadOnlyList<ChatProposal>)Proposals
                            .Where(p => p.CreatedAt >= (DateTime)args![1]! && p.Status is ChatProposalStatus.Pending or ChatProposalStatus.Failed).ToList();
                    case nameof(IBookRepository.UpdateProposalAsync): return null;
                    case nameof(IBookRepository.DiscardPendingChanges): return null;
                    default: throw new NotSupportedException(method.Name);
                }
            }

            var result = Result();
            var type = method!.ReturnType;
            if (type == typeof(void)) return null;
            if (type == typeof(Task)) return Task.CompletedTask;
            // Task<T>: wrap the result in a completed task of the declared type.
            var inner = type.GetGenericArguments()[0];
            return typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(inner).Invoke(null, [result]);
        }
    }

    /// <summary>A planner whose breakdown is given, instead of an LLM call.</summary>
    private class FixedPlanner(IBookRepository repo, params Chapter[] parts)
        : PlannerAgent(repo, null!, null!, null!, null!, NullLoggerFactory.Instance)
    {
        public override async Task<BreakdownPlan> PlanBreakdownAsync(int bookId, int sectionId, int? chapterCount, CancellationToken ct)
        {
            var section = (await Repo.GetSectionAsync(bookId, sectionId))!;
            var current = (await Repo.GetChaptersAsync(bookId)).Where(c => c.SectionId == sectionId).ToList();
            return new BreakdownPlan(section, current, [.. parts]);
        }
    }

    private sealed record Harness(Store Store, BookChatAgent Chat, ProposalExecutor Executor);

    private static Harness Create(params Chapter[] breakdown)
    {
        var (repo, store) = Store.Create();
        var notifier = DispatchProxy.Create<IBookNotifier, ProposalQueueTests.NullNotifier>();
        var state = new AgentRunStateService(null!, NullLogger<AgentRunStateService>.Instance);
        var content = new BookContentService(repo, null!, null!, notifier, state, NullLoggerFactory.Instance);
        var chat = new BookChatAgent(repo, null!, null!, notifier, state, NullLoggerFactory.Instance, content, null!);
        var executor = new ProposalExecutor(repo, notifier, state, content, chat, null!, null!, null!, null!, null!,
            new FixedPlanner(repo, breakdown), NullLogger<ProposalExecutor>.Instance);
        return new Harness(store, chat, executor);
    }

    private static RawChatAction A(string type, int? section = null, int? chapter = null, params (string, string)[] changes) =>
        new(type, "", chapter, null, changes.Select(c => KeyValuePair.Create(c.Item1, c.Item2)).ToList(), null, null, null, null, null, section);

    /// <summary>Proposes the actions as the chat would (normalized, previewed) and applies them all as one batch.</summary>
    private static async Task<List<ChatProposal>> ProposeAndApplyAsync(Harness h, params RawChatAction[] actions)
    {
        var message = await h.Chat.ProposeAsync(1, "test", actions);
        Assert.Equal(actions.Length, message.Proposals.Count);
        await h.Executor.ApplyAsync(1, message.Proposals.Select(p => p.Id).ToList(), CancellationToken.None);
        return message.Proposals.OrderBy(p => p.Ordinal).ToList();
    }

    private static string Order(Store s) =>
        string.Join(" | ", s.Sections.Where(x => !x.IsArchived).OrderBy(x => x.SortOrder)
            .Select(x => $"S{x.Number}:" + string.Join(",", s.Chapters.Where(c => c.SectionId == x.Id && !c.IsArchived)
                .OrderBy(c => c.SortOrder).Select(c => $"{c.Number}.{c.Title}"))));

    [Fact]
    public async Task AddSection_AtTheEnd_OffersItsBreakdown_AndUndoRemovesIt()
    {
        var h = Create();
        h.Store.AddSection("One", ("One", "Prose."));

        var p = Assert.Single(await ProposeAndApplyAsync(h,
            A(BookChatActions.AddSection, changes: [("title", "Flight"), ("outline", "They run."), ("chapters", "3")])));

        Assert.Equal(ChatProposalStatus.Applied, p.Status);
        Assert.Equal("S1:1.One | S2:2.Flight", Order(h.Store));
        var section = h.Store.Sections.Single(s => s.Title == "Flight");
        Assert.Equal((3, "They run."), (section.PlannedChapters, section.Outline));
        Assert.Equal($"section:{section.Id}", p.Target);
        Assert.Contains("\"chapters\": \"3\"", p.PreviewAfter);
        // The next step offered for a section planned for several chapters is its breakdown.
        var offer = h.Store.Proposals.Single(o => o.Type == BookChatActions.BreakDownSection);
        Assert.Equal(section.Id, ChatActionPayload.FromJson(offer.PayloadJson).SectionId);

        await h.Executor.UndoAsync(1, p.Id, CancellationToken.None);
        Assert.Equal(ChatProposalStatus.Undone, p.Status);
        Assert.Equal("S1:1.One", Order(h.Store));
    }

    [Fact]
    public async Task AddSection_AfterAnEarlierSection_RenumbersTheLaterOnes()
    {
        var h = Create();
        h.Store.AddSection("One", ("One", "Prose."));
        h.Store.AddSection("Two", ("Two", "Prose."));

        var p = Assert.Single(await ProposeAndApplyAsync(h, A(BookChatActions.AddSection, section: 1, changes: [("title", "Interlude")])));

        Assert.Equal(ChatProposalStatus.Applied, p.Status);
        Assert.Equal("S1:1.One | S2:2.Interlude | S3:3.Two", Order(h.Store));
    }

    [Fact]
    public async Task UpdateSectionPlan_OfASectionOfSeveralChapters_SetsItsOwnPlanAndCount_AndUndoRestoresThem()
    {
        var h = Create();
        var sea = h.Store.AddSection("Sea", ("Storm", ""), ("Calm", ""));

        var p = Assert.Single(await ProposeAndApplyAsync(h,
            A(BookChatActions.UpdateSectionPlan, section: 1, changes: [("outline", "A longer voyage."), ("chapters", "4")])));

        Assert.Equal(ChatProposalStatus.Applied, p.Status);
        Assert.Equal(("A longer voyage.", 4), (sea.Outline, sea.PlannedChapters));
        Assert.Equal("Storm outline.", h.Store.Chapters[0].Outline);

        await h.Executor.UndoAsync(1, p.Id, CancellationToken.None);
        Assert.Equal(ChatProposalStatus.Undone, p.Status);
        Assert.Equal(("Sea plan.", 1), (sea.Outline, sea.PlannedChapters));
    }

    [Fact]
    public async Task UpdateSectionPlan_OfASectionOfOneChapter_EditsThatChapter()
    {
        var h = Create();
        h.Store.AddSection("One", ("One", ""));

        await ProposeAndApplyAsync(h, A(BookChatActions.UpdateSectionPlan, section: 1, changes: [("outline", "New plan.")]));

        Assert.Equal("New plan.", h.Store.Chapters[0].Outline);
        Assert.Equal("New plan.", h.Store.Sections[0].Outline);
    }

    [Fact]
    public async Task MoveSection_RemembersWhereItWas_AndUndoPutsItBack()
    {
        var h = Create();
        h.Store.AddSection("One", ("One", "Prose."));
        h.Store.AddSection("Two", ("Two", ""));
        h.Store.AddSection("Three", ("Three", ""));

        var p = Assert.Single(await ProposeAndApplyAsync(h, A(BookChatActions.MoveSection, section: 3, changes: [("after", "0")])));

        Assert.Equal(ChatProposalStatus.Applied, p.Status);
        Assert.Equal("S1:1.Three | S2:2.One | S3:3.Two", Order(h.Store));
        await h.Executor.UndoAsync(1, p.Id, CancellationToken.None);
        Assert.Equal(ChatProposalStatus.Undone, p.Status);
        Assert.Equal("S1:1.One | S2:2.Two | S3:3.Three", Order(h.Store));
    }

    [Fact]
    public async Task ArchiveSection_RemovesItsChapters_AndUndoBringsThemBack()
    {
        var h = Create();
        h.Store.AddSection("One", ("One", "Prose."));
        h.Store.AddSection("Sea", ("Storm", ""), ("Calm", ""));

        var p = Assert.Single(await ProposeAndApplyAsync(h, A(BookChatActions.ArchiveSection, section: 2)));

        Assert.Equal(ChatProposalStatus.Applied, p.Status);
        Assert.Equal("S1:1.One", Order(h.Store));
        await h.Executor.UndoAsync(1, p.Id, CancellationToken.None);
        Assert.Equal("S1:1.One | S2:2.Storm,3.Calm", Order(h.Store));
    }

    [Fact]
    public async Task AddChapter_ToASection_GoesAtItsEnd_AndUndoRemovesIt()
    {
        var h = Create();
        h.Store.AddSection("Sea", ("Storm", ""));
        h.Store.AddSection("Land", ("Shore", "Prose."));

        var p = Assert.Single(await ProposeAndApplyAsync(h, A(BookChatActions.AddChapter, section: 1, changes: [("title", "Fog"), ("outline", "Lost.")])));

        Assert.Equal(ChatProposalStatus.Applied, p.Status);
        Assert.Equal("S1:1.Storm,2.Fog | S2:3.Shore", Order(h.Store));
        // The section keeps the plan it had while it was one chapter.
        Assert.Equal("Storm outline.", h.Store.Sections[0].Outline);
        await h.Executor.UndoAsync(1, p.Id, CancellationToken.None);
        Assert.Equal("S1:1.Storm | S2:2.Shore", Order(h.Store));
    }

    [Fact]
    public async Task WriteSection_QueuesAWriteForEachUnwrittenChapter_OneAtATime()
    {
        var h = Create();
        h.Store.AddSection("Sea", ("Storm", "Prose."), ("Calm", ""), ("Fog", ""));

        var p = Assert.Single(await ProposeAndApplyAsync(h, A(BookChatActions.WriteSection, section: 1)));

        Assert.Equal(ChatProposalStatus.Applied, p.Status);
        var writes = h.Store.Proposals.Where(o => o.Type == BookChatActions.WriteChapter).OrderBy(o => o.Ordinal).ToList();
        Assert.Equal([2, 3], writes.Select(w => ChatActionPayload.FromJson(w.PayloadJson).ChapterNumber!.Value));
        // The second waits for the first (which fails here: the test has no writer, so it goes back to Pending).
        Assert.Equal(writes[0].Id, writes[1].QueuedAfterProposalId);
        Assert.NotEqual(ChatProposalStatus.Pending, writes[0].Status);
        Assert.Contains("its own card", await h.Executor.UndoBlockerAsync(1, p));
    }

    [Fact]
    public async Task BreakDownStep_PostsTheChaptersAsProposals_AndApplyingThemKeepsTheSectionPlan()
    {
        var h = Create(
            new Chapter { Title = "Storm", Outline = "The storm hits." },
            new Chapter { Title = "Wreck", Outline = "The ship breaks." },
            new Chapter { Title = "Raft", Outline = "They drift." });
        h.Store.AddSection("One", ("One", "Prose."));
        h.Store.AddSection("Sea", ("Voyage", ""));
        var sea = h.Store.Sections[1];
        sea.PlannedChapters = 3;

        var step = Assert.Single(await ProposeAndApplyAsync(h, A(BookChatActions.BreakDownSection, section: 2)));

        Assert.Equal(ChatProposalStatus.Applied, step.Status);
        var posted = h.Store.Proposals.Where(o => o.MessageId != step.MessageId).OrderBy(o => o.Ordinal).ToList();
        Assert.Equal([BookChatActions.AddChapter, BookChatActions.AddChapter, BookChatActions.UpdateChapterOutline], posted.Select(o => o.Type));
        Assert.Equal(posted[0].Id, posted[2].DependsOnProposalId);
        Assert.Equal("Voyage outline.", h.Store.Chapters[1].Outline);   // nothing saved yet

        await h.Executor.ApplyAsync(1, posted.Select(o => o.Id).ToList(), CancellationToken.None);

        Assert.All(posted, o => Assert.Equal(ChatProposalStatus.Applied, o.Status));
        Assert.Equal("S1:1.One | S2:2.Storm,3.Wreck,4.Raft", Order(h.Store));
        Assert.Equal("Voyage outline.", sea.Outline);   // the section keeps its overall plan
        Assert.Equal("The storm hits.", h.Store.Chapters.Single(c => c.Title == "Storm").Outline);
        // The planned chapters come with one card to write the section.
        Assert.Contains(h.Store.Proposals, o => o.Type == BookChatActions.WriteSection && o.Status == ChatProposalStatus.Pending);
    }
}
