param(
    [string]$Source = (Join-Path $PSScriptRoot '..\Assets\Gallery'),
    [string]$Destination = (Join-Path $PSScriptRoot '..\docs\gallery')
)

$ErrorActionPreference = 'Stop'
Add-Type -ReferencedAssemblies PresentationCore,PresentationFramework,WindowsBase,System.Xaml -TypeDefinition @'
using System;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

public static class GalleryPreview
{
    public static double Generate(string sourcePath, string outputPath, string watermark)
    {
        BitmapFrame frame;
        using (var stream = File.OpenRead(sourcePath))
        {
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            frame = decoder.Frames[0];
        }

        var scale = Math.Min(1d, 1600d / Math.Max(frame.PixelWidth, frame.PixelHeight));
        var width = Math.Max(1, (int)Math.Round(frame.PixelWidth * scale));
        var height = Math.Max(1, (int)Math.Round(frame.PixelHeight * scale));
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle(new SolidColorBrush(Color.FromRgb(20, 17, 28)), null, new Rect(0, 0, width, height));
            drawing.DrawImage(frame, new Rect(0, 0, width, height));

            var fontSize = Math.Max(12, Math.Min(26, width / 45));
            var text = new FormattedText(
                watermark,
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal),
                fontSize,
                new SolidColorBrush(Color.FromArgb(220, 255, 205, 240)),
                1d);
            var padding = Math.Max(10, (int)(fontSize * 0.8));
            var x = width - text.Width - padding;
            var y = height - text.Height - padding;
            drawing.DrawRectangle(
                new SolidColorBrush(Color.FromArgb(145, 16, 12, 24)),
                null,
                new Rect(x - 6, y - 3, text.Width + 12, text.Height + 6));
            drawing.DrawText(text, new Point(x, y));
        }

        var preview = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        preview.Render(visual);
        var encoder = new JpegBitmapEncoder { QualityLevel = 84 };
        encoder.Frames.Add(BitmapFrame.Create(preview));
        using (var stream = File.Create(outputPath))
            encoder.Save(stream);

        return (double)frame.PixelWidth / frame.PixelHeight;
    }
}
'@

$imageDirectory = Join-Path $Destination 'images'
New-Item -ItemType Directory -Force -Path $imageDirectory | Out-Null

$sourceRoot = (Resolve-Path -LiteralPath $Source).Path.TrimEnd('\') + '\'
$supportedExtensions = @('.png', '.jpg', '.jpeg', '.gif', '.bmp')
$manifest = foreach ($file in Get-ChildItem -LiteralPath $Source -Recurse -File | Sort-Object FullName) {
    if ($supportedExtensions -notcontains $file.Extension.ToLowerInvariant()) {
        continue
    }

    $relativePath = $file.FullName.Substring($sourceRoot.Length)
    $relativeDirectory = [IO.Path]::GetDirectoryName($relativePath)
    $category = if ([string]::IsNullOrWhiteSpace($relativeDirectory)) { 'Artwork' } else { $relativeDirectory.Split([IO.Path]::DirectorySeparatorChar)[0] }
    $name = [IO.Path]::GetFileNameWithoutExtension($file.Name)
    $match = [regex]::Match($name, '^(?<title>.+?)\s+-\s+@(?<artist>[^@]+)$')
    $title = if ($match.Success) { $match.Groups['title'].Value } else { $name }
    $artist = if ($match.Success) { $match.Groups['artist'].Value.Trim() } else { '' }
    $slug = (($category + '-' + $title + '-' + $artist).ToLowerInvariant() -replace '[^a-z0-9]+', '-').Trim('-')
    $outputName = "$slug.jpg"
    $outputPath = Join-Path $imageDirectory $outputName

    $watermark = if ($artist) { "Voxie preview | @$artist" } else { 'Voxie preview' }
    $aspectRatio = [GalleryPreview]::Generate($file.FullName, $outputPath, $watermark)

    [pscustomobject]@{
        title = $title
        artist = $artist
        category = $category
        imageUrl = "https://raw.githubusercontent.com/itz-lexi/Voxie/master/docs/gallery/images/$outputName"
        addedAt = $file.LastWriteTime.ToString('o')
        aspectRatio = [Math]::Round($aspectRatio, 5)
    }
}

$manifest | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath (Join-Path $Destination 'manifest.json')
Write-Host "Generated $($manifest.Count) hosted gallery previews."
