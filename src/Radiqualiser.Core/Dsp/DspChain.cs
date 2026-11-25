namespace Radiqualiser.Core.Dsp;

/// <param name="On">Replace the incoming audio with a test tone.</param>
/// <param name="Seconds">Time for one pass from 20 Hz to 20 kHz.</param>
/// <param name="LevelDb">Tone level in dBFS.</param>
/// <param name="Hold">Stay on the current frequency instead of sweeping.</param>
/// <param name="ManualHz">While held: glide to this frequency (0 = stay where the sweep stopped). Lets the tone be dragged by hand.</param>
public sealed record SweepConfig(bool On, double Seconds, double LevelDb, bool Hold, double ManualHz = 0)
{
    public static readonly SweepConfig Off = new(false, 20, -20, false);
}

/// <summary>
/// The full signal path at the output sample rate:
/// (test tone) → preamp → drawn-curve EQ → transient softener → de-harsh → phantom bass → valve → tape →
/// glue compressor → reverb → output trim / auto level → limiter.
/// Parameters are compiled on the calling (UI) thread and swapped in atomically; the audio thread never locks.
/// </summary>
public sealed class DspChain
{
    sealed class Compiled
    {
        public double InGain = 1, OutGain = 1;
        public bool AutoLevel;
        public TubeCompiled Tube = TubeMath.Compile(TubeParams.Off, 48000);
        public TapeCompiled Tape = TapeMath.Compile(TapeParams.Off, 48000);
        public double Transient, Deharsh, DeharshHz = 3500, Bass, BassHz = 90, Glue, Space, SpaceDecay = 6;
    }

    readonly double _fs;
    readonly TubeSection _tube;
    readonly TapeSection _tape;
    readonly TransientSoftener _soften;
    readonly Deharsh _deharsh;
    readonly PhantomBass _bass;
    readonly GlueComp _glue;
    readonly CathedralReverb _reverb;
    readonly Limiter _limiter;

    Compiled _pending;
    Compiled _cur;

    // EQ: the curve is turned into a filter on a worker thread and handed to the audio thread when ready
    readonly FirDesigner _designer;
    readonly FirConvolver _conv;
    FirKernel _kernel = FirKernel.Flat;
    EqCurve _wantedCurve = EqCurve.Flat;
    int _designing;

    double _inGain = 1, _outGain = 1, _vol = 1;
    double _volTarget = 1;

    // auto level
    readonly BiquadCoeffs _kShelf, _kHp;
    BiquadState _kInL1, _kInL2, _kInR1, _kInR2, _kOutL1, _kOutL2, _kOutR1, _kOutR2;
    double _pIn = 1e-9, _pOut = 1e-9;   // smoothed loudness-weighted power before / after processing
    double _match = 1;                  // gain that makes the processed loudness equal the unprocessed loudness
    double _overDb;                     // how far the loudness-matched peaks would overshoot full scale (held)
    double _overHold;                   // seconds left before the held overshoot may relax
    int _fastSamples;
    float _autoDb;

    // sweep generator
    SweepConfig _sweep = SweepConfig.Off;
    double _swPhase, _swPos, _swGain;
    float _sweepHz;

    // meters / analyser taps (written by the audio thread, read by the UI)
    float _inPeak, _outPeak, _minGain = 1;
    readonly float[] _spectrum = new float[32768];
    int _spectrumIdx;

    public double SampleRate => _fs;
    public int LatencySamples => _soften.LatencySamples + _tube.LatencySamples + _tape.LatencySamples + _limiter.LatencySamples;
    public int OversampleFactor => _tube.OversampleFactor;

    public DspChain(int sampleRate)
    {
        _fs = sampleRate;
        _designer = new FirDesigner(sampleRate);
        _conv = new FirConvolver(_designer.Block, _designer.Length);
        _tube = new TubeSection(sampleRate);
        _tape = new TapeSection(sampleRate);
        _soften = new TransientSoftener(sampleRate);
        _deharsh = new Deharsh(sampleRate);
        _bass = new PhantomBass(sampleRate);
        _glue = new GlueComp(sampleRate);
        _reverb = new CathedralReverb(sampleRate);
        _limiter = new Limiter(sampleRate);
        _pending = _cur = new Compiled { Tube = TubeMath.Compile(TubeParams.Off, _fs), Tape = TapeMath.Compile(TapeParams.Off, _fs) };
        // Approximation of the standard loudness ("K") weighting: ears are less sensitive to bass and a little more to
        // upper mids, so a bass boost should count for less than a midrange boost when matching loudness.
        _kShelf = BiquadCoeffs.Design(BandType.HighShelf, 1500, 4, 0.707, _fs);
        _kHp = BiquadCoeffs.Design(BandType.HighPass, 60, 0, 0.5, _fs);
    }

