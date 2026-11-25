using System.Numerics.Tensors;
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Radiqualiser.Check")]

namespace Radiqualiser.Core.Dsp;

/// <summary>Per-sample constants of the valve transfer curve.</summary>
public struct TubeCoefs
{
    public double Gain;      // drive into the curve
    public double Bias;      // operating-point offset: 0 = symmetric (odd harmonics), >0 = asymmetric (adds even harmonics)
    public double TanhBias;  // tanh(Bias), cached
    public double Wet;       // mix * makeup / small-signal slope
    public double Dry;       // 1 - mix
    public double Beta;      // negative feedback around the curve (0 = none … 9 = 20 dB)

    /// <summary>The static (memoryless) valve curve.</summary>
    public readonly double Shape(double x)
    {
        if (Beta <= 1e-9) return Dry * x + Wet * (Math.Tanh(Gain * x + Bias) - TanhBias);
        double norm = 1 / (1 - TanhBias * TanhBias);
        double y = TubeMath.SolveFeedback(Gain * (1 + Beta) * x, Beta, Bias, TanhBias, norm, 0, 40);
        return Dry * x + Wet * (y / norm);
    }
}

internal sealed class TubeCompiled
{
    public bool Enabled;
    public TubeCoefs Coefs;
    public BiquadCoeffs Pre, De, Bloom;
    public double HfA;
}

/// <summary>
/// The maths behind the valve model, shared by the audio path and the UI's harmonic display.
///
/// The curve is y = tanh(g·x + b) − tanh(b), rescaled so that small signals pass at unity gain.
///  • With b = 0 it is symmetric: it squashes both half-waves equally and adds only odd harmonics (3rd, 5th…),
///    like a well-balanced push-pull output stage.
///  • With b &gt; 0 one half-wave is squashed sooner than the other, which adds even harmonics (mostly the 2nd, an
///    octave up), like a single-ended triode.
/// Drive sets how far up the curve the music reaches; a loudness-matched makeup gain keeps the level steady so that
/// turning Drive up changes the tone rather than the volume.
///
/// Feedback wraps the curve in a negative-feedback loop, as most valve amps do: the output is compared with the input
/// and the error is what gets amplified. Below clipping this straightens the curve (less distortion, tighter sound);
/// but once the stage runs out of headroom the loop can no longer correct, so clipping arrives abruptly instead of
/// gradually. No feedback = loose and rich; heavy feedback = clean, then suddenly hard.
/// </summary>
public static class TubeMath
{
    /// <summary>
    /// Drive range. Music is normally mastered right up to full scale, so 0 dB here is already heavy: a full-scale
    /// peak reaches well into the bend of the curve. A real hi-fi valve amp in normal use sits around −20 … −10 dB
    /// (a fraction of a percent to a few percent distortion); the top of the range is guitar-amp territory.
    /// </summary>
    public const double MinDriveDb = -30, MaxDriveDb = 24;
    const double MaxBias = 0.65;
    const double RefLevel = 0.25;   // level at which the makeup gain holds loudness constant
    const double MaxFeedbackDb = 20;

    public static TubeCoefs Coefs(TubeParams p)
    {
        double db = Math.Clamp(p.DriveDb, MinDriveDb, MaxDriveDb);
        double g = Math.Pow(10, db / 20.0);
        double b = MaxBias * Math.Clamp(p.Character, 0, 1);
        double tb = Math.Tanh(b);
        double slope = g * (1 - tb * tb);                    // small-signal gain of tanh(g·x + b)
        double makeup = g * RefLevel / Math.Tanh(RefLevel * g); // → 1 at low drive, → loudness-matched at high drive
        // The first 3 dB of the range fade the curve in, so the control's minimum is exactly "no effect".
        double mix = Math.Clamp(p.Mix, 0, 1) * Math.Clamp((db - MinDriveDb) / 3.0, 0, 1);
        double beta = Math.Pow(10, MaxFeedbackDb * Math.Clamp(p.Feedback, 0, 1) / 20.0) - 1;
        return new TubeCoefs { Gain = g, Bias = b, TanhBias = tb, Wet = mix * makeup / slope, Dry = 1 - mix, Beta = beta };
    }

