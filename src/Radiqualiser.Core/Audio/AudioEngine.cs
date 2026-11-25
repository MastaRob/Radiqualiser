using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using Radiqualiser.Core.Dsp;

namespace Radiqualiser.Core.Audio;

/// <param name="SafetyBufferMs">Extra audio held in reserve against timing hiccups. Lower = less delay, higher = more glitch-proof.</param>
/// <param name="LowLatency">Ask Windows for its small-buffer mode where the drivers allow it.</param>
/// <param name="FollowWindowsVolume">Keep the virtual cable's Windows volume slider and the real output device's volume in step.</param>
public sealed record EngineOptions(int SafetyBufferMs = 20, bool LowLatency = true, bool FollowWindowsVolume = true);

public sealed record EngineStats(
    bool Running, int CaptureRate, int OutputRate, double BufferMs, double DriftPpm, int Underruns, int Resyncs,
    int Overflows, double TotalLatencyMs, bool CaptureLowLatency, bool OutputLowLatency, double VolumePercent,
    bool Muted, int Oversampling);

/// <summary>
/// Captures what Windows plays into the virtual cable, runs it through the <see cref="DspChain"/>, and plays the result
/// on the real output device.
/// </summary>
public sealed class AudioEngine : IDisposable
{
    enum SampleKind { Float32, Pcm16, Pcm24, Pcm32 }

    readonly object _gate = new();
    MMDeviceEnumerator? _enum;
    MMDevice? _capDev, _renDev, _cableDev;
    WasapiRecorder? _rec;
    WasapiPlayer? _player;
    Timer? _volTimer;

    SpscRing? _ring;
    AsyncResampler? _rs;
    DspChain? _chain;
    DspParams _params = DspParams.Default;
    EngineOptions _opt = new();

    // capture side
    SampleKind _kind;
    int _capChannels, _capBlockAlign, _capRate;
    float[] _capTmp = new float[4096];
    int _overflows;

    // render side
    int _outRate;
    double[] _l = new double[2048], _r = new double[2048];
    bool _buffering = true;
    double _fillAvg;
    int _maxChunk;
    int _underruns, _resyncs;
    double _capLatencyMs, _outLatencyMs;
    bool _capLow, _outLow;

    volatile bool _running;
    volatile bool _stopping;
    // volume link
    AudioEndpointVolume? _cableVol, _outVol;
    bool _linked;
    float _lastCable, _lastOut;
    bool _lastCableMute, _lastOutMute;
    int _pollBusy;
    double _volPercent = 100;
    bool _muted;

    /// <summary>Raised (on a worker thread) if a device disappears or errors while running.</summary>
    public event Action<string>? Faulted;

    public bool IsRunning => _running;
    public DspChain? Chain => _chain;
    public string? Notes { get; private set; }

    public void SetParams(DspParams p)
    {
        _params = p;
        _chain?.SetParams(p);
    }

    public void SetOptions(EngineOptions opt) => _opt = opt;

    SweepConfig _sweepCfg = SweepConfig.Off;

    /// <summary>Test-tone generator; only audible while the engine is running.</summary>
    public void SetSweep(SweepConfig cfg)
    {
        _sweepCfg = cfg;
        _chain?.SetSweep(cfg);
    }

