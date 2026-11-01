using Avalonia;
using FormPotok.Ocr.Contracts;
using FormPotok.PilotCore.LocalText;

namespace FormPotok.App;

internal static class ReviewLinking
{
    public static bool TryGetTextSpan(LocalTextDocument document, int pageIndex, int blockIndex, out int start, out int length)
    {
        start = 0;
        length = 0;
        var pages = document.Extraction?.Pages;
        if (pages is null || pageIndex < 0 || pageIndex >= pages.Count
            || blockIndex < 0 || blockIndex >= pages[pageIndex].TextBlocks.Count)
            return false;

        var text = pages[pageIndex].TextBlocks[blockIndex].Text;
        if (string.IsNullOrEmpty(text)) return false;
        length = text.Length;
        if (document.EditedText == document.OriginalText)
        {
            for (var page = 0; page < pageIndex; page++)
                start += pages[page].TextBlocks.Sum(block => block.Text.Length) + Math.Max(0, pages[page].TextBlocks.Count - 1) + 2;
            for (var block = 0; block < blockIndex; block++)
                start += pages[pageIndex].TextBlocks[block].Text.Length + 1;
            return start + length <= document.EditedText.Length
                && document.EditedText.AsSpan(start, length).SequenceEqual(text.AsSpan());
        }

        start = document.EditedText.IndexOf(text, StringComparison.Ordinal);
        return start >= 0 && document.EditedText.IndexOf(text, start + 1, StringComparison.Ordinal) < 0;
    }

    public static Rect? HighlightRect(ExtractionPage page, ExtractionTextBlock block, Size imagePixels, Size viewport)
    {
        // Text-layer PDF polygons are currently estimated from line order, not extracted from PDF glyph positions.
        if (page.CoordinateFrame != "original-pixels" && !block.Id.StartsWith("tesseract-", StringComparison.Ordinal))
            return null;
        if (page.Width <= 0 || page.Height <= 0 || imagePixels.Width <= 0 || imagePixels.Height <= 0
            || viewport.Width <= 0 || viewport.Height <= 0 || block.Polygon.Count < 3)
            return null;
        var pageRatio = (double)page.Width / page.Height;
        var imageRatio = imagePixels.Width / imagePixels.Height;
        if (Math.Abs(pageRatio - imageRatio) / pageRatio > 0.02) return null;

        var left = block.Polygon.Min(point => point.X);
        var top = block.Polygon.Min(point => point.Y);
        var right = block.Polygon.Max(point => point.X);
        var bottom = block.Polygon.Max(point => point.Y);
        if (left < 0 || top < 0 || right > page.Width || bottom > page.Height || left >= right || top >= bottom)
            return null;

        var scale = Math.Min(viewport.Width / imagePixels.Width, viewport.Height / imagePixels.Height);
        var displayWidth = imagePixels.Width * scale;
        var displayHeight = imagePixels.Height * scale;
        var offsetX = (viewport.Width - displayWidth) / 2;
        var offsetY = (viewport.Height - displayHeight) / 2;
        return new Rect(
            offsetX + left / page.Width * displayWidth,
            offsetY + top / page.Height * displayHeight,
            (right - left) / page.Width * displayWidth,
            (bottom - top) / page.Height * displayHeight);
    }
}