    /// <summary>
    /// Solves y = φ(a − β·y) for y, where φ is the valve curve normalised to unit slope: the output of the stage with
    /// its feedback loop closed. Newton's method, kept inside a shrinking bracket so it cannot run away.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static double SolveFeedback(double a, double beta, double bias, double tb, double norm, double y, int maxIter)
    {
        double lo = -(1 + tb) * norm, hi = (1 - tb) * norm;     // φ's range
        if (y < lo) y = lo; else if (y > hi) y = hi;
        for (int i = 0; i < maxIter; i++)
        {
            double th = Math.Tanh(a - beta * y + bias);
            double h = y - (th - tb) * norm;
            if (Math.Abs(h) < 1e-10) break;
            if (h > 0) hi = y; else lo = y;
            double next = y - h / (1 + beta * (1 - th * th) * norm);
            y = next > lo && next < hi ? next : 0.5 * (lo + hi);
        }
        return y;
    }

    internal static TubeCompiled Compile(TubeParams p, double fs)
    {
        double t = Math.Clamp(p.Transformer, 0, 1);
        double bloom = Math.Clamp(p.Bloom, 0, 1);
        double shelfDb = 12.0 * t;                       // bass is pushed harder into the curve, then restored
        // Treble roll-off corner: 6 kHz at full, rising without limit (fully transparent) as the control approaches 0.
        double hfA = t < 1e-4 ? 1.0 : 1 - Math.Exp(-2 * Math.PI * (6000.0 / Math.Pow(t, 1.5)) / fs);
        return new TubeCompiled
        {
            Enabled = p.Enabled,
            Coefs = Coefs(p),
            Pre = BiquadCoeffs.Design(BandType.LowShelf, 110, shelfDb, 0.707, fs),
            De = BiquadCoeffs.Design(BandType.LowShelf, 110, -shelfDb, 0.707, fs),
            Bloom = BiquadCoeffs.Design(BandType.Peak, 70, 6.0 * bloom, 0.9, fs),
            HfA = hfA,
        };
    }

    /// <summary>
    /// Harmonic levels (dB relative to the fundamental) that the valve curve adds to a pure sine at the given level.
    /// Index 0 is the 2nd harmonic.
    /// </summary>
    public static double[] Harmonics(TubeParams p, double levelDb, int count)
    {
        var c = Coefs(p);
        const int n = 2048;
        double amp = Math.Pow(10, levelDb / 20.0);
        var re = new double[count + 2];
        var im = new double[count + 2];
        for (int i = 0; i < n; i++)
        {
            double ph = 2 * Math.PI * i / n;
            double y = c.Shape(amp * Math.Sin(ph));
            for (int k = 1; k <= count + 1; k++)
            {
                re[k] += y * Math.Cos(k * ph);
                im[k] += y * Math.Sin(k * ph);
            }
        }
        double fund = Math.Sqrt(re[1] * re[1] + im[1] * im[1]);
        var result = new double[count];
        for (int k = 2; k <= count + 1; k++)
        {
            double m = Math.Sqrt(re[k] * re[k] + im[k] * im[k]);
            result[k - 2] = fund > 0 && m > 0 ? Math.Max(-160, 20 * Math.Log10(m / fund)) : -160;
        }
        return result;
    }
}

/// <summary>A curve the <see cref="Oversampler"/> can run. A struct, so the JIT inlines it into the inner loop.</summary>
internal interface IShaper
{
    /// <summary>Called once per input sample: advance any parameter ramps.</summary>
    void Next();
    /// <summary>The curve itself, called at the oversampled rate.</summary>
    double Shape(double u);
}

/// <summary>The valve curve with per-sample parameter ramps.</summary>
internal struct ValveShaper : IShaper
{
    double _g, _b, _wet, _dry, _tb, _beta, _norm;
    double _dg, _db, _dwet, _ddry, _dbeta;
    bool _biasMoving;
    /// <summary>Last solved output of the feedback loop (carried between blocks as the next starting guess).</summary>
    public double Y;