    /// <summary>Thread-safe: call from the UI whenever a control changes.</summary>
    public void SetParams(DspParams p)
    {
        var tape = p.Tape ?? TapeParams.Off;
        var c = new Compiled
        {
            Tube = TubeMath.Compile(p.Bypass ? p.Tube with { Enabled = false } : p.Tube, _fs),
            Tape = TapeMath.Compile(p.Bypass ? tape with { Enabled = false } : tape, _fs),
        };
        if (!p.Bypass)
        {
            var fx = p.Fx ?? FxParams.Off;
            c.Transient = fx.SoftenOn ? Math.Clamp(fx.Transient, 0, 1) : 0;
            c.Deharsh = fx.SoftenOn ? Math.Clamp(fx.Deharsh, 0, 1) : 0;
            c.DeharshHz = fx.DeharshHz;
            c.Bass = fx.BassOn ? Math.Clamp(fx.BassAmount, 0, 1) : 0;
            c.BassHz = fx.BassHz;
            c.Glue = fx.GlueOn ? Math.Clamp(fx.Glue, 0, 1) : 0;
            c.Space = fx.SpaceOn ? Math.Clamp(fx.SpaceAmount, 0, 1) : 0;
            c.SpaceDecay = fx.SpaceDecay;
            c.AutoLevel = p.AutoLevel;
            if (!p.AutoLevel)
            {
                c.InGain = Math.Pow(10, p.PreampDb / 20.0);
                c.OutGain = Math.Pow(10, p.OutputDb / 20.0);
            }
        }
        Volatile.Write(ref _pending, c);

        var curve = p.Bypass || !p.EqEnabled
            ? EqCurve.Flat
            : new EqCurve(p.Bands.Where(b => b.Enabled).Select(b => (b.Freq, b.GainDb)), p.EqRounding);
        if (!curve.SameAs(Volatile.Read(ref _wantedCurve)))
        {
            Volatile.Write(ref _wantedCurve, curve);
            if (Interlocked.CompareExchange(ref _designing, 1, 0) == 0) Task.Run(DesignLoop);
        }
    }

    /// <summary>Builds filters for the most recently requested curve until it has caught up. One at a time; while a point is being dragged, intermediate positions are simply skipped.</summary>
    void DesignLoop()
    {
        EqCurve? done = null;
        while (true)
        {
            var want = Volatile.Read(ref _wantedCurve);
            if (ReferenceEquals(want, done))
            {
                Volatile.Write(ref _designing, 0);
                // a request may have slipped in between the check and the release
                if (ReferenceEquals(Volatile.Read(ref _wantedCurve), done) || Interlocked.CompareExchange(ref _designing, 1, 0) != 0) return;
                continue;
            }
            try { Volatile.Write(ref _kernel, _designer.Design(want)); }
            catch { /* keep the previous filter rather than take the audio down */ }
            done = want;
        }
    }

    /// <summary>Blocks until the EQ filter for the current settings has been built (used by offline tests).</summary>
    public void WaitForEq()
    {
        while (Volatile.Read(ref _designing) != 0) Thread.Sleep(1);
    }

    /// <summary>The EQ filter's impulse response as currently built (empty when flat).</summary>
    internal double[] EqTaps => Volatile.Read(ref _kernel).Taps;

    /// <summary>Thread-safe: test-tone generator settings.</summary>
    public void SetSweep(SweepConfig cfg) => Volatile.Write(ref _sweep, cfg);

    /// <summary>Optional final gain (linear, 0…1). Unused while Windows applies the listening volume itself.</summary>
    public void SetVolume(double linear) => Volatile.Write(ref _volTarget, Math.Clamp(linear, 0.0, 1.0));

