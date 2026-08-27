using ABook.Agents;
using ABook.Api.Mcp;
using ABook.Api.Services;
using ABook.Core.Interfaces;
using ABook.Core.Models;
using ABook.Infrastructure.Data;
using ABook.Infrastructure.Repositories;
using ABook.Infrastructure.VectorStore;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using System.Security.Claims;
using System.Text.Json;

namespace ABook.Tests;

public class McpLlmConfigurationSafetyTests
{
    [Fact]
    public async Task SetLlmConfig_WhenOnlyGlobalExists_CreatesUserDefaultWithoutMutatingGlobal()
    {
        await using var fixture = await McpFixture.CreateAsync();
        var global = new LlmConfiguration
        {
            Provider = LlmProvider.Ollama,
            ModelName = "global-model",
            Endpoint = "http://global"
        };
        fixture.Db.LlmConfigurations.Add(global);
        await fixture.Db.SaveChangesAsync();

        var tools = fixture.CreateUserTools(userId: 1);
        await tools.SetLlmConfig("OpenAI", "user-model", "http://user/v1", "user-key");

        var persistedGlobal = await fixture.Repo.GetExactLlmConfigAsync(null, null);
        var userDefault = await fixture.Repo.GetExactLlmConfigAsync(null, 1);

        Assert.NotNull(persistedGlobal);
        Assert.Equal(LlmProvider.Ollama, persistedGlobal!.Provider);
        Assert.Equal("global-model", persistedGlobal.ModelName);
        Assert.Equal("http://global", persistedGlobal.Endpoint);

        Assert.NotNull(userDefault);
        Assert.Equal(1, userDefault!.UserId);
        Assert.Equal(LlmProvider.OpenAI, userDefault.Provider);
        Assert.Equal("user-model", userDefault.ModelName);
        Assert.Equal("http://user/v1", userDefault.Endpoint);
        Assert.Equal("user-key", userDefault.ApiKey);
    }

    [Fact]
    public async Task ApplyPreset_WhenOnlyGlobalExists_CreatesUserDefaultWithoutMutatingGlobal()
    {
        await using var fixture = await McpFixture.CreateAsync();
        fixture.Db.LlmConfigurations.Add(new LlmConfiguration
        {
            Provider = LlmProvider.Ollama,
            ModelName = "global-model",
            Endpoint = "http://global"
        });
        var preset = new LlmPreset
        {
            UserId = 1,
            Name = "User preset",
            Provider = LlmProvider.OpenAI,
            ModelName = "preset-model",
            Endpoint = "http://preset/v1",
            ApiKey = "preset-key"
        };
        fixture.Db.LlmPresets.Add(preset);
        await fixture.Db.SaveChangesAsync();

        var tools = fixture.CreateUserTools(userId: 1);
        await tools.ApplyPreset(preset.Id);

        var persistedGlobal = await fixture.Repo.GetExactLlmConfigAsync(null, null);
        var userDefault = await fixture.Repo.GetExactLlmConfigAsync(null, 1);

        Assert.NotNull(persistedGlobal);
        Assert.Equal("global-model", persistedGlobal!.ModelName);
        Assert.Equal("http://global", persistedGlobal.Endpoint);

        Assert.NotNull(userDefault);
        Assert.Equal(LlmProvider.OpenAI, userDefault!.Provider);
        Assert.Equal("preset-model", userDefault.ModelName);
        Assert.Equal("preset-key", userDefault.ApiKey);
    }
}

public class McpOwnershipAndStatusTests
{
    [Fact]
    public async Task GetAgentStatus_TerminalState_IsNotReportedAsRunning()
    {
        await using var fixture = await McpFixture.CreateAsync();
        var book = await fixture.AddBookAsync(userId: 1);
        fixture.RunState.SetStatus(book.Id, new AgentRunStatus(AgentRole.Planner, "Failed", null));

        var tools = new BookMcpTools(fixture.Repo, fixture.RunState, McpFixture.HttpForUser(1));
        var json = await tools.GetAgentStatus(book.Id);
        using var doc = JsonDocument.Parse(json);

        Assert.False(doc.RootElement.GetProperty("running").GetBoolean());
        Assert.Equal("Failed", doc.RootElement.GetProperty("state").GetString());
    }

