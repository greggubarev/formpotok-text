using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace FormPotok.App;

internal static class BrandAssets
{
    private static readonly string MarkPath = Path.Combine(AppContext.BaseDirectory, "Assets", "formpotok-mark.png");

    public static WindowIcon CreateWindowIcon() => new(MarkPath);

    public static Image CreateMark(double size) => new()
    {
        Source = new Bitmap(MarkPath),
        Width = size,
        Height = size,
        Stretch = Stretch.Uniform,
    };
}
