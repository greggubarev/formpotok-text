param(
    [Parameter(Mandatory = $true)][string]$SourceImage,
    [string]$OutputDirectory = 'apps/FormPotok.App/Assets'
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$sourcePath = (Resolve-Path -LiteralPath $SourceImage).Path
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$targetRoot = if ([IO.Path]::IsPathRooted($OutputDirectory)) {
    [IO.Path]::GetFullPath($OutputDirectory)
} else {
    [IO.Path]::GetFullPath((Join-Path $repoRoot $OutputDirectory))
}
[IO.Directory]::CreateDirectory($targetRoot) | Out-Null
$pngPath = Join-Path $targetRoot 'formpotok-mark.png'
$iconPath = Join-Path $targetRoot 'formpotok-mark.ico'

$sourceStream = [IO.MemoryStream]::new([IO.File]::ReadAllBytes($sourcePath))
$source = [System.Drawing.Image]::FromStream($sourceStream)
try {
    $sizes = @(16, 32, 48, 256, 512)
    $images = @{}
    foreach ($size in $sizes) {
        $bitmap = [System.Drawing.Bitmap]::new($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
        try {
            $graphics.Clear([System.Drawing.Color]::Transparent)
            $graphics.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceOver
            $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
            $graphics.DrawImage($source, [System.Drawing.Rectangle]::new(0, 0, $size, $size))
        }
        finally { $graphics.Dispose() }
        $stream = [IO.MemoryStream]::new()
        try {
            $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
            $images[$size] = $stream.ToArray()
        }
        finally { $stream.Dispose(); $bitmap.Dispose() }
    }
    [IO.File]::WriteAllBytes($pngPath, $images[512])

    $iconSizes = @(16, 32, 48, 256)
    $stream = [IO.File]::Create($iconPath)
    $writer = [IO.BinaryWriter]::new($stream)
    try {
        $writer.Write([uint16]0)
        $writer.Write([uint16]1)
        $writer.Write([uint16]$iconSizes.Count)
        $offset = 6 + 16 * $iconSizes.Count
        foreach ($size in $iconSizes) {
            $writer.Write([byte]($size % 256))
            $writer.Write([byte]($size % 256))
            $writer.Write([byte]0)
            $writer.Write([byte]0)
            $writer.Write([uint16]1)
            $writer.Write([uint16]32)
            $writer.Write([uint32]$images[$size].Length)
            $writer.Write([uint32]$offset)
            $offset += $images[$size].Length
        }
        foreach ($size in $iconSizes) { $writer.Write([byte[]]$images[$size]) }
    }
    finally { $writer.Dispose() }
}
finally { $source.Dispose(); $sourceStream.Dispose() }

[pscustomobject]@{ png = $pngPath; icon = $iconPath }
