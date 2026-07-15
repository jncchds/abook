using ABook.Core.Interfaces;
using ABook.Core.Models;
using ModelContextProtocol;
using System.Security.Claims;

namespace ABook.Api.Mcp;

/// <summary>
/// Shared helpers for all MCP tool classes. Extracts the boilerplate that every tool shares:
/// user identification from claims and per-book ownership verification.
/// </summary>
public abstract class McpToolBase
{
    protected readonly IHttpContextAccessor Http;

    protected McpToolBase(IHttpContextAccessor http) => Http = http;

    /// <summary>Extracts the authenticated user's ID from the current HTTP context.</summary>
    protected int CurrentUserId() =>
        int.Parse(Http.HttpContext!.User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    /// <summary>
    /// Verifies that the current user owns (or has unrestricted access to) a book.
    /// Throws an <see cref="McpException"/> if the book doesn't exist or the user is not the owner.
    /// </summary>
    protected async Task<Book> GetOwnedBookAsync(int bookId, IBookRepository repo)
    {
        var userId = CurrentUserId();
        var book = await repo.GetByIdAsync(bookId);
        if (book is null || book.UserId != userId)
            throw new McpException($"Book {bookId} not found.");
        return book;
    }

    /// <summary>Same as <see cref="GetOwnedBookAsync"/>, but throws a more specific message.</summary>
    protected async Task EnsureBookOwnershipAsync(int bookId, IBookRepository repo)
    {
        var userId = CurrentUserId();
        var book = await repo.GetByIdAsync(bookId);
        if (book is null || book.UserId != userId)
            throw new McpException($"Book {bookId} not found.");
    }
}