    public void Process(Span<double> l, Span<double> r)
    {
        int n = l.Length;
        if (n == 0) return;
        double inv = 1.0 / n;

        var c = Volatile.Read(ref _pending);
        var prev = _cur;
        bool changed = !ReferenceEquals(c, prev);
        if (changed)
        {
            _fastSamples = (int)(_fs * 2);      // re-learn levels quickly after any control moves
            _overHold = Math.Min(_overHold, 1.5);
        }

        // 0. test tone replaces the incoming audio
        bool toneActive = GenerateSweep(l, r);

        // 1. meter, loudness of the unprocessed signal, preamp
        double ig = _inGain, dig = (c.InGain - ig) * inv;
        double inPeak = 0;
        for (int i = 0; i < n; i++)
        {
            double p = Math.Max(Math.Abs(l[i]), Math.Abs(r[i]));
            if (p > inPeak) inPeak = p;
        }
        double pInBlock = c.AutoLevel ? (KPower(l, ref _kInL1, ref _kInL2) + KPower(r, ref _kInR1, ref _kInR2)) * inv : 0;
        if (ig != 1 || dig != 0)
        {
            for (int i = 0; i < n; i++) { ig += dig; l[i] *= ig; r[i] *= ig; }
        }
        _inGain = c.InGain;

        // 2. EQ
        _conv.Process(l, r, Volatile.Read(ref _kernel));

        // 3. the effects, in the order a signal would meet them: shaping, the amplifier, the recorder, the room
        _soften.Process(l, r, c.Transient);
        _deharsh.Process(l, r, c.Deharsh, c.DeharshHz);
        _bass.Process(l, r, c.Bass, c.BassHz);
        _tube.Process(l, r, prev.Tube, c.Tube);
        _tape.Process(l, r, prev.Tape, c.Tape);
        _glue.Process(l, r, c.Glue);
        _reverb.Process(l, r, c.Space, c.SpaceDecay);

        // 4. output trim: manual, or automatic
        double target = c.AutoLevel ? AutoLevelGain(l, r, pInBlock, toneActive) : c.OutGain;
        if (!c.AutoLevel) _autoDb = 0;
        double og = _outGain, dog = (target - og) * inv;
        if (og != 1 || dog != 0)
        {
            for (int i = 0; i < n; i++) { og += dog; l[i] *= og; r[i] *= og; }
        }
        _outGain = target;

        // 5. limiter
        _limiter.Process(l, r);

        // 6. meter + analyser tap, optional final gain
        double outPeak = 0;
        double volTarget = Volatile.Read(ref _volTarget);
        double vpost = _vol, dvpost = (volTarget - vpost) * inv;
        int si = _spectrumIdx;
        var spec = _spectrum;
        for (int i = 0; i < n; i++)
        {
            double xl = l[i], xr = r[i];
            double p = Math.Max(Math.Abs(xl), Math.Abs(xr));
            if (p > outPeak) outPeak = p;
            spec[si] = (float)((xl + xr) * 0.5);
            si = (si + 1) & 32767;
            vpost += dvpost;
            l[i] = xl * vpost; r[i] = xr * vpost;
        }
        Volatile.Write(ref _spectrumIdx, si);
        _vol = volTarget;

        if (inPeak > _inPeak) _inPeak = (float)inPeak;
        if (outPeak > _outPeak) _outPeak = (float)outPeak;
        float mg = (float)_limiter.TakeMinGain();
        if (mg < _minGain) _minGain = mg;

        _cur = c;
    }

    /// <summary>
    /// Works out the output gain that (a) makes the processed sound as loud as the unprocessed sound, so that EQ and
    /// valve changes alter the tone rather than the volume, and (b) backs off just enough that peaks stay under full
    /// scale, so the limiter is left with nothing to do.
    /// </summary>
    double AutoLevelGain(Span<double> l, Span<double> r, double pInBlock, bool toneActive)
    {
        int n = l.Length;
        double dt = n / _fs;
        double pOutBlock = (KPower(l, ref _kOutL1, ref _kOutL2) + KPower(r, ref _kOutR1, ref _kOutR2)) / n;
        bool fast = _fastSamples > 0;
        if (fast) _fastSamples -= n;

        // A pure tone would make "match the loudness" mean "undo the EQ at that frequency", so levels are frozen
        // while the test tone plays; and silence teaches nothing.
        if (!toneActive && pInBlock > 1e-6 && pOutBlock > 1e-12)
        {
            double alpha = 1 - Math.Exp(-dt / (fast ? 0.35 : 5.0));
            _pIn += (pInBlock - _pIn) * alpha;
            _pOut += (pOutBlock - _pOut) * alpha;
            _match = Math.Clamp(Math.Sqrt(_pIn / _pOut), 0.063, 4.0);      // −24 … +12 dB
        }

        if (!toneActive)
        {
            double peak = 0;
            for (int i = 0; i < n; i++)
            {
                double p = Math.Max(Math.Abs(l[i]), Math.Abs(r[i]));
                if (p > peak) peak = p;
            }
            double overDb = Math.Max(0, 20 * Math.Log10(Math.Max(peak * _match, 1e-9)));
            if (overDb >= _overDb)
            {
                _overDb = overDb;                   // back off at once (the limiter covers this first instant)
                _overHold = 30;                     // …and stay there while peaks like this keep coming
            }
            else if ((_overHold -= dt) <= 0)
            {
                _overDb = Math.Max(overDb, _overDb - 0.5 * dt);    // then ease back up, half a dB per second
            }
        }

        double gain = _match * Math.Pow(10, -_overDb / 20.0);
        _autoDb = (float)(20 * Math.Log10(gain));
        return gain;
    }