    /// <summary>Call from a worker (MTA) thread. Throws if a device cannot be opened.</summary>
    public void Start(string captureId, string renderId, string? cableRenderId, EngineOptions opt)
    {
        lock (_gate)
        {
            StopCore();
            _opt = opt;
            _stopping = false;
            try
            {
                _enum = new MMDeviceEnumerator();
                _capDev = _enum.GetDevice(captureId);
                _renDev = _enum.GetDevice(renderId);
                if (cableRenderId != null)
                {
                    try { _cableDev = _enum.GetDevice(cableRenderId); } catch { _cableDev = null; }
                }

                _player = new WasapiPlayerBuilder()
                    .WithDevice(_renDev).WithSharedMode().WithEventSync().WithLatency(20)
                    .WithMmcssThreadPriority("Pro Audio").WithLowLatency(opt.LowLatency).Build();
                _outRate = _player.DeviceMixFormat.SampleRate;

                _rec = new WasapiRecorderBuilder()
                    .WithDevice(_capDev).WithSharedMode().WithEventSync()
                    .WithMmcssThreadPriority("Pro Audio").WithLowLatency(opt.LowLatency).Build();
                ConfigureCaptureFormat(_rec.WaveFormat);

                _ring = new SpscRing(_capRate * 2 * 2);      // two seconds of stereo
                _rs = new AsyncResampler(_capRate, _outRate);
                _chain = new DspChain(_outRate);
                _chain.SetParams(_params);
                _chain.SetSweep(_sweepCfg);
                _buffering = true;
                _fillAvg = 0;
                _maxChunk = 0;
                _underruns = _resyncs = _overflows = 0;

                _player.Init(new Provider(this, WaveFormat.CreateIeeeFloatWaveFormat(_outRate, 2)));
                _player.PlaybackStopped += OnStopped;
                _rec.RecordingStopped += OnStopped;
                _rec.DataAvailable += OnCapture;

                _linked = false;
                try { _cableVol = _cableDev?.AudioEndpointVolume; _outVol = _renDev.AudioEndpointVolume; }
                catch { _cableVol = _outVol = null; }
                PollVolume(null);
                _rec.StartRecording();
                _player.Play();

                _capLatencyMs = SafeMs(() => _rec.LatencyMilliseconds);
                _outLatencyMs = SafeMs(() => _player.LatencyMilliseconds);
                _capLow = _rec.LowLatencyActive;
                _outLow = _player.LowLatencyActive;
                var notes = new List<string>();
                if (_capRate != _outRate)
                    notes.Add($"Cable is {_capRate} Hz but the output is {_outRate} Hz; converting. For best quality set both to the same rate in Windows Sound settings.");
                int bits = DeviceBits(_renDev);
                if (bits is > 0 and <= 16)
                    notes.Add("The output device is set to 16-bit, which adds a faint hiss at low Windows volume. Set it to 24-bit: Sound settings → the device → Properties → Advanced → Default Format.");
                Notes = notes.Count > 0 ? string.Join("  ", notes) : null;

                _running = true;
                _volTimer = new Timer(PollVolume, null, 40, 40);
            }
            catch
            {
                StopCore();
                throw;
            }
        }
    }

    public void Stop()
    {
        lock (_gate) StopCore();
    }

    void StopCore()
    {
        _stopping = true;
        _running = false;
        _volTimer?.Dispose(); _volTimer = null;
        _cableVol = _outVol = null;
        Quiet(() => { if (_rec != null) { _rec.DataAvailable -= OnCapture; _rec.StopRecording(); } });
        Quiet(() => _player?.Stop());
        Quiet(() => _rec?.Dispose());
        Quiet(() => _player?.Dispose());
        Quiet(() => _capDev?.Dispose());
        Quiet(() => _renDev?.Dispose());
        Quiet(() => _cableDev?.Dispose());
        Quiet(() => _enum?.Dispose());
        _rec = null; _player = null; _capDev = _renDev = _cableDev = null; _enum = null;
    }

    /// <summary>The bit depth Windows actually sends to the device (its "Default Format"), or 0 if unknown.</summary>
    static int DeviceBits(MMDevice dev)
    {
        try
        {
            if (dev.Properties[PropertyKeys.PKEY_AudioEngine_DeviceFormat].Value is byte[] b && b.Length >= 16)
            {
                int container = BitConverter.ToUInt16(b, 14);
                return b.Length >= 20 ? Math.Min(container, (int)BitConverter.ToUInt16(b, 18)) : container;
            }
        }
        catch
        {
            // property not readable on this device: say nothing
        }
        return 0;
    }

    static void Quiet(Action a) { try { a(); } catch { /* tearing down; nothing useful to do */ } }
    static double SafeMs(Func<int> f) { try { return f(); } catch { return 0; } }

    void OnStopped(object? sender, StoppedEventArgs e)
    {
        if (_stopping || !_running) return;
        _running = false;
        string msg = e.Exception?.Message ?? "The audio device stopped.";
        Task.Run(() => Faulted?.Invoke(msg));
    }

