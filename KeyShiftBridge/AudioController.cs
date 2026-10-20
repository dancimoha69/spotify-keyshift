using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;

namespace KeyShiftBridge;

[SupportedOSPlatform("windows10.0.19041")]
internal sealed class AudioController : IDisposable
{
    private const float CaptureAttenuation = 0.001f;
    private static readonly WaveFormat CaptureFormat = WaveFormat.CreateIeeeFloatWaveFormat(44100, 2);
    private readonly SemaphoreSlim _operation = new(1, 1);
    private readonly object _inputGate = new();
    private readonly List<SessionSnapshot> _sessionSnapshots = [];
    private WasapiRecorder? _recorder;
    private WasapiPlayer? _player;
    private BufferedWaveProvider? _input;
    private PitchWaveProvider? _pitchProvider;
    private MMDevice? _sessionDevice;
    private TaskCompletionSource<bool>? _audibleSignal;
    private volatile bool _enabled;
    private long _lastInputSignalTimestamp;
    private float _captureGain = 1.0f;
    private int _attenuationPhase;
    private int _preAttenuationPeakBits;
    private int _postAttenuationPeakBits;
    private double _semitones;
    private string _statusMessage = "Готов к подключению к Spotify";

    public bool Enabled => _enabled;
    public double Semitones => Volatile.Read(ref _semitones);
    public string StatusMessage => _statusMessage;
    public string? OutputDeviceName => _sessionDevice?.FriendlyName;
    public bool? OutputMuted => _player?.IsMuted;
    public float? OutputVolume => _player?.Volume;