    /// <summary>Mean-square-ready sum of the loudness-weighted signal; leaves <paramref name="x"/> untouched.</summary>
    double KPower(ReadOnlySpan<double> x, ref BiquadState s1, ref BiquadState s2)
    {
        double a0 = _kShelf.B0, a1 = _kShelf.B1, a2 = _kShelf.B2, a3 = _kShelf.A1, a4 = _kShelf.A2;
        double h0 = _kHp.B0, h1 = _kHp.B1, h2 = _kHp.B2, h3 = _kHp.A1, h4 = _kHp.A2;
        double z1 = s1.Z1, z2 = s1.Z2, y1 = s2.Z1, y2 = s2.Z2, sum = 0;
        for (int i = 0; i < x.Length; i++)
        {
            double v = x[i];
            double u = a0 * v + z1; z1 = a1 * v - a3 * u + z2; z2 = a2 * v - a4 * u;
            double w = h0 * u + y1; y1 = h1 * u - h3 * w + y2; y2 = h2 * u - h4 * w;
            sum += w * w;
        }
        s1.Z1 = z1; s1.Z2 = z2; s2.Z1 = y1; s2.Z2 = y2;
        s1.Flush(); s2.Flush();
        return sum;
    }

    /// <summary>Overwrites the block with the test tone while it is on (or fading out). Returns whether it did.</summary>
    bool GenerateSweep(Span<double> l, Span<double> r)
    {
        var cfg = Volatile.Read(ref _sweep);
        if (!cfg.On && _swGain <= 0)
        {
            _sweepHz = 0;
            return false;
        }
        if (cfg.On && _swGain <= 0 && !cfg.Hold) _swPos = 0;        // a fresh start begins at 20 Hz

        double amp = Math.Pow(10, Math.Clamp(cfg.LevelDb, -60, -3) / 20.0);
        double gainStep = (cfg.On ? 1 : -1) / (_fs * 0.03);        // 30 ms fade in/out, no clicks
        double posStep = cfg.Hold || !cfg.On ? 0 : 1.0 / (Math.Clamp(cfg.Seconds, 1, 600) * _fs);
        double top = Math.Min(20000.0, _fs * 0.45);
        double span = Math.Log(top / 20.0);
        double twoPiOverFs = 2 * Math.PI / _fs;
        // dragged by hand: head for the requested frequency, quickly but without a jump
        double manual = cfg.On && cfg.Hold && cfg.ManualHz > 0 ? Math.Clamp(Math.Log(cfg.ManualHz / 20.0) / span, 0, 1) : -1;
        double glide = Fx.Coef(12, _fs);
        double phase = _swPhase, pos = _swPos, gain = _swGain, hz = 20;
        for (int i = 0; i < l.Length; i++)
        {
            gain = Math.Clamp(gain + gainStep, 0, 1);
            hz = 20.0 * Math.Exp(span * pos);
            phase += twoPiOverFs * hz;
            if (phase > 2 * Math.PI) phase -= 2 * Math.PI;
            double v = amp * gain * Math.Sin(phase);
            l[i] = v; r[i] = v;
            if (manual >= 0) pos += (manual - pos) * glide;
            else pos += posStep;
            if (pos >= 1) pos = manual >= 0 ? 1 : 0;
        }
        _swPhase = phase; _swPos = pos; _swGain = gain;
        _sweepHz = (float)hz;
        return true;
    }

    // ---- UI-side readouts ----

    public float TakeInputPeak() => Interlocked.Exchange(ref _inPeak, 0);
    public float TakeOutputPeak() => Interlocked.Exchange(ref _outPeak, 0);
    /// <summary>Deepest limiter gain since last read (1 = idle).</summary>
    public float TakeLimiterGain() => Interlocked.Exchange(ref _minGain, 1);
    /// <summary>Deepest de-harsh cut since last read, in dB.</summary>
    public double TakeDeharshDb() => _deharsh.TakeMaxCut();
    /// <summary>Deepest glue-compressor gain reduction since last read, in dB.</summary>
    public double TakeGlueDb() => _glue.TakeMaxReduction();
    /// <summary>Gain the auto level is currently applying, in dB (0 when off).</summary>
    public float AutoGainDb => _autoDb;
    /// <summary>Current test-tone frequency, or 0 when the tone is off.</summary>
    public float SweepHz => _sweepHz;

    /// <summary>Copies the most recent processed samples (mono mix) for the spectrum display.</summary>
    public void CopyRecent(Span<float> dst)
    {
        int end = Volatile.Read(ref _spectrumIdx);
        int start = end - dst.Length;
        for (int i = 0; i < dst.Length; i++) dst[i] = _spectrum[(start + i) & 32767];
    }
}
