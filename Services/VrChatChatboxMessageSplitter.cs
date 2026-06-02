namespace Voxie.Services;

public static class VrChatChatboxMessageSplitter
{
    public const int MaximumCharacters = 144;

    public static IReadOnlyList<string> Split(string text)
    {
        var remaining = text.Trim();
        var chunks = new List<string>();

        while (remaining.Length > MaximumCharacters)
        {
            var splitAt = remaining.LastIndexOf(' ', MaximumCharacters);
            if (splitAt <= 0)
                splitAt = MaximumCharacters;

            chunks.Add(remaining[..splitAt].Trim());
            remaining = remaining[splitAt..].TrimStart();
        }

        if (remaining.Length > 0)
            chunks.Add(remaining);

        return chunks;
    }
}
