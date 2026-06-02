namespace Voxie.Models;

public sealed class ArtPiece
{
    public string Title { get; set; } = "";
    public string Artist { get; set; } = "";
    public string Category { get; set; } = "Artwork";
    public string FilePath { get; set; } = "";
    public DateTime AddedAt { get; set; }
}
