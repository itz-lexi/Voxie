using System.IO;
using System.Text.RegularExpressions;
using Voxie.Models;

namespace Voxie.Services;

public static class BundledGalleryService
{
    private static readonly Regex ArtistSuffix = new(@"^(?<title>.+?)\s+-\s+@(?<artist>[^@]+)$", RegexOptions.Compiled);
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".webp", ".gif", ".bmp"
    };

    public static IReadOnlyList<ArtPiece> Load()
    {
        var galleryPath = Path.Combine(AppContext.BaseDirectory, "Assets", "Gallery");
        if (!Directory.Exists(galleryPath))
            return [];

        return Directory
            .EnumerateFiles(galleryPath, "*", SearchOption.AllDirectories)
            .Where(path => SupportedExtensions.Contains(Path.GetExtension(path)))
            .Select(path => CreateArtPiece(galleryPath, path))
            .OrderBy(piece => piece.Category)
            .ThenBy(piece => piece.Title)
            .ToList();
    }

    private static ArtPiece CreateArtPiece(string galleryPath, string path)
    {
        var fileName = Path.GetFileNameWithoutExtension(path);
        var match = ArtistSuffix.Match(fileName);
        return new ArtPiece
        {
            Title = match.Success ? match.Groups["title"].Value : fileName,
            Artist = match.Success ? match.Groups["artist"].Value.Trim() : "",
            FilePath = path,
            Category = GetCategory(galleryPath, path),
            AddedAt = File.GetLastWriteTime(path)
        };
    }

    private static string GetCategory(string galleryPath, string path)
    {
        var relativeDirectory = Path.GetDirectoryName(Path.GetRelativePath(galleryPath, path));
        if (!string.IsNullOrWhiteSpace(relativeDirectory) && relativeDirectory != ".")
            return relativeDirectory.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];

        return "Artwork";
    }
}
