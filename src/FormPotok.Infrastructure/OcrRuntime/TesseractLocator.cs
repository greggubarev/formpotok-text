namespace FormPotok.Infrastructure.OcrRuntime;

public static class TesseractLocator
{
    private const string DefaultInstallPath = @"C:\Program Files\Tesseract-OCR\tesseract.exe";
    private const string DefaultTessdataPath = @"C:\Program Files\Tesseract-OCR\tessdata";

    public static string ResolveDefaultPath() => File.Exists(DefaultInstallPath) ? DefaultInstallPath : string.Empty;

    public static string ResolveDefaultTessdataDirectory() => Directory.Exists(DefaultTessdataPath) ? DefaultTessdataPath : string.Empty;
}