    /// <summary>
    /// While this app is running, the Windows volume slider and keys act on the virtual cable, which takes no notice of
    /// them. So the two sliders are kept in step: whichever one changes is copied to the other. The real output device
    /// always holds the level the user last chose, which means volume and mute behave exactly as they did before, there
    /// is no jump in loudness when the app starts, stops or crashes, and (because Windows applies that volume after this
    /// app's processing) the valve stage is driven the same at any listening level.
    /// </summary>
    void PollVolume(object? _)
    {
        var chain = _chain;
        if (chain == null || Interlocked.Exchange(ref _pollBusy, 1) != 0) return;
        try
        {
            var cv = _cableVol; var ov = _outVol;
            if (ov != null)
            {
                _volPercent = ov.MasterVolumeLevelScalar * 100.0;
                _muted = ov.Mute;
            }
            if (cv == null || ov == null || !_opt.FollowWindowsVolume)
            {
                _linked = false;
                return;
            }
            if (!_linked)
            {
                // Start from the real device's level so nothing changes audibly when the app takes over.
                cv.MasterVolumeLevelScalar = ov.MasterVolumeLevelScalar;
                cv.Mute = ov.Mute;
                _lastCable = cv.MasterVolumeLevelScalar; _lastOut = ov.MasterVolumeLevelScalar;
                _lastCableMute = cv.Mute; _lastOutMute = ov.Mute;
                _linked = true;
                return;
            }

            float c = cv.MasterVolumeLevelScalar, o = ov.MasterVolumeLevelScalar;
            if (Math.Abs(c - _lastCable) > 0.0005f) { ov.MasterVolumeLevelScalar = c; }
            else if (Math.Abs(o - _lastOut) > 0.0005f) { cv.MasterVolumeLevelScalar = o; }
            bool cm = cv.Mute, om = ov.Mute;
            if (cm != _lastCableMute) ov.Mute = cm;
            else if (om != _lastOutMute) cv.Mute = om;

            // Read back what the devices actually settled on, so rounding differences are never mistaken for a change.
            _lastCable = cv.MasterVolumeLevelScalar; _lastOut = ov.MasterVolumeLevelScalar;
            _lastCableMute = cv.Mute; _lastOutMute = ov.Mute;
        }
        catch
        {
            // device went away mid-poll; the stopped handler deals with it
        }
        finally
        {
            Volatile.Write(ref _pollBusy, 0);
        }
    }

    /// <summary>Mutes this app's own stream in the Windows mixer (used by the test harness to check the path silently).</summary>
    public void SetSessionMute(bool mute)
    {
        var p = _player;
        if (p != null) p.IsMuted = mute;
    }

    public EngineStats Stats()
    {
        var chain = _chain;
        double chainMs = chain != null && _outRate > 0 ? chain.LatencySamples * 1000.0 / _outRate : 0;
        double bufMs = _capRate > 0 ? _fillAvg * 1000.0 / _capRate : 0;
        double ppm = (_rs?.Adjust ?? 0) * 1e6;
        return new EngineStats(_running, _capRate, _outRate, bufMs, ppm, _underruns, _resyncs, _overflows,
            _capLatencyMs + bufMs + chainMs + _outLatencyMs, _capLow, _outLow, _volPercent, _muted,
            chain?.OversampleFactor ?? 0);
    }

    // ---------------- capture thread ----------------

    void ConfigureCaptureFormat(WaveFormat wf)
    {
        var enc = wf.Encoding;
        if (wf is WaveFormatExtensible ext) enc = ext.ToStandardWaveFormat().Encoding;
        _kind = (enc, wf.BitsPerSample) switch
        {
            (WaveFormatEncoding.IeeeFloat, 32) => SampleKind.Float32,
            (WaveFormatEncoding.Pcm, 16) => SampleKind.Pcm16,
            (WaveFormatEncoding.Pcm, 24) => SampleKind.Pcm24,
            (WaveFormatEncoding.Pcm, 32) => SampleKind.Pcm32,
            _ => throw new NotSupportedException($"Unsupported capture format: {wf}"),
        };
        _capChannels = wf.Channels;
        _capBlockAlign = wf.BlockAlign;
        _capRate = wf.SampleRate;
    }