    [Fact]
    public async Task GetAgentStatus_OtherUsersBook_IsRejected()
    {
        await using var fixture = await McpFixture.CreateAsync();
        var book = await fixture.AddBookAsync(userId: 1);
        var tools = new BookMcpTools(fixture.Repo, fixture.RunState, McpFixture.HttpForUser(2));

        await Assert.ThrowsAsync<McpException>(() => tools.GetAgentStatus(book.Id));
    }

    [Fact]
    public async Task StopWorkflow_OtherUsersBook_IsRejected()
    {
        await using var fixture = await McpFixture.CreateAsync();
        var book = await fixture.AddBookAsync(userId: 1);
        var tools = fixture.CreateAgentTools(userId: 2);

        await Assert.ThrowsAsync<McpException>(() => tools.StopWorkflow(book.Id));
    }

    [Fact]
    public async Task AnswerAgentQuestion_OtherUsersQuestion_IsRejected()
    {
        await using var fixture = await McpFixture.CreateAsync();
        var book = await fixture.AddBookAsync(userId: 1);
        var message = await fixture.Repo.AddMessageAsync(new AgentMessage
        {
            BookId = book.Id,
            AgentRole = AgentRole.Planner,
            MessageType = MessageType.Question,
            Content = "Owner-only question?"
        });
        var tools = fixture.CreateAgentTools(userId: 2);

        await Assert.ThrowsAsync<McpException>(() => tools.AnswerAgentQuestion(message.Id, "No access"));
    }
}

public class McpAgentDispatchTests
{
    [Fact]
    public async Task RunContinuityCheck_ForwardsFocusedChapterId()
    {
        await using var fixture = await McpFixture.CreateAsync();
        var book = await fixture.AddBookAsync(userId: 1);
        var chapter = await fixture.Repo.AddChapterAsync(new Chapter
        {
            BookId = book.Id,
            Number = 1,
            Title = "Focused chapter",
            Outline = "Audit"
        });
        var tools = fixture.CreateAgentTools(userId: 1);

        await tools.RunContinuityCheck(book.Id, chapter.Id);
        var observed = await fixture.Orchestrator.ContinuityCall.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(book.Id, observed.BookId);
        Assert.Equal(chapter.Id, observed.ChapterId);
    }

    [Fact]
    public async Task GenerateBook_WhenAtCapacity_RejectsBeforeCreatingBook()
    {
        await using var fixture = await McpFixture.CreateAsync();
        fixture.RunState.MaxConcurrentRuns = 1;
        fixture.RunState.SetStatus(999, new AgentRunStatus(AgentRole.Writer, "Running", 1));
        var tools = fixture.CreateUserTools(userId: 1);
        var before = await fixture.Db.Books.CountAsync();

        var ex = await Assert.ThrowsAsync<McpException>(() =>
            tools.GenerateBook("Blocked", "Should not be persisted", "Test", 3));

        Assert.Contains("maximum concurrent agent capacity", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(before, await fixture.Db.Books.CountAsync());
    }
    [Fact]
    public async Task StopWorkflow_CancelsPerChapterWriteToken()
    {
        await using var fixture = await McpFixture.CreateAsync();
        var book = await fixture.AddBookAsync(userId: 1);
        var chapter = await fixture.Repo.AddChapterAsync(new Chapter
        {
            BookId = book.Id,
            Number = 1,
            Title = "Cancelable chapter",
            Outline = "Audit"
        });
        fixture.Orchestrator.BlockWritesUntilCancelled = true;
        var tools = fixture.CreateAgentTools(userId: 1);

        await tools.WriteChapter(book.Id, chapter.Id);
        await fixture.Orchestrator.WriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await tools.StopWorkflow(book.Id);

        await fixture.Orchestrator.WriteCancelled.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(fixture.Orchestrator.LastWriteToken.IsCancellationRequested);
    }
}

internal sealed class McpFixture : IAsyncDisposable
{
    private readonly ServiceProvider _services;

