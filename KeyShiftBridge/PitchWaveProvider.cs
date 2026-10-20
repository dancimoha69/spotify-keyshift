using System.Runtime.InteropServices;
using NAudio.Wave;
using SoundTouch;

namespace KeyShiftBridge;

internal sealed class PitchWaveProvider : IWaveProvider
{
    private readonly BufferedWaveProvider _source;
    private readonly SoundTouchProcessor _processor;
    private readonly byte[] _input = new byte[8192];
    private readonly object _gate = new();
    private readonly int _fadeInFrames;
    private long _fadeInFramesPlayed;
    private int _hasAudibleOutput;

    public PitchWaveProvider(BufferedWaveProvider source)
    {
        _source = source;
        WaveFormat = source.WaveFormat;
        // SoundTouch has a short startup latency. Ramp the first real output frames
        // to full level so starting playback does not create a sharp audio transient.
        _fadeInFrames = Math.Max(1, WaveFormat.SampleRate / 8); // 125 ms
        _processor = new SoundTouchProcessor
        {
            SampleRate = WaveFormat.SampleRate,
            Channels = WaveFormat.Channels,
            Tempo = 1.0,
            Pitch = 1.0,
            Rate = 1.0,
        };
        // Shorter SoundTouch windows make pitch updates reach the output sooner.
        // These are a modest reduction from the library defaults to limit artifacts.
        _processor.SetSetting(SettingId.SequenceDurationMs, 32);
        _processor.SetSetting(SettingId.SeekWindowDurationMs, 12);
        _processor.SetSetting(SettingId.OverlapDurationMs, 6);
    }

    public WaveFormat WaveFormat { get; }
    public bool HasAudibleOutput => Volatile.Read(ref _hasAudibleOutput) != 0;

    public double PitchSemiTones
    {
        get { lock (_gate) return _processor.PitchSemiTones; }
        set { lock (_gate) _processor.PitchSemiTones = value; }
    }

    public int Read(Span<byte> buffer)
    {
        lock (_gate)
        {
            var requiredFrames = buffer.Length / (sizeof(float) * WaveFormat.Channels);
            while (_processor.AvailableSamples < requiredFrames)
            {
                var bytes = _source.Read(_input.AsSpan());
                if (bytes <= 0) break;

                var inputSamples = MemoryMarshal.Cast<byte, float>(_input.AsSpan(0, bytes));
                _processor.PutSamples(inputSamples, inputSamples.Length / WaveFormat.Channels);
            }

            var outputSamples = MemoryMarshal.Cast<byte, float>(buffer);
            outputSamples.Clear();
            var receivedFrames = _processor.ReceiveSamples(outputSamples, outputSamples.Length / WaveFormat.Channels);
            var receivedSamples = Math.Min(outputSamples.Length, receivedFrames * WaveFormat.Channels);
            for (var frame = 0; frame < receivedFrames; frame++)
            {
                var fade = (float)Math.Min(1.0, (double)(++_fadeInFramesPlayed) / _fadeInFrames);
                var frameStart = frame * WaveFormat.Channels;
                for (var channel = 0; channel < WaveFormat.Channels; channel++)
                {
                    var index = frameStart + channel;
                    outputSamples[index] *= fade;
                    if (Math.Abs(outputSamples[index]) > 0.00005f)
                        Volatile.Write(ref _hasAudibleOutput, 1);
                }
            }
            // A real-time source must keep feeding the audio device during SoundTouch's
            // initial latency; the cleared tail is silence until processed frames arrive.
            return buffer.Length;
        }
    }
}
