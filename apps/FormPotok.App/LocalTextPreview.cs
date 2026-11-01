using Avalonia.Media.Imaging;
using SkiaSharp;

namespace FormPotok.App;

internal static class LocalTextPreview
{
    internal static bool IsWithinBudget(int width, int height) =>
        width > 0 && height > 0 && (long)width * height <= 20_000_000;

    public static (Bitmap Image, Avalonia.PixelSize SourceSize) Decode(string path)
    {
        using var stream = File.OpenRead(path);
        int width, height;
        // Inspect the encoded header before allocating the decoded image.
        using (var codec = SKCodec.Create(stream))
        {
            if (codec is null || !IsWithinBudget(codec.Info.Width, codec.Info.Height))
                throw new InvalidDataException("Preview dimensions exceed the local budget.");
            width = codec.Info.Width;
            height = codec.Info.Height;
        }
        using var input = File.OpenRead(path);
        var image = width >= height ? Bitmap.DecodeToWidth(input, Math.Min(width, 1600))
            : Bitmap.DecodeToHeight(input, Math.Min(height, 1600));
        return (image, new Avalonia.PixelSize(width, height));
    }
}
