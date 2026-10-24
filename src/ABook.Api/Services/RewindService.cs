using ABook.Agents;
using ABook.Core.Interfaces;
using ABook.Core.Models;

namespace ABook.Api.Services;

public enum RewindStartResult { Started, Busy, AtCapacity, Stale, TooEarly, NothingToDo, NotFound }

public sealed record RewindStartOutcome(RewindStartResult Result, string Message);

/// <summary>
/// Starting a rewind, shared by the REST controller, the MCP tools and the Telegram bot: checks the preview is still
/// current, then runs it as an agent run (so the book does one thing at a time).
/// </summary>
public class RewindService(IServiceScopeFactory scopes, AgentRunnerService runner, AgentRunStateService runState)
{
    public async Task<RewindStartOutcome> StartAsync(int bookId, DateTime at, string? fingerprint, string? origin)
    {
        if (runState.GetStatus(bookId) is { IsActive: true })
            return new(RewindStartResult.Busy, "The book is busy. Stop the running agent or wait for it to finish, then rewind.");

        using var scope = scopes.CreateScope();
        if (await scope.ServiceProvider.GetRequiredService<IBookRepository>().GetByIdAsync(bookId) is null)
            return new(RewindStartResult.NotFound, "Book not found.");
        RewindPlan plan;
        try { plan = await scope.ServiceProvider.GetRequiredService<IBookRewinder>().PreviewAsync(bookId, at); }
        catch (RewindTooEarlyException ex) { return new(RewindStartResult.TooEarly, ex.Message); }
        if (fingerprint is not null && fingerprint != plan.Fingerprint)
            return new(RewindStartResult.Stale, "The book changed since the preview. Preview the rewind again.");
        if (plan.IsEmpty) return new(RewindStartResult.NothingToDo, "The book is already as it was then — nothing to rewind.");

        var result = runner.TryStart(bookId, AgentRole.Rewind, null, (o, c) => o.RewindAsync(bookId, at, plan.Fingerprint, origin, c));
        return result switch
        {
            RunStartResult.Success => new(RewindStartResult.Started, "Rewinding."),
            RunStartResult.AtCapacity => new(RewindStartResult.AtCapacity, result.ToMessage()),
            _ => new(RewindStartResult.Busy, result.ToMessage()),
        };
    }
}
