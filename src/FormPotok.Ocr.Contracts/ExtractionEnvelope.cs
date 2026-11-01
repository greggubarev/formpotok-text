using System.Text.Json.Serialization;

namespace FormPotok.Ocr.Contracts;

public sealed record ExtractionEnvelope(
    string DocumentId,
    string EngineName,
    string EngineVersion,
    IReadOnlyList<ExtractionPage> Pages,
    IReadOnlyList<ExtractionWarning> Warnings,
    IReadOnlyList<ExtractionFieldCandidate> Candidates,
    IReadOnlyList<string> Artifacts);

public sealed record ExtractionPage(
    int PageNumber,
    int Width,
    int Height,
    IReadOnlyList<ExtractionTextBlock> TextBlocks,
    IReadOnlyList<ExtractionTableBlock> TableBlocks,
    string? CoordinateFrame = null,
    string? SelectedVariant = null);

public sealed record ExtractionTextBlock(
    string Id,
    string Text,
    double Confidence,
    IReadOnlyList<Point2D> Polygon);

public sealed record ExtractionTableBlock(
    string Id,
    IReadOnlyList<ExtractionTableCell> Cells);

public sealed record ExtractionTableCell(
    int RowIndex,
    int ColumnIndex,
    string Text,
    double Confidence);

public sealed record ExtractionFieldCandidate(
    string FieldKey,
    string RawValue,
    string? NormalizedValue,
    double? Confidence,
    int? PageNumber,
    string? Snippet,
    string SourceType,
    bool RequiresReview,
    [property: JsonPropertyName("bbox_normalized")]
    NormalizedBoundingBox? BoundingBox = null,
    [property: JsonPropertyName("bbox_coordinate_frame")]
    string? BoundingBoxCoordinateFrame = null);

public sealed record ExtractionWarning(
    string Code,
    string Message);

public sealed record OcrRuntimeHealth(
    bool IsHealthy,
    string EngineName,
    string EngineVersion,
    string PythonVersion,
    string Device,
    string? Details);

public sealed record Point2D(double X, double Y);

public sealed record NormalizedBoundingBox(double X, double Y, double Width, double Height);
