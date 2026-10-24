using ABook.Core.Models;

namespace ABook.Core.Interfaces;

/// <summary>Abstraction for pushing real-time notifications to connected UI clients.</summary>
public interface IBookNotifier
{
    Task StreamTokenAsync(int bookId, int? chapterId, AgentRole agentRole, string token, CancellationToken ct = default);
    Task NotifyQuestionAsync(int bookId, AgentMessage message, CancellationToken ct = default);
    Task NotifyStatusChangedAsync(int bookId, AgentRole role, string state, int? chapterId = null, CancellationToken ct = default);
    Task NotifyChapterUpdatedAsync(int bookId, int chapterId, CancellationToken ct = default);
    Task NotifyWorkflowProgressAsync(int bookId, string step, bool isComplete, CancellationToken ct = default);
    Task NotifyTokenStatsAsync(int bookId, int? chapterId, string agentRole, int promptTokens, int completionTokens, CancellationToken ct = default);
    Task NotifyAgentErrorAsync(int bookId, string agentRole, string errorMessage, CancellationToken ct = default);
    Task NotifyMessagesUpdatedAsync(int bookId, CancellationToken ct = default);
    /// <summary>An illustration was planned, edited or rendered; null <paramref name="illustrationId"/> means several changed.</summary>
    Task NotifyIllustrationsUpdatedAsync(int bookId, int? illustrationId, CancellationToken ct = default);
    /// <summary>
    /// The book chat changed: a message was added or a proposal's state changed (<paramref name="messageId"/>
    /// names the message, null means several). The UI re-reads the chat; the book itself may have changed too.
    /// </summary>
    Task NotifyBookChatAsync(int bookId, int? messageId, CancellationToken ct = default);
    /// <summary>
    /// The book was rewound to <paramref name="at"/>: everything may have changed (the UI re-reads the book), and the
    /// book-chat messages <paramref name="hiddenChatMessageIds"/> are hidden from now on.
    /// </summary>
    Task NotifyRewoundAsync(int bookId, DateTime at, IReadOnlyList<int> hiddenChatMessageIds, string? origin, CancellationToken ct = default);
}