    void OnCapture(ReadOnlySpan<byte> data, AudioClientBufferFlags flags, long devicePosition, long qpcPosition)
    {
        var ring = _ring;
        if (ring == null) return;
        int frames = data.Length / _capBlockAlign;
        if (frames == 0) return;
        if (_capTmp.Length < frames * 2) _capTmp = new float[frames * 2];
        Span<float> tmp = _capTmp.AsSpan(0, frames * 2);
        int ch = _capChannels;
        int second = ch > 1 ? 1 : 0;

        if ((flags & AudioClientBufferFlags.Silent) != 0)
        {
            tmp.Clear();
        }
        else
        {
            switch (_kind)
            {
                case SampleKind.Float32:
                {
                    var s = MemoryMarshal.Cast<byte, float>(data);
                    for (int i = 0; i < frames; i++) { tmp[2 * i] = s[i * ch]; tmp[2 * i + 1] = s[i * ch + second]; }
                    break;
                }
                case SampleKind.Pcm16:
                {
                    var s = MemoryMarshal.Cast<byte, short>(data);
                    const float k = 1f / 32768f;
                    for (int i = 0; i < frames; i++) { tmp[2 * i] = s[i * ch] * k; tmp[2 * i + 1] = s[i * ch + second] * k; }
                    break;
                }
                case SampleKind.Pcm32:
                {
                    var s = MemoryMarshal.Cast<byte, int>(data);
                    const float k = 1f / 2147483648f;
                    for (int i = 0; i < frames; i++) { tmp[2 * i] = s[i * ch] * k; tmp[2 * i + 1] = s[i * ch + second] * k; }
                    break;
                }
                case SampleKind.Pcm24:
                {
                    const float k = 1f / 8388608f;
                    for (int i = 0; i < frames; i++)
                    {
                        int o = i * _capBlockAlign;
                        tmp[2 * i] = Read24(data, o) * k;
                        tmp[2 * i + 1] = Read24(data, o + second * 3) * k;
                    }
                    break;
                }
            }
        }
        if (ring.Write(tmp) < tmp.Length) _overflows++;
    }

    static int Read24(ReadOnlySpan<byte> d, int o) => (d[o] | (d[o + 1] << 8) | ((sbyte)d[o + 2] << 16));

    // ---------------- render thread ----------------

    sealed class Provider(AudioEngine engine, WaveFormat format) : IWaveProvider
    {
        public WaveFormat WaveFormat { get; } = format;
        public int Read(Span<byte> buffer) => engine.Render(buffer);
    }

    int Render(Span<byte> buffer)
    {
        var f = MemoryMarshal.Cast<byte, float>(buffer);
        int frames = f.Length / 2;
        var ring = _ring; var rs = _rs; var chain = _chain;
        if (frames == 0 || ring == null || rs == null || chain == null)
        {
            buffer.Clear();
            return buffer.Length;
        }
        if (_l.Length < frames) { _l = new double[frames]; _r = new double[frames]; }
        Span<double> l = _l.AsSpan(0, frames), r = _r.AsSpan(0, frames);

        // Hold one render chunk plus the safety margin in reserve; steer the resampler to keep it there.
        if (frames > _maxChunk) _maxChunk = frames;
        double ratio = (double)_capRate / _outRate;
        int target = (int)(_maxChunk * ratio) + _capRate * Math.Clamp(_opt.SafetyBufferMs, 2, 500) / 1000;
        int avail = ring.Available / 2;

        if (avail > target * 2 + _capRate / 10)
        {
            ring.Skip((avail - target) * 2);         // far too much queued (e.g. after a stall): jump back to the target
            avail = target;
            _fillAvg = target;
            _resyncs++;
        }
        if (_buffering && avail >= target)
        {
            _buffering = false;
            _fillAvg = avail;
            rs.Adjust = 0;
        }

        bool ok = false;
        if (!_buffering)
        {
            double alpha = 1 - Math.Exp(-frames / (_outRate * 2.0));          // ~2 s smoothing
            _fillAvg += (avail - _fillAvg) * alpha;
            double errSeconds = (_fillAvg - target) / _capRate;
            rs.Adjust = Math.Clamp(0.1 * errSeconds, -0.002, 0.002);
            ok = rs.Process(ring, l, r);
            if (!ok)
            {
                _underruns++;
                _buffering = true;
            }
        }
        if (!ok) { l.Clear(); r.Clear(); }

        chain.Process(l, r);

        for (int i = 0; i < frames; i++)
        {
            f[2 * i] = (float)l[i];
            f[2 * i + 1] = (float)r[i];
        }
        return buffer.Length;
    }

    public void Dispose() => Stop();
}
