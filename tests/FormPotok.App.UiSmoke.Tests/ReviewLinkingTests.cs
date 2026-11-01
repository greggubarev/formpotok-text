using Avalonia;
using FormPotok.Ocr.Contracts;
using FormPotok.PilotCore.LocalText;
using Xunit;

namespace FormPotok.App.UiSmoke.Tests;

public sealed class ReviewLinkingTests
{
    [Fact]
    public void OriginalText_MapsRepeatedLinesToTheirOwnPages()
    {
        var document = Document("Alpha", "Echo", "Echo");
        Assert.True(ReviewLinking.TryGetTextSpan(document, 0, 1, out var first, out var length));
        Assert.True(ReviewLinking.TryGetTextSpan(document, 1, 0, out var second, out _));
        Assert.Equal("Echo", document.EditedText.Substring(first, length));
        Assert.Equal("Echo", document.EditedText.Substring(second, length));
        Assert.True(second > first);
    }

    [Fact]
    public void EditedText_OnlyMapsAnUnambiguousOccurrence()
    {
        var document = Document("Alpha", "Echo", "Other").WithEdit("Preface\nEcho\nOther");
        Assert.True(ReviewLinking.TryGetTextSpan(document, 0, 1, out var start, out var length));
        Assert.Equal("Echo", document.EditedText.Substring(start, length));
        Assert.False(ReviewLinking.TryGetTextSpan(document.WithEdit("Echo\nEcho"), 0, 1, out _, out _));
    }

    [Fact]
    public void Highlight_AccountsForUniformImageLetterboxing()
    {
        var page = Page(1, "original-pixels", Block("line", "Text", 20, 10, 80, 40));
        var rect = ReviewLinking.HighlightRect(page, page.TextBlocks[0], new Size(200, 100), new Size(300, 300));
        Assert.Equal(new Rect(30, 90, 90, 45), rect);
    }

    [Fact]
    public void Highlight_RejectsEstimatedAndMismatchedGeometry()
    {
        var block = Block("text-layer-0001", "Text", 20, 10, 80, 40);
        Assert.Null(ReviewLinking.HighlightRect(Page(1, null, block), block, new Size(200, 100), new Size(300, 300)));
        var page = Page(1, "original-pixels", block);
        Assert.Null(ReviewLinking.HighlightRect(page, block, new Size(100, 200), new Size(300, 300)));
        Assert.Null(ReviewLinking.HighlightRect(page, Block("bad", "Text", -1, 10, 80, 40),
            new Size(200, 100), new Size(300, 300)));
    }

    private static LocalTextDocument Document(string first, string second, string third)
    {
        var pages = new[]
        {
            Page(1, "original-pixels", Block("one", first, 0, 0, 10, 10), Block("two", second, 0, 10, 10, 20)),
            Page(2, "original-pixels", Block("three", third, 0, 0, 10, 10)),
        };
        return new LocalTextDocument(Guid.NewGuid(), "sample", "hash", "source")
            .WithExtraction(new ExtractionEnvelope("sample", "test", "1", pages, [], [], []));
    }

    private static ExtractionPage Page(int number, string? frame, params ExtractionTextBlock[] blocks) =>
        new(number, 200, 100, blocks, [], CoordinateFrame: frame);

    private static ExtractionTextBlock Block(string id, string text, double left, double top, double right, double bottom) =>
        new(id, text, 0.9, [new Point2D(left, top), new Point2D(right, top), new Point2D(right, bottom), new Point2D(left, bottom)]);
}
