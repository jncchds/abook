using ABook.Agents;

namespace ABook.Tests;

public class PartialJsonTests
{
    [Fact]
    public void Salvage_CompleteArray_ReturnedUnchanged()
    {
        var raw = """[{"name":"Ana"},{"name":"Bo"}]""";
        Assert.Equal(raw, PartialJson.SalvageArray(raw));
        Assert.True(PartialJson.IsComplete(raw));
    }

    [Fact]
    public void Salvage_TruncatedMidElement_KeepsCompletedElements()
    {
        var raw = """[{"name":"Ana","arc":"grows"},{"name":"Bo","arc":"fal""";
        var json = PartialJson.SalvageArray(raw);

        using var doc = System.Text.Json.JsonDocument.Parse(json);
        Assert.Equal(1, doc.RootElement.GetArrayLength());
        Assert.Equal("Ana", doc.RootElement[0].GetProperty("name").GetString());
        Assert.False(PartialJson.IsComplete(raw));
    }

    [Fact]
    public void Salvage_TruncatedAfterComma_KeepsCompletedElements()
    {
        var raw = """[{"name":"Ana"},{"name":"Bo"},""";
        using var doc = System.Text.Json.JsonDocument.Parse(PartialJson.SalvageArray(raw));
        Assert.Equal(2, doc.RootElement.GetArrayLength());
    }

    [Fact]
    public void Salvage_BracketsInsideStrings_DoNotConfuseTheScanner()
    {
        var raw = """[{"name":"Ana","notes":"uses [brackets] and {braces}"},{"name":"Bo""";
        using var doc = System.Text.Json.JsonDocument.Parse(PartialJson.SalvageArray(raw));
        Assert.Equal(1, doc.RootElement.GetArrayLength());
        Assert.Equal("uses [brackets] and {braces}", doc.RootElement[0].GetProperty("notes").GetString());
    }

    [Fact]
    public void Salvage_EscapedQuoteInsideString_Handled()
    {
        var raw = """[{"name":"Ana","notes":"she said \"go\" [now]"},{"name":"B""";
        using var doc = System.Text.Json.JsonDocument.Parse(PartialJson.SalvageArray(raw));
        Assert.Equal(1, doc.RootElement.GetArrayLength());
    }

    [Fact]
    public void Salvage_NestedArrays_CountAsPartOfTheirElement()
    {
        var raw = """[{"number":1,"charactersInvolved":["Ana","Bo"]},{"number":2,"charactersInvolved":["Ana""";
        using var doc = System.Text.Json.JsonDocument.Parse(PartialJson.SalvageArray(raw));
        Assert.Equal(1, doc.RootElement.GetArrayLength());
        Assert.Equal(2, doc.RootElement[0].GetProperty("charactersInvolved").GetArrayLength());
    }

    [Fact]
    public void Salvage_MarkdownFencedArray_Unwrapped()
    {
        var raw = "```json\n[{\"name\":\"Ana\"}]\n```";
        using var doc = System.Text.Json.JsonDocument.Parse(PartialJson.SalvageArray(raw));
        Assert.Equal(1, doc.RootElement.GetArrayLength());
    }

    [Fact]
    public void Salvage_ClosedThinkingBlock_Ignored()
    {
        var raw = """<think>I will list [two] characters</think>[{"name":"Ana"}]""";
        using var doc = System.Text.Json.JsonDocument.Parse(PartialJson.SalvageArray(raw));
        Assert.Equal(1, doc.RootElement.GetArrayLength());
        Assert.Equal("Ana", doc.RootElement[0].GetProperty("name").GetString());
    }

    [Fact]
    public void Salvage_UnterminatedThinkingBlock_YieldsNothing()
    {
        var raw = """<think>let me plan the [list] of characters""";
        Assert.Equal(string.Empty, PartialJson.SalvageArray(raw));
    }

    [Fact]
    public void Salvage_ProseBeforeArray_FindsTheArray()
    {
        var raw = """Sure — here is the [requested] list: [{"name":"Ana"}]""";
        using var doc = System.Text.Json.JsonDocument.Parse(PartialJson.SalvageArray(raw));
        Assert.Equal(1, doc.RootElement.GetArrayLength());
        Assert.Equal("Ana", doc.RootElement[0].GetProperty("name").GetString());
    }

    [Fact]
    public void Salvage_ArrayOfStrings_IsNotTreatedAsData()
    {
        // Bracketed prose must never be mistaken for the payload.
        Assert.Equal(string.Empty, PartialJson.SalvageArray("""["one","two"]"""));
    }

    [Fact]
    public void IsComplete_TruncatedArray_False()
    {
        Assert.False(PartialJson.IsComplete("""[{"name":"Ana"},{"name":"B"""));
        Assert.False(PartialJson.IsComplete("no json at all"));
    }

    [Fact]
    public void IsComplete_TrailingProseAfterArray_StillComplete()
    {
        Assert.True(PartialJson.IsComplete("""[{"name":"Ana"}]  Hope this helps!"""));
    }

    [Fact]
    public void Salvage_NoCompleteElement_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, PartialJson.SalvageArray("""[{"name":"An"""));
        Assert.Equal(string.Empty, PartialJson.SalvageArray("no json at all"));
        Assert.Equal(string.Empty, PartialJson.SalvageArray(""));
    }

    [Fact]
    public void Salvage_EmptyArray_IsCompleteButHasNoElements()
    {
        Assert.Equal("[]", PartialJson.SalvageArray("[]"));
        Assert.True(PartialJson.IsComplete("[]"));
    }
}
