using FormPotok.Ocr.Contracts;
using System.Text.Json.Serialization;

namespace FormPotok.PilotCore.LocalText;

public enum LocalTextStatus { Queued, Recognizing, NeedsReview, Reviewed, Failed }
public enum LocalTextFailure { None, RuntimeMissing, ModelsMissing, SourceLimit, PageLimit, PixelLimit, MemoryLimit, Timeout, InvalidSource, Unknown, ResourceGuard, OutputLimit, EmptyText }

public sealed class LocalTextSourceLimitException() : IOException("Source exceeds the local text size limit.");
public sealed record LocalTextReviewMark(
    [property: JsonRequired][property: JsonNumberHandling(JsonNumberHandling.Strict)] int PageIndex,
    [property: JsonRequired][property: JsonNumberHandling(JsonNumberHandling.Strict)] int BlockIndex);

public sealed record LocalTextDocument(
    Guid Id,
    string DisplayName,
    string SourceHash,
    string SourceFile,
    LocalTextStatus Status = LocalTextStatus.Queued,
    ExtractionEnvelope? Extraction = null,
    string OriginalText = "",
    string EditedText = "",
    LocalTextFailure Failure = LocalTextFailure.None)
{
    private IReadOnlyList<LocalTextReviewMark>? _reviewedBlocks;

    public IReadOnlyList<LocalTextReviewMark>? ReviewedBlocks
    {
        get => _reviewedBlocks;
        init => _reviewedBlocks = value is null ? null : Array.AsReadOnly(value.ToArray());
    }

    public LocalTextDocument WithExtraction(ExtractionEnvelope extraction)
    {
        ArgumentNullException.ThrowIfNull(extraction);
        if (extraction.Pages is null || extraction.Pages.Count == 0 || extraction.Artifacts is null
            || extraction.Warnings is null || extraction.Candidates is null || extraction.Candidates.Count != 0
            || extraction.Artifacts.Any(artifact => artifact is null)
            || extraction.Pages.Any(page => page is null || page.Width <= 0 || page.Height <= 0
                || page.TextBlocks is null || page.TableBlocks is null
                || page.TextBlocks.Any(block => block is null || block.Text is null || block.Polygon is null
                    || !double.IsFinite(block.Confidence) || block.Confidence is < 0 or > 1
                    || block.Polygon.Any(point => point is null || !double.IsFinite(point.X) || !double.IsFinite(point.Y))))
            || !extraction.Pages.Select(page => page.PageNumber).Order().SequenceEqual(Enumerable.Range(1, extraction.Pages.Count)))
            throw new InvalidDataException("Extraction pages are invalid.");

        var text = string.Join("\n\n", extraction.Pages.OrderBy(page => page.PageNumber)
            .Select(page => string.Join("\n", page.TextBlocks.Select(block => block.Text))));
        var empty = string.IsNullOrWhiteSpace(text);
        return this with { Extraction = extraction, OriginalText = text, EditedText = text, ReviewedBlocks = null,
            Status = empty ? LocalTextStatus.Failed : LocalTextStatus.NeedsReview,
            Failure = empty ? LocalTextFailure.EmptyText : LocalTextFailure.None };
    }

    public bool IsBlockReviewed(int pageIndex, int blockIndex)
    {
        ValidateBlockIndex(pageIndex, blockIndex);
        return ReviewedBlocks?.Any(mark => mark.PageIndex == pageIndex && mark.BlockIndex == blockIndex) == true;
    }

    public LocalTextDocument WithBlockReview(int pageIndex, int blockIndex, bool reviewed)
    {
        var alreadyReviewed = IsBlockReviewed(pageIndex, blockIndex);
        if (alreadyReviewed == reviewed) return this;
        var marks = ReviewedBlocks?.ToList() ?? [];
        if (reviewed) marks.Add(new LocalTextReviewMark(pageIndex, blockIndex));
        else marks.RemoveAll(mark => mark.PageIndex == pageIndex && mark.BlockIndex == blockIndex);
        return this with { ReviewedBlocks = marks };
    }

    public int RemainingUncertainCount()
    {
        if (Extraction is null) return 0;
        return Extraction.Pages.SelectMany((page, pageIndex) => page.TextBlocks
            .Select((block, blockIndex) => (block, pageIndex, blockIndex)))
            .Count(item => item.block.Confidence < 0.75 && !IsBlockReviewed(item.pageIndex, item.blockIndex));
    }

    internal void ValidateReviewedBlocks()
    {
        if (ReviewedBlocks is null) return;
        if (Extraction is null) throw new InvalidDataException("Review marks require an extraction.");
        var seen = new HashSet<(int PageIndex, int BlockIndex)>();
        foreach (var mark in ReviewedBlocks)
        {
            if (mark is null || mark.PageIndex < 0 || mark.PageIndex >= Extraction.Pages.Count
                || mark.BlockIndex < 0 || mark.BlockIndex >= Extraction.Pages[mark.PageIndex].TextBlocks.Count
                || !seen.Add((mark.PageIndex, mark.BlockIndex)))
                throw new InvalidDataException("Review mark is invalid.");
        }
    }

    private void ValidateBlockIndex(int pageIndex, int blockIndex)
    {
        if (Extraction is null) throw new InvalidOperationException("Review marks require an extraction.");
        if (pageIndex < 0 || pageIndex >= Extraction.Pages.Count) throw new ArgumentOutOfRangeException(nameof(pageIndex));
        if (blockIndex < 0 || blockIndex >= Extraction.Pages[pageIndex].TextBlocks.Count)
            throw new ArgumentOutOfRangeException(nameof(blockIndex));
    }

    public LocalTextDocument WithEdit(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text == EditedText) return this;
        var empty = string.IsNullOrWhiteSpace(text) && string.IsNullOrWhiteSpace(OriginalText);
        return this with { EditedText = text, Status = empty ? LocalTextStatus.Failed : LocalTextStatus.NeedsReview,
            Failure = empty ? LocalTextFailure.EmptyText : LocalTextFailure.None };
    }

    public LocalTextDocument Approve()
    {
        if (Extraction is null || string.IsNullOrWhiteSpace(EditedText) || Status is LocalTextStatus.Queued or LocalTextStatus.Recognizing or LocalTextStatus.Failed)
            throw new InvalidOperationException("Recognition and nonempty reviewed text are required.");
        return this with { Status = LocalTextStatus.Reviewed };
    }
}
