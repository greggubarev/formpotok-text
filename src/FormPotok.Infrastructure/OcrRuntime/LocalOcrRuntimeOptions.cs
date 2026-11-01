namespace FormPotok.Infrastructure.OcrRuntime;

public sealed class LocalOcrRuntimeOptions
{
    public string? SidecarExecutablePath { get; init; }

    public bool AutoBootstrapPythonEnvironment { get; init; }

    public bool TextOnly { get; init; }

    public bool OfflineOnly { get; init; }

    public bool DisableTextDocumentUnwarping { get; init; }

    public bool RetryTextWithoutUnwarpingOnMemoryLimit { get; init; }

    public bool ForceTextOriginalVariant { get; init; }

    public bool TileTextPage { get; init; }

    public string? PaddleModelCacheHome { get; init; }

    public int MaximumPages { get; init; } = 100;

    public long MaximumPagePixels { get; init; } = 20_000_000;

    // Windows caps committed memory for the owned job (including descendants), not RSS.
    public long? MaximumProcessMemoryBytes { get; init; }

    public string PythonExecutablePath { get; init; } = string.Empty;

    public string SidecarScriptPath { get; init; } = string.Empty;

    public string WorkingDirectory { get; init; } = string.Empty;

    public string OutputDirectory { get; init; } = string.Empty;

    public string PreferredEngine { get; init; } = "auto";

    public string PaddleProfile { get; init; } = "mobile-cpu";

    public string? PaddleDetectionModelDirectory { get; init; }

    public string? PaddleRecognitionModelDirectory { get; init; }

    public string? PaddleRecognitionModelName { get; init; }

    public string? TesseractExecutablePath { get; init; }

    public string? TessdataDirectory { get; init; }

    public string TesseractLanguages { get; init; } = "rus+eng";

    public TimeSpan HealthcheckTimeout { get; init; } = TimeSpan.FromSeconds(30);

    public TimeSpan ExtractTimeout { get; init; } = TimeSpan.FromMinutes(15);

    public TimeSpan BootstrapTimeout { get; init; } = TimeSpan.FromMinutes(30);
}
