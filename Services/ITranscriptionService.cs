namespace Voxie.Services;

public interface ITranscriptionService
{
    string DisplayName { get; }
    bool IsConfigured { get; }
    Task<string> TranscribeFileAsync(string filePath, CancellationToken cancellationToken = default);
}
