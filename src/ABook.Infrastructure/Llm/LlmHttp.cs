namespace ABook.Infrastructure.Llm;

/// <summary>
/// HTTP clients for the hand-rolled LLM providers. All share one connection pool, so a long workflow
/// making hundreds of calls neither leaks sockets in TIME_WAIT (as a new handler per call does) nor
/// pins stale DNS (connections are recycled every few minutes). The clients themselves are cheap and
/// carry the per-call timeout.
/// </summary>
internal static class LlmHttp
{
    private static readonly SocketsHttpHandler SharedHandler = new()
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    };

    /// <summary>A client on the shared pool. Disposing it leaves the pool intact.</summary>
    internal static HttpClient CreateClient(TimeSpan timeout, Uri? baseAddress = null) =>
        new(SharedHandler, disposeHandler: false) { Timeout = timeout, BaseAddress = baseAddress };
}
