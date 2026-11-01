using FormPotok.Infrastructure.OcrRuntime;

namespace FormPotok.App;

internal static class LocalTextRuntime
{
    public static string ResolveStorageRoot() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FormPotok", "Text");

    public static LocalOcrRuntimeOptions CreateOptions(string storageRoot)
    {
        var root = File.Exists(Path.Combine(AppContext.BaseDirectory, "tools", "ocr-sidecar", "main.py"))
            ? AppContext.BaseDirectory
            : IsDevelopmentRoot(Environment.CurrentDirectory) ? Environment.CurrentDirectory : AppContext.BaseDirectory;
        return CreateOptions(storageRoot, root);
    }

    internal static LocalOcrRuntimeOptions CreateOptions(string storageRoot, string runtimeRoot)
    {
        var root = Path.GetFullPath(runtimeRoot);
        var sidecar = Path.Combine(root, "tools", "ocr-sidecar");
        var executable = Path.Combine(sidecar, "runtime", "formpotok-ocr-sidecar.exe");
        var python = Path.Combine(root, "tools", "python310", "python.exe");
        var modelCache = Path.Combine(sidecar, "models", "paddlex");
        if (IsDevelopmentRoot(root))
        {
            var developmentPython = Path.Combine(sidecar, ".venv", "Scripts", "python.exe");
            if (!File.Exists(python) && File.Exists(developmentPython)) python = developmentPython;
            var developmentCache = Path.Combine(root, "output", "ocr-bootstrap-cache", "win-x64", "models", "paddlex");
            if (!Directory.Exists(modelCache) && Directory.Exists(developmentCache)) modelCache = developmentCache;
        }
        return new LocalOcrRuntimeOptions
        {
            SidecarExecutablePath = File.Exists(executable) ? executable : null,
            PythonExecutablePath = python,
            SidecarScriptPath = Path.Combine(sidecar, "main.py"),
            WorkingDirectory = sidecar,
            OutputDirectory = Path.Combine(storageRoot, "ocr-runs"),
            AutoBootstrapPythonEnvironment = false,
            TextOnly = true,
            OfflineOnly = true,
            RetryTextWithoutUnwarpingOnMemoryLimit = OperatingSystem.IsWindows(),
            PreferredEngine = "paddle",
            PaddleModelCacheHome = modelCache,
            PaddleDetectionModelDirectory = Path.Combine(modelCache, "official_models", "PP-OCRv5_mobile_det"),
            PaddleRecognitionModelDirectory = Path.Combine(modelCache, "official_models", "eslav_PP-OCRv5_mobile_rec"),
            PaddleRecognitionModelName = "eslav_PP-OCRv5_mobile_rec",
            MaximumPages = 20,
            MaximumPagePixels = 20_000_000,
            MaximumProcessMemoryBytes = 2L * 1024 * 1024 * 1024,
            ExtractTimeout = TimeSpan.FromSeconds(300),
            TessdataDirectory = Path.Combine(sidecar, "tessdata"),
            TesseractLanguages = "rus+eng",
        };
    }

    private static bool IsDevelopmentRoot(string root) =>
        (Directory.Exists(Path.Combine(root, ".git")) || File.Exists(Path.Combine(root, ".git")))
        && File.Exists(Path.Combine(root, "apps", "FormPotok.App", "FormPotok.App.csproj"));
}
