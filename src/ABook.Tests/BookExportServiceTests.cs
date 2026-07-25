using ABook.Api.Services;

namespace ABook.Tests;

public class BookExportServiceSafeFilenameTests
{
    [Fact]
    public void SafeFilename_CyrillicTitle_Transliterated()
    {
        var result = BookExportService.SafeFilename("Привет");
        Assert.DoesNotContain("П", result);
        Assert.False(string.IsNullOrWhiteSpace(result));
    }

    [Fact]
    public void SafeFilename_AsciiTitle_LowercasedAndHyphenated()
    {
        var result = BookExportService.SafeFilename("My Book Title");
        Assert.Equal("my-book-title", result);
    }

    [Fact]
    public void SafeFilename_NullInput_DoesNotThrow()
    {
        var ex = Record.Exception(() => BookExportService.SafeFilename(null!));
        Assert.Null(ex);
    }

    [Fact]
    public void SafeFilename_EmptyString_ReturnsFallback()
    {
        // Empty/whitespace-only input falls back to "book"
        var result = BookExportService.SafeFilename(string.Empty);
        Assert.Equal("book", result);
    }
}

public class BookExportServiceMarkdownTests
{
    [Fact]
    public void MarkdownToHtml_H2Heading_RendersH2Tag()
    {
        var result = BookExportService.MarkdownToHtml("## Hello World");
        Assert.Contains("<h2>", result);
        Assert.Contains("Hello World", result);
        Assert.Contains("</h2>", result);
    }

    [Fact]
    public void MarkdownToHtml_HorizontalRule_RendersHrTag()
    {
        var result = BookExportService.MarkdownToHtml("---");
        Assert.Contains("<hr>", result);
    }

    [Fact]
    public void MarkdownToHtml_PlainParagraph_WrappedInP()
    {
        var result = BookExportService.MarkdownToHtml("Hello world");
        Assert.Contains("<p>", result);
        Assert.Contains("Hello world", result);
        Assert.Contains("</p>", result);
    }

    [Fact]
    public void MarkdownToHtml_TwoParagraphs_BothWrapped()
    {
        var result = BookExportService.MarkdownToHtml("First paragraph\n\nSecond paragraph");
        Assert.Contains("<p>First paragraph</p>", result);
        Assert.Contains("<p>Second paragraph</p>", result);
    }

    [Fact]
    public void MarkdownToHtml_LessThanSign_EscapedToHtmlEntity()
    {
        var result = BookExportService.MarkdownToHtml("x < y");
        Assert.Contains("&lt;", result);
        Assert.DoesNotContain("x < y", result);
    }

    [Fact]
    public void InlineMd_Bold_RendersStrongTag()
    {
        var result = BookExportService.InlineMd("**bold text**");
        Assert.Contains("<strong>bold text</strong>", result);
    }

    [Fact]
    public void InlineMd_Italic_RendersEmTag()
    {
        var result = BookExportService.InlineMd("*italic text*");
        Assert.Contains("<em>italic text</em>", result);
    }

    [Fact]
    public void InlineMd_InlineCode_RendersCodeTag()
    {
        var result = BookExportService.InlineMd("`some code`");
        Assert.Contains("<code>some code</code>", result);
    }

    [Fact]
    public void InlineMd_BoldAndItalic_RendersBothTags()
    {
        var result = BookExportService.InlineMd("***bold italic***");
        Assert.Contains("<strong>", result);
        Assert.Contains("<em>", result);
    }

    [Fact]
    public void InlineMd_AmpersandEscaped()
    {
        var result = BookExportService.InlineMd("cats & dogs");
        Assert.Contains("&amp;", result);
    }
}