    public async Task SetEnabledAsync(bool enabled)
    {
        await _operation.WaitAsync();
        try
        {
            if (!enabled)
            {
                await StopCoreAsync();
                _enabled = false;
                _statusMessage = "Обработка Spotify выключена";
                return;
            }

            if (_enabled) return;

            _statusMessage = "Подключаюсь к Spotify…";
            var spotifyProcesses = Process.GetProcessesByName("Spotify");
            var spotify = spotifyProcesses
                .OrderBy(p => p.StartTime)
                .FirstOrDefault();
            if (spotify is null)
            {
                foreach (var process in spotifyProcesses) process.Dispose();
                _statusMessage = "Сначала открой Spotify";
                return;
            }
            var spotifyPid = (uint)spotify.Id;
            var spotifyPids = spotifyProcesses.Select(p => (uint)p.Id).ToHashSet();
            foreach (var process in spotifyProcesses) process.Dispose();

            _input = new BufferedWaveProvider(CaptureFormat, TimeSpan.FromSeconds(1))
            {
                DiscardOnBufferOverflow = true,
                ReadFully = true,
            };
            _pitchProvider = new PitchWaveProvider(_input) { PitchSemiTones = Semitones };
            Volatile.Write(ref _attenuationPhase, 0);
            Interlocked.Exchange(ref _preAttenuationPeakBits, 0);
            Interlocked.Exchange(ref _postAttenuationPeakBits, 0);
            Volatile.Write(ref _captureGain, 1.0f);

            Interlocked.Exchange(ref _lastInputSignalTimestamp, 0);
            _audibleSignal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _recorder = await new WasapiRecorderBuilder()
                .WithProcessLoopback(spotifyPid, ProcessLoopbackMode.IncludeTargetProcessTree)
                .WithFormat(CaptureFormat)
                .WithBufferLength(50)
                .BuildAsync();
            _recorder.DataAvailable += OnDataAvailable;
            _recorder.StartRecording();

            var signal = await Task.WhenAny(_audibleSignal.Task, Task.Delay(TimeSpan.FromSeconds(3)));
            if (signal != _audibleSignal.Task || !_audibleSignal.Task.Result)
            {
                await StopCoreAsync();
                _statusMessage = "Не получил звук Spotify. Проверь, что музыка играет и в Spotify выключен эксклюзивный режим.";
                return;
            }

            // Locate the endpoint only after process loopback confirms real audio. Spotify
            // may have several active processes and its multimedia and console routes can differ.
            _sessionDevice = FindSpotifyRenderDevice(spotifyPids);
            if (_sessionDevice is null)
            {
                await StopCoreAsync();
                _statusMessage = "Захват Spotify есть, но его устройство вывода не найдено. Исходный звук оставлен включённым.";
                return;
            }

            if (!AttenuateSpotifySessions(spotifyPids))
            {
                await StopCoreAsync();
                _statusMessage = "Не получилось приглушить только исходный звук Spotify. Оригинальный звук оставлен без изменений.";
                return;
            }

            // Session mute also mutes process-loopback on some Windows audio paths. Keep
            // the stream alive at -60 dB. Measure the actual attenuation before compensating
            // it, so in-flight full-level packets cannot become a clipped startup transient.
            Volatile.Write(ref _attenuationPhase, 1);
            await Task.Delay(150);
            Interlocked.Exchange(ref _postAttenuationPeakBits, 0);
            await Task.Delay(200);
            var beforePeak = BitConverter.Int32BitsToSingle(Volatile.Read(ref _preAttenuationPeakBits));
            var afterPeak = BitConverter.Int32BitsToSingle(Volatile.Read(ref _postAttenuationPeakBits));
            if (afterPeak <= 0.0000001f)
            {
                await StopCoreAsync();
                _statusMessage = "После снижения громкости Spotify захват стал пустым. Исходная громкость восстановлена.";
                return;
            }
            var measuredGain = beforePeak > 0.0000001f ? beforePeak / afterPeak : 1.0f;
            var captureGain = Math.Clamp(measuredGain, 1.0f, 1.0f / CaptureAttenuation);

            _input.ClearBuffer();
            _audibleSignal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Interlocked.Exchange(ref _lastInputSignalTimestamp, 0);
            Volatile.Write(ref _captureGain, captureGain);

            // Use the exact endpoint whose Spotify session will be muted.
            _player = new WasapiPlayerBuilder()
                .WithDevice(_sessionDevice)
                .WithSharedMode()
                .WithLatency(40)
                .WithLowLatency()
                .WithMmcssThreadPriority("Pro Audio")
                .Build();
            _player.Init(_pitchProvider);
            _player.IsMuted = false;
            _player.Volume = 1.0f;
            _player.Play();

            var recapture = await Task.WhenAny(_audibleSignal.Task, Task.Delay(TimeSpan.FromSeconds(2)));
            if (recapture != _audibleSignal.Task || !_audibleSignal.Task.Result)
            {
                await StopCoreAsync();
                _statusMessage = "Windows заглушил захват при снижении громкости Spotify. Исходная громкость восстановлена.";
                return;
            }

            var outputDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);
            while (!_pitchProvider.HasAudibleOutput && DateTime.UtcNow < outputDeadline)
                await Task.Delay(25);
            if (!_pitchProvider.HasAudibleOutput)
            {
                await StopCoreAsync();
                _statusMessage = "Обработанный звук не появился. Исходный звук Spotify оставлен включённым.";
                return;
            }

            if (_sessionSnapshots.Count == 0)
            {
                await StopCoreAsync();
                _statusMessage = "Не нашёл управляемую аудиосессию Spotify. Его звук не изменён.";
                return;
            }

            await Task.Delay(500);
            var lastSignal = Interlocked.Read(ref _lastInputSignalTimestamp);
            if (lastSignal == 0 || Stopwatch.GetElapsedTime(lastSignal) > TimeSpan.FromMilliseconds(400))
            {
                await StopCoreAsync();
                _statusMessage = "После переключения Spotify перестал отдавать звук обработчику. Исходный звук восстановлен.";
                return;
            }

            _enabled = true;
            _statusMessage = "Spotify обрабатывается · исходный темп сохранён";
        }
        catch (Exception ex)
        {
            await StopCoreAsync();
            _enabled = false;
            _statusMessage = $"Не удалось подключить Spotify: {ex.Message}";
        }
        finally
        {
            _operation.Release();
        }
    }

    public void SetSemitones(double semitones)
    {
        semitones = Math.Clamp(semitones, -12, 12);
        Volatile.Write(ref _semitones, semitones);
        if (_pitchProvider is not null) _pitchProvider.PitchSemiTones = semitones;
        if (_enabled) _statusMessage = $"Spotify обрабатывается · {semitones:+#;-#;0} полутонов · темп сохранён";
    }

    private void OnDataAvailable(ReadOnlySpan<byte> buffer, AudioClientBufferFlags flags, long devicePosition, long qpcPosition)
    {
        if (buffer.IsEmpty) return;
        BufferedWaveProvider? input;
        lock (_inputGate) input = _input;
        if (input is null) return;
        var copy = new byte[buffer.Length];
        if ((flags & AudioClientBufferFlags.Silent) == 0)
        {
            var samples = MemoryMarshal.Cast<byte, float>(buffer);
            var peak = 0f;
            for (var i = 0; i < samples.Length; i++)
            {
                var value = Math.Abs(samples[i]);
                if (value > peak) peak = value;
            }

            if (peak > 0.0000001f)
            {
                Interlocked.Exchange(ref _lastInputSignalTimestamp, Stopwatch.GetTimestamp());
                _audibleSignal?.TrySetResult(true);
            }
            RecordPeak(ref _preAttenuationPeakBits, ref _postAttenuationPeakBits,
                Volatile.Read(ref _attenuationPhase) != 0, peak);
            var gain = Volatile.Read(ref _captureGain);
            // Protect the transition from any final pre-attenuation packet still in flight.
            if (peak > 0f) gain = Math.Min(gain, 0.85f / peak);
            var amplified = MemoryMarshal.Cast<byte, float>(copy.AsSpan());
            for (var i = 0; i < samples.Length; i++)
                amplified[i] = Math.Clamp(samples[i] * gain, -1.0f, 1.0f);
        }
        lock (_inputGate)
        {
            if (ReferenceEquals(input, _input)) input.AddSamples(copy, 0, copy.Length);
        }
    }

    private static MMDevice? FindSpotifyRenderDevice(HashSet<uint> spotifyPids)
    {
        using var enumerator = new MMDeviceEnumerator();
        foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            var endpointId = device.ID;
            var hasActiveSpotifySession = false;
            try
            {
                var sessions = device.AudioSessionManager.Sessions;
                for (var index = 0; index < sessions.Count; index++)
                {
                    var session = sessions[index];
                    try
                    {
                        if (session.State == AudioSessionState.AudioSessionStateActive && spotifyPids.Contains(session.GetProcessID))
                            hasActiveSpotifySession = true;
                    }
                    catch { }
                    finally { session.Dispose(); }
                }
            }
            catch { }
            device.Dispose();

            if (hasActiveSpotifySession)
                return enumerator.GetDevice(endpointId);
        }
        return null;
    }

    private bool AttenuateSpotifySessions(HashSet<uint> spotifyPids)
    {
        if (_sessionDevice is null) return false;
        var sessions = _sessionDevice.AudioSessionManager.Sessions;
        for (var index = 0; index < sessions.Count; index++)
        {
            var session = sessions[index];
            try
            {
                if (!spotifyPids.Contains(session.GetProcessID))
                {
                    session.Dispose();
                    continue;
                }

                var volume = session.SimpleAudioVolume;
                var originalMute = volume.Mute;
                var originalVolume = volume.Volume;
                if (originalMute || originalVolume <= 0)
                {
                    session.Dispose();
                    continue;
                }

                try
                {
                    volume.Mute = false;
                    volume.Volume = originalVolume * CaptureAttenuation;
                    _sessionSnapshots.Add(new SessionSnapshot(session, originalMute, originalVolume));
                }
                catch
                {
                    try
                    {
                        volume.Volume = originalVolume;
                        volume.Mute = originalMute;
                    }
                    catch { }
                    throw;
                }
            }
            catch
            {
                session.Dispose();
            }
        }
        return _sessionSnapshots.Count > 0;
    }

    private async Task StopCoreAsync()
    {
        try { _recorder?.StopRecording(); } catch { }
        _recorder?.Dispose();
        _recorder = null;

        try { _player?.Stop(); } catch { }
        _player?.Dispose();
        _player = null;
        _pitchProvider = null;
        lock (_inputGate) _input = null;

        foreach (var snapshot in _sessionSnapshots)
        {
            try
            {
                snapshot.Session.SimpleAudioVolume.Volume = snapshot.Volume;
                snapshot.Session.SimpleAudioVolume.Mute = snapshot.Mute;
            }
            catch { }
            snapshot.Session.Dispose();
        }
        _sessionSnapshots.Clear();
        _sessionDevice?.Dispose();
        _sessionDevice = null;
        _audibleSignal = null;
        Interlocked.Exchange(ref _lastInputSignalTimestamp, 0);
        Volatile.Write(ref _captureGain, 1.0f);
        Volatile.Write(ref _attenuationPhase, 0);
        await Task.CompletedTask;
    }

    private static void RecordPeak(ref int beforeBits, ref int afterBits, bool attenuated, float peak)
    {
        ref var location = ref attenuated ? ref afterBits : ref beforeBits;
        var candidate = BitConverter.SingleToInt32Bits(peak);
        while (true)
        {
            var current = Volatile.Read(ref location);
            if (BitConverter.Int32BitsToSingle(current) >= peak) return;
            if (Interlocked.CompareExchange(ref location, candidate, current) == current) return;
        }
    }

    public void Dispose()
    {
        _operation.Wait();
        try { StopCoreAsync().GetAwaiter().GetResult(); }
        finally
        {
            _operation.Release();
            _operation.Dispose();
        }
    }

    private sealed record SessionSnapshot(AudioSessionControl Session, bool Mute, float Volume);
}