    public static ValveShaper Create(in TubeCoefs from, in TubeCoefs to, int n, double y)
    {
        double inv = 1.0 / Math.Max(1, n);
        return new ValveShaper
        {
            _g = from.Gain, _b = from.Bias, _wet = from.Wet, _dry = from.Dry, _tb = from.TanhBias, _beta = from.Beta,
            _norm = 1 / (1 - from.TanhBias * from.TanhBias),
            _dg = (to.Gain - from.Gain) * inv, _db = (to.Bias - from.Bias) * inv, _dwet = (to.Wet - from.Wet) * inv,
            _ddry = (to.Dry - from.Dry) * inv, _dbeta = (to.Beta - from.Beta) * inv,
            _biasMoving = to.Bias != from.Bias,
            Y = y,
        };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Next()
    {
        _g += _dg; _wet += _dwet; _dry += _ddry; _beta += _dbeta;
        if (_biasMoving) { _b += _db; _tb = Math.Tanh(_b); _norm = 1 / (1 - _tb * _tb); }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public double Shape(double u)
    {
        if (_beta <= 1e-9) return _dry * u + _wet * (Math.Tanh(_g * u + _b) - _tb);
        // Oversampled audio changes little from one sample to the next, so the previous answer is an excellent first
        // guess and a couple of iterations suffice.
        Y = TubeMath.SolveFeedback(_g * (1 + _beta) * u, _beta, _b, _tb, _norm, Y, 8);
        return _dry * u + _wet * (Y / _norm);
    }
}

/// <summary>
/// Runs a curve at 2–8× the sample rate. A curve like this creates harmonics well above the audio band; at the normal
/// sample rate those would fold back down as harsh, unmusical tones (aliasing), so the signal is interpolated up,
/// shaped, then filtered and brought back down. Linear-phase filters; total delay is a whole number of samples.
/// </summary>
internal sealed class Oversampler
{
    readonly int _l, _n, _k;
    readonly double[] _h;
    readonly double[][] _phase;
    readonly double[][] _ih = new double[2][];
    readonly double[][] _dh = new double[2][];
    readonly int[] _iw = new int[2];
    readonly int[] _dw = new int[2];

    public int DelaySamples { get; }
    public int Factor => _l;

    public Oversampler(double fs)
    {
        _l = fs < 60000 ? 8 : fs < 120000 ? 4 : 2;
        _h = DesignLowpass(_l, fs, out int k);
        _n = _h.Length;
        _k = k + 1;
        DelaySamples = k;

        _phase = new double[_l][];
        for (int p = 0; p < _l; p++)
        {
            _phase[p] = new double[_k];
            for (int j = 0; j < _k; j++)
            {
                int idx = p + j * _l;
                _phase[p][j] = idx < _n ? _l * _h[idx] : 0;
            }
        }
        for (int ch = 0; ch < 2; ch++)
        {
            _ih[ch] = new double[2 * _k];
            _dh[ch] = new double[2 * _n];
        }
    }

    static double[] DesignLowpass(int l, double fs, out int k)
    {
        double fpass = Math.Min(20000.0, 0.45 * fs);
        double fstop = fs - fpass;              // anything above this would fold back below fpass
        double hi = l * fs;
        double df = (fstop - fpass) / hi;
        const double atten = 100.0;
        double beta = 0.1102 * (atten - 8.7);
        int n = (int)Math.Ceiling((atten - 7.95) / (14.36 * df)) + 1;
        k = (int)Math.Ceiling((n - 1) / (double)l);
        if ((k * l) % 2 != 0) k++;
        n = k * l + 1;
        double fc = 0.5 * (fpass + fstop) / hi;
        double m = (n - 1) / 2.0;
        var h = new double[n];
        double sum = 0;
        double i0b = Window.BesselI0(beta);
        for (int i = 0; i < n; i++)
        {
            double t = i - m;
            double r = t / m;
            double w = Window.BesselI0(beta * Math.Sqrt(Math.Max(0, 1 - r * r))) / i0b;
            h[i] = 2 * fc * Window.Sinc(2 * fc * t) * w;
            sum += h[i];
        }
        for (int i = 0; i < n; i++) h[i] /= sum;
        return h;
    }

    public void Process<T>(Span<double> x, int ch, ref T shaper) where T : struct, IShaper
    {
        double[] ih = _ih[ch], dh = _dh[ch];
        int iw = _iw[ch], dw = _dw[ch];
        int k = _k, nn = _n, l = _l;
        ReadOnlySpan<double> h = _h;

        for (int i = 0; i < x.Length; i++)
        {
            shaper.Next();

            iw = (iw == 0 ? k : iw) - 1;
            ih[iw] = ih[iw + k] = x[i];
            ReadOnlySpan<double> hist = ih.AsSpan(iw, k);

            // Phase 0 lands exactly on the input sample's time, so decimating right after it keeps the delay integral.
            double v = shaper.Shape(TensorPrimitives.Dot(_phase[0], hist));
            dw = (dw == 0 ? nn : dw) - 1;
            dh[dw] = dh[dw + nn] = v;
            x[i] = TensorPrimitives.Dot(h, dh.AsSpan(dw, nn));

            for (int p = 1; p < l; p++)
            {
                v = shaper.Shape(TensorPrimitives.Dot(_phase[p], hist));
                dw = (dw == 0 ? nn : dw) - 1;
                dh[dw] = dh[dw + nn] = v;
            }
        }
        _iw[ch] = iw; _dw[ch] = dw;
    }
}

/// <summary>
/// The clean signal, delayed to line up with a processed path that has latency, so a section can be switched in and
/// out (with a short crossfade) without the timing ever shifting.
/// </summary>
internal sealed class DryPath
{
    readonly double[] _d0, _d1;
    int _idx;
    double[] _l = Array.Empty<double>(), _r = Array.Empty<double>();

    public DryPath(int delaySamples)
    {
        _d0 = new double[Math.Max(1, delaySamples)];
        _d1 = new double[Math.Max(1, delaySamples)];
    }

    /// <summary>Feeds the block in and makes the delayed copy available through <see cref="L"/> / <see cref="R"/>.</summary>
    public void Capture(ReadOnlySpan<double> l, ReadOnlySpan<double> r)
    {
        int n = l.Length;
        if (_l.Length < n) { _l = new double[n]; _r = new double[n]; }
        int di = _idx, dn = _d0.Length;
        for (int i = 0; i < n; i++)
        {
            _l[i] = _d0[di]; _d0[di] = l[i];
            _r[i] = _d1[di]; _d1[di] = r[i];
            if (++di == dn) di = 0;
        }
        _idx = di;
    }

    public Span<double> L(int n) => _l.AsSpan(0, n);
    public Span<double> R(int n) => _r.AsSpan(0, n);

    public void CopyTo(Span<double> l, Span<double> r)
    {
        L(l.Length).CopyTo(l);
        R(r.Length).CopyTo(r);
    }

    /// <summary>Blends across the block: towards the processed signal already in l/r, or away from it.</summary>
    public void Crossfade(Span<double> l, Span<double> r, bool towardsWet)
    {
        int n = l.Length;
        double step = 1.0 / n, f = 0;
        for (int i = 0; i < n; i++)
        {
            f += step;
            double wet = towardsWet ? f : 1 - f;
            l[i] = _l[i] + wet * (l[i] - _l[i]);
            r[i] = _r[i] + wet * (r[i] - _r[i]);
        }
    }
}

/// <summary>The whole valve-amp section: transformer emphasis → oversampled valve curve → de-emphasis → DC block → treble roll-off → bass bloom.</summary>
internal sealed class TubeSection
{
    readonly Oversampler _os;
    readonly DryPath _dry;
    bool _active;

    BiquadState _preL, _preR, _deL, _deR, _bloomL, _bloomR;
    double _dcXL, _dcYL, _dcXR, _dcYR, _hfL, _hfR, _fbL, _fbR;
    readonly double _dcR;

    public int LatencySamples => _os.DelaySamples;
    public int OversampleFactor => _os.Factor;

    public TubeSection(double fs)
    {
        _os = new Oversampler(fs);
        _dry = new DryPath(_os.DelaySamples);
        _dcR = 1 - 2 * Math.PI * 3.0 / fs;
    }

    public void Process(Span<double> l, Span<double> r, TubeCompiled from, TubeCompiled to)
    {
        int n = l.Length;
        bool was = _active;
        _active = to.Enabled;

        _dry.Capture(l, r);
        if (!was && !_active)
        {
            _dry.CopyTo(l, r);
            return;
        }

        if (!was)
        {
            _preL = _preR = _deL = _deR = _bloomL = _bloomR = default;
            _dcXL = _dcYL = _dcXR = _dcYR = _hfL = _hfR = _fbL = _fbR = 0;
        }
        var src = was ? from : to;

        Fx.Filter(src.Pre, to.Pre, ref _preL, l); Fx.Filter(src.Pre, to.Pre, ref _preR, r);
        var sl = ValveShaper.Create(src.Coefs, to.Coefs, n, _fbL);
        _os.Process(l, 0, ref sl); _fbL = sl.Y;
        var sr = ValveShaper.Create(src.Coefs, to.Coefs, n, _fbR);
        _os.Process(r, 1, ref sr); _fbR = sr.Y;
        Fx.Filter(src.De, to.De, ref _deL, l); Fx.Filter(src.De, to.De, ref _deR, r);

        double dcR = _dcR, a = src.HfA, da = (to.HfA - src.HfA) / n;
        double xl = _dcXL, yl = _dcYL, xr = _dcXR, yr = _dcYR, hl = _hfL, hr = _hfR;
        for (int i = 0; i < n; i++)
        {
            a += da;
            double v = l[i]; yl = v - xl + dcR * yl; xl = v; hl += a * (yl - hl); l[i] = hl;
            v = r[i]; yr = v - xr + dcR * yr; xr = v; hr += a * (yr - hr); r[i] = hr;
        }
        _dcXL = xl; _dcYL = Fx.Tiny(yl); _dcXR = xr; _dcYR = Fx.Tiny(yr); _hfL = Fx.Tiny(hl); _hfR = Fx.Tiny(hr);

        Fx.Filter(src.Bloom, to.Bloom, ref _bloomL, l); Fx.Filter(src.Bloom, to.Bloom, ref _bloomR, r);

        if (was != _active) _dry.Crossfade(l, r, _active);
    }
}

/// <summary>Small helpers shared by the effect sections.</summary>
internal static class Fx
{
    public static void Filter(in BiquadCoeffs from, in BiquadCoeffs to, ref BiquadState s, Span<double> x)
    {
        if (BiquadCoeffs.Same(from, to)) Biquad.Process(to, ref s, x);
        else Biquad.ProcessRamp(from, to, ref s, x);
    }

    public static double Tiny(double v) => Math.Abs(v) < 1e-25 ? 0 : v;

    /// <summary>One-pole smoothing coefficient for a time constant in milliseconds.</summary>
    public static double Coef(double ms, double fs) => 1 - Math.Exp(-1.0 / (fs * ms / 1000.0));

    public static double Db(double lin) => 20 * Math.Log10(Math.Max(lin, 1e-12));
    public static double Lin(double db) => Math.Pow(10, db / 20.0);
}

internal static class Window
{
    public static double Sinc(double x)
    {
        if (Math.Abs(x) < 1e-9) return 1;
        double px = Math.PI * x;
        return Math.Sin(px) / px;
    }

    public static double BesselI0(double x)
    {
        double sum = 1, term = 1, q = x * x / 4;
        for (int k = 1; k < 60; k++)
        {
            term *= q / (k * (double)k);
            sum += term;
            if (term < sum * 1e-17) break;
        }
        return sum;
    }
}
