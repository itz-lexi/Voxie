using System.IO;
using NAudio.Wave;

namespace Voxie.Services;

public sealed record AudioInputDevice(int DeviceNumber, string Name);

public sealed class MicrophoneCaptureService : IDisposable
{
    private const double SilenceThreshold = 0.015;
    private WaveInEvent? _waveIn;
    private WaveFileWriter? _writer;
    private DateTime _lastSoundAt;
    private TimeSpan _silenceDuration;
    private bool _silenceReported;

    public bool IsRecording => _waveIn is not null;
    public event EventHandler? SilenceDetected;

    public static IReadOnlyList<AudioInputDevice> GetDevices() =>
        Enumerable.Range(0, WaveIn.DeviceCount)
            .Select(index => new AudioInputDevice(index, WaveIn.GetCapabilities(index).ProductName))
            .ToList();

    public void Start(int deviceNumber, string outputPath, TimeSpan silenceDuration)
    {
        if (IsRecording)
            throw new InvalidOperationException("Microphone capture is already running.");

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        _silenceDuration = silenceDuration;
        _lastSoundAt = DateTime.UtcNow;
        _silenceReported = false;
        _waveIn = new WaveInEvent
        {
            DeviceNumber = deviceNumber,
            WaveFormat = new WaveFormat(16000, 16, 1),
            BufferMilliseconds = 100
        };
        _writer = new WaveFileWriter(outputPath, _waveIn.WaveFormat);
        _waveIn.DataAvailable += WriteAudio;
        _waveIn.StartRecording();
    }

    public void Stop()
    {
        if (_waveIn is null)
            return;

        _waveIn.StopRecording();
        _waveIn.DataAvailable -= WriteAudio;
        _waveIn.Dispose();
        _writer?.Dispose();
        _waveIn = null;
        _writer = null;
    }

    public void Dispose() => Stop();

    private void WriteAudio(object? sender, WaveInEventArgs e)
    {
        _writer?.Write(e.Buffer, 0, e.BytesRecorded);
        _writer?.Flush();

        if (GetPeakAmplitude(e.Buffer, e.BytesRecorded) >= SilenceThreshold)
            _lastSoundAt = DateTime.UtcNow;
        else if (!_silenceReported && DateTime.UtcNow - _lastSoundAt >= _silenceDuration)
        {
            _silenceReported = true;
            SilenceDetected?.Invoke(this, EventArgs.Empty);
        }
    }

    private static double GetPeakAmplitude(byte[] buffer, int length)
    {
        var peak = 0;
        for (var index = 0; index + 1 < length; index += 2)
        {
            var sample = Math.Abs(BitConverter.ToInt16(buffer, index));
            if (sample > peak)
                peak = sample;
        }
        return peak / 32768d;
    }
}
