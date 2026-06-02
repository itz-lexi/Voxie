using System.IO;
using System.Text;
using Whisper.net;
using Whisper.net.Ggml;

namespace Voxie.Services;

public sealed class WhisperTranscriptionService : ITranscriptionService
{
    public const string ModelFileName = "ggml-base.en.bin";

    public string DisplayName => "Local Whisper base.en";
    public bool IsConfigured => File.Exists(ModelPath);
    public string ModelPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Voxie",
        "Models",
        ModelFileName);

    public async Task<string> TranscribeFileAsync(string filePath, CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
            throw new FileNotFoundException($"Whisper model not found. Place {ModelFileName} in {Path.GetDirectoryName(ModelPath)}.", ModelPath);

        await using var audio = File.OpenRead(filePath);
        using var factory = WhisperFactory.FromPath(ModelPath);
        using var processor = factory.CreateBuilder()
            .WithLanguage("en")
            .Build();

        var transcript = new StringBuilder();
        await foreach (var segment in processor.ProcessAsync(audio, cancellationToken))
            transcript.Append(segment.Text);

        return transcript.ToString().Trim();
    }

    public async Task DownloadModelAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ModelPath)!);
        await using var model = await WhisperGgmlDownloader.Default.GetGgmlModelAsync(GgmlType.BaseEn, cancellationToken: cancellationToken);
        await using var output = File.Create(ModelPath);
        await model.CopyToAsync(output, cancellationToken);
    }
}
