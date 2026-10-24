namespace ABook.Agents;

public static class AgentCreatedBy
{
    public const string Writer = "agent:Writer";
    public const string Editor = "agent:Editor";
    public const string Characters = "agent:Characters";
    public const string PlotThreads = "agent:PlotThreads";
    /// <summary>A change the author approved in the book chat of an Interactive book.</summary>
    public const string BookChat = "agent:BookChat";
    /// <summary>An edit the owner made in the private chat with the Telegram bot (a card).</summary>
    public const string Telegram = "telegram";
}
