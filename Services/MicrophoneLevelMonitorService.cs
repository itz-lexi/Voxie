using NAudio.Wave;

namespace Voxie.Services;

public sealed class MicrophoneLevelMonitorService : IDisposable
{
    private WaveInEvent? _waveIn;

    public event EventHandler<double>? LevelChanged;

    public void Start(int deviceNumber)
    {
        Stop();
        _waveIn = new WaveInEvent
        {
            DeviceNumber = deviceNumber,
            WaveFormat = new WaveFormat(16000, 16, 1),
            BufferMilliseconds = 90
        };
        _waveIn.DataAvailable += ReadLevel;
        _waveIn.StartRecording();
    }

    public void Stop()
    {
        if (_waveIn is null)
            return;

        _waveIn.StopRecording();
        _waveIn.DataAvailable -= ReadLevel;
        _waveIn.Dispose();
        _waveIn = null;
        LevelChanged?.Invoke(this, 0);
    }

    public void Dispose() => Stop();

    private void ReadLevel(object? sender, WaveInEventArgs e) =>
        LevelChanged?.Invoke(this, GetPeakAmplitude(e.Buffer, e.BytesRecorded));

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
