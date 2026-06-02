namespace Voxie.Models;

public sealed class AppSettings
{
    public string AudioSource { get; set; } = "";
    public string Language { get; set; } = "English";
    public string Model { get; set; } = "Whisper base.en (local CPU)";
    public bool AutoCopyTranscript { get; set; }
    public string ActivationKey { get; set; } = "F8";
    public double SilenceDurationSeconds { get; set; } = 5;
    public double ActivationThreshold { get; set; } = 0.004;
    public bool EnableNoiseSuppression { get; set; } = true;
    public bool DisableVrChatOsc { get; set; }
}
