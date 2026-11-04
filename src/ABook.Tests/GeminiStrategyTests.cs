using ABook.Core.Models;
using ABook.Infrastructure.Llm.Strategies;
using G = ABook.Infrastructure.Llm.Strategies.GoogleAIStudioProviderStrategy;

namespace ABook.Tests;

/// <summary>Google AI Studio through Gemini's own API: the request it sends and how it reads the stream.</summary>
public class GeminiStrategyTests
{
    [Theory]
    [InlineData(null, "https://generativelanguage.googleapis.com/v1beta")]
    [InlineData("https://generativelanguage.googleapis.com/v1beta/openai/", "https://generativelanguage.googleapis.com/v1beta")]
    [InlineData("https://proxy.example.com/gemini", "https://proxy.example.com/gemini")]
    public void BaseUrl_DropsTheOpenAICompatiblePath(string? endpoint, string expected) =>
        Assert.Equal(expected, G.BaseUrl(endpoint));

    [Fact]
    public void Request_PutsSystemAside_NamesTheModelTurn_AndAsksForJsonByTheSchema()
    {
        var body = G.BuildRequest(
            [
                new(LlmChatRole.System, "Be brief."),
                new(LlmChatRole.User, "Hi"),
                new(LlmChatRole.Assistant, "Hello"),
                new(LlmChatRole.User, "Plan it"),
            ],
            new LlmChatOptions { Temperature = 0.7f, MaxTokens = 500, JsonSchema = """{"type":"object"}""", ReasoningEffort = "low" },
            "models/gemini-2.5-flash");

        Assert.Equal("Be brief.", (string?)body["systemInstruction"]!["parts"]![0]!["text"]);
        Assert.Equal(["user", "model", "user"], body["contents"]!.AsArray().Select(c => (string?)c!["role"]));
        var config = body["generationConfig"]!;
        Assert.Equal(500, (int)config["maxOutputTokens"]!);
        Assert.Equal("application/json", (string?)config["responseMimeType"]);
        Assert.Equal("object", (string?)config["responseJsonSchema"]!["type"]);
        Assert.Equal(1024, (int)config["thinkingConfig"]!["thinkingBudget"]!);
        Assert.True((bool)config["thinkingConfig"]!["includeThoughts"]!);
    }

    [Fact]
    public void Request_WithoutTheSchemaOrThinking_StillAsksForJson_AndOlderModelsGetNoThinking()
    {
        var options = new LlmChatOptions { JsonSchema = """{"type":"object"}""" };
        var fallback = G.BuildRequest([new(LlmChatRole.User, "x")], options, "gemini-2.5-pro", thinking: false, schema: false)["generationConfig"]!;
        Assert.Equal("application/json", (string?)fallback["responseMimeType"]);
        Assert.Null(fallback["responseJsonSchema"]);
        Assert.Null(fallback["thinkingConfig"]);
        Assert.Null(G.BuildRequest([new(LlmChatRole.User, "x")], options, "gemini-2.0-flash")["generationConfig"]!["thinkingConfig"]);

        // A system-only request goes as the user's turn.
        var systemOnly = G.BuildRequest([new(LlmChatRole.System, "Only this")], new LlmChatOptions(), "gemini-2.0-flash");
        Assert.Null(systemOnly["systemInstruction"]);
        Assert.Equal("Only this", (string?)systemOnly["contents"]![0]!["parts"]![0]!["text"]);
    }

    [Theory]
    [InlineData("gemini-2.5-flash", null, null)]
    [InlineData("gemini-2.5-flash", "none", 0)]
    [InlineData("gemini-2.5-pro", "none", 128)]   // Pro models cannot turn thinking off
    [InlineData("gemini-3-flash", "high", 24576)]
    public void ThinkingBudget_FollowsReasoningEffort(string model, string? effort, int? budget) =>
        Assert.Equal(budget, G.ThinkingBudget(model, effort));

    [Fact]
    public void Event_SplitsThoughtsFromText()
    {
        var (chunks, stopped) = G.ParseEvent("""
            {"candidates":[{"content":{"role":"model","parts":[{"text":"Weighing it","thought":true},{"text":"Answer"}]}}]}
            """);
        Assert.Null(stopped);
        Assert.Equal(("Weighing it", null), (chunks[0].Reasoning, chunks[0].Content));
        Assert.Equal("Answer", chunks[1].Content);
    }

    [Theory]
    [InlineData("""{"candidates":[{"content":{"parts":[{"text":"Last"}]},"finishReason":"STOP"}]}""", null)]
    [InlineData("""{"candidates":[{"finishReason":"MAX_TOKENS"}]}""", null)]
    [InlineData("""{"candidates":[{"finishReason":"SAFETY"}]}""", "Gemini stopped the reply (SAFETY).")]
    [InlineData("""{"promptFeedback":{"blockReason":"PROHIBITED_CONTENT"}}""", "Gemini blocked the request (PROHIBITED_CONTENT).")]
    public void Event_SaysWhyAReplyStopped(string json, string? expected) =>
        Assert.Equal(expected, G.ParseEvent(json).Stopped);

    [Fact]
    public void Busy_IsTriedAgain_AfterGooglesWaitOrAGrowingOne()
    {
        Assert.True(G.IsBusy(System.Net.HttpStatusCode.ServiceUnavailable));
        Assert.True(G.IsBusy(System.Net.HttpStatusCode.TooManyRequests));
        Assert.False(G.IsBusy(System.Net.HttpStatusCode.BadRequest));
        Assert.Equal(TimeSpan.FromSeconds(4), G.RetryDelay(2, null));
        Assert.Equal(TimeSpan.FromSeconds(7), G.RetryDelay(1, TimeSpan.FromSeconds(7)));
        Assert.Equal(TimeSpan.FromMinutes(1), G.RetryDelay(1, TimeSpan.FromHours(1)));
    }

    [Fact]
    public void ErrorMessage_ReadsGooglesError() =>
        Assert.Equal("API key not valid.", G.ErrorMessage("""{"error":{"code":400,"message":"API key not valid.","status":"INVALID_ARGUMENT"}}"""));
}