    private McpFixture(AppDbContext db, ServiceProvider services, RecordingOrchestrator orchestrator)
    {
        Db = db;
        Repo = new BookRepository(db);
        Users = new UserRepository(db);
        _services = services;
        Orchestrator = orchestrator;
        var scopeFactory = services.GetRequiredService<IServiceScopeFactory>();
        RunState = new AgentRunStateService(scopeFactory, services.GetRequiredService<ILogger<AgentRunStateService>>());
        Runner = new AgentRunnerService(scopeFactory, RunState, services.GetRequiredService<ILogger<AgentRunnerService>>());
    }

    public AppDbContext Db { get; }
    public BookRepository Repo { get; }
    public UserRepository Users { get; }
    public AgentRunStateService RunState { get; }
    public AgentRunnerService Runner { get; }
    public RecordingOrchestrator Orchestrator { get; }

    public static async Task<McpFixture> CreateAsync()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"mcp-tests-{Guid.NewGuid():N}")
            .Options;
        var db = new McpTestDbContext(options);
        var orchestrator = new RecordingOrchestrator();
        var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton<IAgentOrchestrator>(orchestrator)
            .BuildServiceProvider();

        db.Users.AddRange(
            new AppUser { Id = 1, Username = "owner", PasswordHash = "x" },
            new AppUser { Id = 2, Username = "other", PasswordHash = "x" });
        await db.SaveChangesAsync();
        return new McpFixture(db, services, orchestrator);
    }

    public async Task<Book> AddBookAsync(int userId)
    {
        var book = await Repo.AddAsync(new Book
        {
            UserId = userId,
            Title = $"Book {Guid.NewGuid():N}",
            Premise = "MCP safety test",
            Genre = "Test",
            TargetChapterCount = 1
        });
        return book;
    }

    public UserMcpTools CreateUserTools(int userId) =>
        new(Repo, Users, RunState, Runner, HttpForUser(userId));

    public AgentMcpTools CreateAgentTools(int userId) =>
        new(_services.GetRequiredService<IServiceScopeFactory>(), RunState, Runner, Repo, HttpForUser(userId));

    public static IHttpContextAccessor HttpForUser(int userId)
    {
        var context = new DefaultHttpContext();
        context.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, userId.ToString())], "test"));
        return new HttpContextAccessor { HttpContext = context };
    }

    public async ValueTask DisposeAsync()
    {
        await Db.DisposeAsync();
        await _services.DisposeAsync();
    }
}


internal sealed class McpTestDbContext(DbContextOptions<AppDbContext> options) : AppDbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.Ignore<ChapterEmbedding>();
    }
}
internal sealed class RecordingOrchestrator : IAgentOrchestrator
{
    public TaskCompletionSource<(int BookId, int? ChapterId)> ContinuityCall { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource<bool> WriteStarted { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource<bool> WriteCancelled { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    public bool BlockWritesUntilCancelled { get; set; }
    public CancellationToken LastWriteToken { get; private set; }

    public Task StartPlanningAsync(int bookId, CancellationToken ct = default) => Task.CompletedTask;

    public async Task StartWritingAsync(int bookId, int chapterId, CancellationToken ct = default)
    {
        LastWriteToken = ct;
        WriteStarted.TrySetResult(true);
        if (!BlockWritesUntilCancelled) return;
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            WriteCancelled.TrySetResult(true);
            throw;
        }
    }

    public Task StartEditingAsync(int bookId, int chapterId, CancellationToken ct = default) => Task.CompletedTask;

    public Task StartContinuityCheckAsync(int bookId, int? chapterId = null, CancellationToken ct = default)
    {
        ContinuityCall.TrySetResult((bookId, chapterId));
        return Task.CompletedTask;
    }

    public Task StartWorkflowAsync(int bookId, CancellationToken ct = default) => Task.CompletedTask;
    public Task ContinueWorkflowAsync(int bookId, CancellationToken ct = default) => Task.CompletedTask;
    public Task ContinuePlanningAsync(int bookId, CancellationToken ct = default) => Task.CompletedTask;
    public Task ResumeWithAnswerAsync(int messageId, string answer, CancellationToken ct = default) => Task.CompletedTask;
}
