// ============================================================================
// INT VoiceToText — microphone capture (NAudio, 16 kHz mono 16-bit → float32)
// ============================================================================

using NAudio.Wave;

namespace INTVoiceToText.Services;

/// <summary>
/// Captures microphone audio while running. Stop() returns the recorded speech as
/// single-precision float samples at 16 kHz — exactly what Whisper expects.
/// </summary>
public sealed class AudioRecorder : IDisposable
{
    private const int SampleRate = 16000;

    private readonly object _gate = new();
    private WaveInEvent? _waveIn;
    private MemoryStream? _pcm;      // raw 16-bit mono bytes while recording
    private volatile bool _recording;
    private int _deviceNumber;
    private float _sensitivity = 1.0f;

    public void Configure(int deviceNumber, float sensitivity)
    {
        _deviceNumber = Math.Max(0, deviceNumber);
        _sensitivity = Math.Clamp(sensitivity, 0.25f, 10f);
    }

    public static IReadOnlyList<(int index, string name)> GetDevices()
    {
        var list = new List<(int, string)>();
        for (int i = 0; i < WaveInEvent.DeviceCount; i++)
            list.Add((i, WaveInEvent.GetCapabilities(i).ProductName));
        return list;
    }

    /// <summary>Raised with a normalized RMS microphone level (0..1) about every 100 ms.</summary>
    public event Action<float>? LevelAvailable;

    public bool IsRecording => _recording;

    /// <summary>Start capturing from the default microphone. Throws if already running or no device.</summary>
    public void Start()
    {
        var waveIn = new WaveInEvent
        {
            DeviceNumber = _deviceNumber,
            BufferMilliseconds = 100,
            WaveFormat = new WaveFormat(SampleRate, 16, 1), // rate, bits, channels
        };
        waveIn.DataAvailable += OnData;

        lock (_gate)
        {
            if (_recording) { waveIn.Dispose(); return; }
            _pcm = new MemoryStream();
            _waveIn = waveIn;
            _recording = true;
        }

        try
        {
            // Start outside _gate: NAudio may invoke DataAvailable immediately.
            // Some Windows audio drivers can hang during waveInOpen; bound that failure.
            var startTask = Task.Run(() => waveIn.StartRecording());
            if (!startTask.Wait(TimeSpan.FromSeconds(4)))
                throw new TimeoutException("Запуск микрофона превысил 4 секунды.");
        }
        catch
        {
            lock (_gate)
            {
                _recording = false;
                if (ReferenceEquals(_waveIn, waveIn)) _waveIn = null;
                _pcm?.Dispose();
                _pcm = null;
            }
            waveIn.Dispose();
            throw;
        }
    }

    private void OnData(object? sender, WaveInEventArgs e)
    {
        float sumSquares = 0;
        int sampleCount = e.BytesRecorded / 2;
        for (int i = 0; i < sampleCount; i++)
        {
            short sample = BitConverter.ToInt16(e.Buffer, i * 2);
            float normalized = sample / 32768f;
            sumSquares += normalized * normalized;
        }
        float rms = sampleCount == 0 ? 0 : MathF.Sqrt(sumSquares / sampleCount);
        try { LevelAvailable?.Invoke(Math.Clamp(rms * 5.5f * _sensitivity, 0f, 1f)); } catch { }

        lock (_gate)
        {
            if (_recording && _pcm != null)
                _pcm.Write(e.Buffer, 0, e.BytesRecorded);
        }
    }

    /// <summary>Stop capturing and return the recorded audio as float32 @16 kHz mono.</summary>
    public float[] Stop()
    {
        WaveInEvent? waveIn;
        MemoryStream? stream;
        lock (_gate)
        {
            if (!_recording) return Array.Empty<float>();
            _recording = false;
            waveIn = _waveIn;
            _waveIn = null;
            stream = _pcm;
            _pcm = null;
        }

        // Do not hold _gate while NAudio waits for its DataAvailable callback.
        try { waveIn?.StopRecording(); } catch { }
        waveIn?.Dispose();
        if (stream == null) return Array.Empty<float>();

        byte[] pcm = stream.ToArray();
        stream.Dispose();

        // int16 → float32 in [-1, 1]
        var samples = new float[pcm.Length / 2];
        for (int i = 0; i < samples.Length; i++)
            samples[i] = BitConverter.ToInt16(pcm, i * 2) / 32768f;

        return samples;
    }

    public void Dispose()
    {
        WaveInEvent? waveIn;
        lock (_gate)
        {
            _recording = false;
            waveIn = _waveIn;
            _waveIn = null;
            _pcm?.Dispose();
            _pcm = null;
        }
        try { waveIn?.Dispose(); } catch { }
    }
}
