using System.Net.Http;
using System.Text.Json;
using Voxie.Models;

namespace Voxie.Services;

public static class HostedGalleryService
{
    private const string GalleryManifestUrl =
        "https://raw.githubusercontent.com/itz-lexi/Voxie/master/docs/gallery/manifest.json";
    private static readonly HttpClient HttpClient = new();
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    static HostedGalleryService()
    {
        HttpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Voxie");
    }

    public static async Task<IReadOnlyList<ArtPiece>> LoadAsync()
    {
        using var response = await HttpClient.GetAsync(GalleryManifestUrl);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync();

        return (await JsonSerializer.DeserializeAsync<List<ArtPiece>>(stream, JsonOptions) ?? [])
            .Where(piece => Uri.TryCreate(piece.ImageUrl, UriKind.Absolute, out _))
            .OrderBy(piece => piece.Category)
            .ThenBy(piece => piece.Title)
            .ToList();
    }
}
