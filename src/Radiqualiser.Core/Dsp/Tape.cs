using System.Runtime.CompilerServices;

namespace Radiqualiser.Core.Dsp;

internal sealed class TapeCompiled
{
    public bool Enabled;
    public double Gain, Wet, Dry;       // overall saturation curve
    public double TrebleGain, TrebleMix; // treble squash: drive into its own curve, and how much of it is blended in
    public double Wow;                  // 0…1
}

/// <summary>
/// The maths of the tape model. Two independent things happen.
///
/// Drive: tape saturates gently and symmetrically (mostly 3rd harmonic) as the whole signal gets loud.
///
/// Treble: tape runs out of headroom in the treble long before the bass. That is what makes tape "soft": a loud
/// cymbal crash or a sibilant "s" is squashed, while quieter treble passes untouched. It is modelled by splitting off
/// everything above about 4.5 kHz, passing just that through its own saturation curve with a much lower ceiling, and
/// putting it back. The control sets how low that ceiling is, from full scale (no effect) down to about −44 dB (a
/// blanket over the tweeters). It works whatever Drive is set to, including zero.
///
/// The split is a Linkwitz–Riley crossover: a low band and a high band that are each properly free of the other's
/// frequencies, and that add back together to a flat response (with a phase rotation around the crossover, which is
/// inaudible). A simpler "signal minus filtered signal" split does not work here: the leftover still contains most of
/// the treble, so squashing the split-off part achieves almost nothing.
/// </summary>
public static class TapeMath
{
    public const double MinDriveDb = -24, MaxDriveDb = 18;
    public const double TrebleSplitHz = 4500, MaxTrebleDriveDb = 44;
    const double RefLevel = 0.25;

    /// <summary>The saturation curve: x / √(1 + x²). A softer knee than the valve's tanh.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double Curve(double x) => x / Math.Sqrt(1 + x * x);

    internal static TapeCompiled Compile(TapeParams p, double fs)
    {
        double d = Math.Clamp(p.Drive, 0, 1);
        double db = MinDriveDb + (MaxDriveDb - MinDriveDb) * d;
        double g = Math.Pow(10, db / 20.0);
        double mix = Math.Clamp(d / 0.07, 0, 1);                 // the first few percent fade the curve in from clean
        double makeup = RefLevel / Curve(g * RefLevel);         // unity at the reference level
        double t = Math.Clamp(p.Squash, 0, 1);
        return new TapeCompiled
        {
            Enabled = p.Enabled,
            Gain = g,
            Wet = mix * makeup,
            Dry = 1 - mix,
            TrebleGain = Math.Pow(10, MaxTrebleDriveDb * Math.Pow(t, 0.75) / 20.0),
            TrebleMix = Math.Clamp(t / 0.05, 0, 1),
            Wow = p.Enabled ? Math.Clamp(p.Wow, 0, 1) : 0,
        };
    }
}

internal struct TapeShaper : IShaper
{
    double _g, _wet, _dry, _tg, _tm, _dg, _dwet, _ddry, _dtg, _dtm;
    double _a;
    /// <summary>Band-split filter memory (carried between blocks).</summary>
    public double S1, S2, S3;

    public static TapeShaper Create(TapeCompiled from, TapeCompiled to, int n, double splitCoef, double s1, double s2, double s3)
    {
        double inv = 1.0 / Math.Max(1, n);
        return new TapeShaper
        {
            _g = from.Gain, _wet = from.Wet, _dry = from.Dry, _tg = from.TrebleGain, _tm = from.TrebleMix,
            _dg = (to.Gain - from.Gain) * inv, _dwet = (to.Wet - from.Wet) * inv, _ddry = (to.Dry - from.Dry) * inv,
            _dtg = (to.TrebleGain - from.TrebleGain) * inv, _dtm = (to.TrebleMix - from.TrebleMix) * inv,
            _a = splitCoef, S1 = s1, S2 = s2, S3 = s3,
        };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Next() { _g += _dg; _wet += _dwet; _dry += _ddry; _tg += _dtg; _tm += _dtm; }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public double Shape(double u)
    {
        // crossover: low = two low-pass stages, high = two high-pass stages (each 12 dB/octave)
        // (trapezoidal one-pole stages: with these the two bands add back to an exactly flat response)
        double t = (u - S1) * _a; double lp = t + S1; S1 = lp + t;
        double h = u - lp;
        t = (lp - S2) * _a; double low = t + S2; S2 = low + t;      // low band
        t = (h - S3) * _a; double hl = t + S3; S3 = hl + t;
        h -= hl;                                                    // high band

        // the treble's own, much lower, ceiling; then the bands go back together (high inverted, as this crossover needs)
        double hs = h * _tg;
        double v = low - (h + _tm * (hs / Math.Sqrt(1 + hs * hs) / _tg - h));

        // then the whole signal onto the tape
        return _dry * v + _wet * TapeMath.Curve(_g * v);
    }
}

/// <summary>Tape: oversampled treble squash and saturation → wow and flutter.</summary>
internal sealed class TapeSection
{
    readonly Oversampler _os;
    readonly DryPath _dry;
    readonly WowFlutter _wow;
    readonly double _splitCoef;
    bool _active;
    double _l1, _l2, _l3, _r1, _r2, _r3;

    public int LatencySamples => _os.DelaySamples;

    public TapeSection(double fs)
    {
        _os = new Oversampler(fs);
        _dry = new DryPath(_os.DelaySamples);
        _wow = new WowFlutter(fs);
        double g = Math.Tan(Math.PI * TapeMath.TrebleSplitHz / (fs * _os.Factor));
        _splitCoef = g / (1 + g);
    }

    public void Process(Span<double> l, Span<double> r, TapeCompiled from, TapeCompiled to)
    {
        int n = l.Length;
        bool was = _active;
        _active = to.Enabled;

        _dry.Capture(l, r);
        if (!was && !_active)
        {
            _dry.CopyTo(l, r);
        }
        else
        {
            if (!was) _l1 = _l2 = _l3 = _r1 = _r2 = _r3 = 0;
            var src = was ? from : to;
            var sl = TapeShaper.Create(src, to, n, _splitCoef, _l1, _l2, _l3);
            _os.Process(l, 0, ref sl);
            _l1 = Fx.Tiny(sl.S1); _l2 = Fx.Tiny(sl.S2); _l3 = Fx.Tiny(sl.S3);
            var sr = TapeShaper.Create(src, to, n, _splitCoef, _r1, _r2, _r3);
            _os.Process(r, 1, ref sr);
            _r1 = Fx.Tiny(sr.S1); _r2 = Fx.Tiny(sr.S2); _r3 = Fx.Tiny(sr.S3);
            if (was != _active) _dry.Crossfade(l, r, _active);
        }

        _wow.Process(l, r, to.Wow);
    }
}

/// <summary>
/// Wow and flutter: the slow drift (wow, around once a second) and fast shimmer (flutter, around ten times a second)
/// in pitch that come from a tape or turntable not turning at a perfectly steady speed. Done the way the real thing
/// does it: the audio is read back through a delay whose length wanders.
/// </summary>
internal sealed class WowFlutter
{
    const int Taps = 16, Half = 8, Phases = 256;
    static readonly float[] Table = BuildTable();

    readonly double _fs;
    readonly double[] _bufL, _bufR;
    readonly int _mask;
    int _w;
    double _amt, _xf;
    double _ph1, _ph2, _ph3, _drift, _flNoise;
    readonly double _wowMax, _flutterMax, _aAmt, _aDrift, _aFl, _driftScale, _flScale;
    uint _rng = 0x9E3779B9;

    public WowFlutter(double fs)
    {
        _fs = fs;
        int size = 1;
        while (size < fs * 0.06) size <<= 1;
        _bufL = new double[size]; _bufR = new double[size];
        _mask = size - 1;
        _wowMax = 0.040 / (2 * Math.PI * 0.7) * fs;      // ±4 % pitch at the main wow rate: unmistakable
        _flutterMax = 0.010 / (2 * Math.PI * 9.0) * fs;  // ±1 % at the flutter rate
        _aAmt = Fx.Coef(150, fs);
        _aDrift = 2 * Math.PI * 0.35 / fs;
        _aFl = 2 * Math.PI * 14.0 / fs;
        _driftScale = Math.Sqrt((2 - _aDrift) / _aDrift) * 0.5;   // brings filtered noise back up to a useful size
        _flScale = Math.Sqrt((2 - _aFl) / _aFl) * 0.5;
    }

    static float[] BuildTable()
    {
        var t = new float[(Phases + 1) * Taps];
        const double beta = 7.0, cutoff = 0.92;
        double i0b = Window.BesselI0(beta);
        var row = new double[Taps];
        for (int p = 0; p <= Phases; p++)
        {
            double f = p / (double)Phases, sum = 0;
            for (int k = 0; k < Taps; k++)
            {
                double x = (k - Half + 1) - f;
                double r = x / Half;
                double w = Math.Abs(r) < 1 ? Window.BesselI0(beta * Math.Sqrt(1 - r * r)) / i0b : 0;
                row[k] = cutoff * Window.Sinc(cutoff * x) * w;
                sum += row[k];
            }
            for (int k = 0; k < Taps; k++) t[p * Taps + k] = (float)(row[k] / sum);
        }
        return t;
    }

    double Noise()
    {
        _rng ^= _rng << 13; _rng ^= _rng >> 17; _rng ^= _rng << 5;
        return _rng * (2.0 / uint.MaxValue) - 1.0;
    }

    public void Process(Span<double> l, Span<double> r, double amount)
    {
        bool wanted = amount > 0;
        bool idle = !wanted && _amt < 1e-4 && _xf <= 0;
        double[] bl = _bufL, br = _bufR;
        int mask = _mask, w = _w;

        if (idle)
        {
            // keep the history current so switching on later starts from real audio
            for (int i = 0; i < l.Length; i++) { bl[w] = l[i]; br[w] = r[i]; w = (w + 1) & mask; }
            _w = w; _amt = 0;
            return;
        }

        double w1 = 2 * Math.PI * 0.55 / _fs, w2 = 2 * Math.PI * 1.37 / _fs, w3 = 2 * Math.PI * 9.3 / _fs;
        ReadOnlySpan<float> table = Table;
        for (int i = 0; i < l.Length; i++)
        {
            double xl = l[i], xr = r[i];
            bl[w] = xl; br[w] = xr;

            _amt += (amount - _amt) * _aAmt;
            _xf = Math.Clamp(_xf + (wanted || _amt > 1e-4 ? 1 : -1) / 256.0, 0, 1);
            double s = _amt * _amt;                                         // squared: 25 % ≈ a good deck, 50 % ≈ ±1 %, 100 % ≈ ±4 %

            _ph1 += w1; if (_ph1 > 2 * Math.PI) _ph1 -= 2 * Math.PI;
            _ph2 += w2; if (_ph2 > 2 * Math.PI) _ph2 -= 2 * Math.PI;
            _ph3 += w3; if (_ph3 > 2 * Math.PI) _ph3 -= 2 * Math.PI;
            _drift += (Noise() * _driftScale - _drift) * _aDrift;
            _flNoise += (Noise() * _flScale - _flNoise) * _aFl;
            double wow = 0.62 * Math.Sin(_ph1) + 0.22 * Math.Sin(_ph2) + 0.16 * Math.Clamp(_drift, -1, 1);
            double flutter = 0.6 * Math.Sin(_ph3) + 0.4 * Math.Clamp(_flNoise, -1, 1);
            double span = _wowMax + _flutterMax;
            double delay = Half + s * (span * 1.05 + _wowMax * wow + _flutterMax * flutter);

            double pos = w - delay;
            double fl = Math.Floor(pos);
            double ph = (pos - fl) * Phases;
            int p0 = (int)ph;
            float a = (float)(ph - p0);
            int start = (int)fl - Half + 1;
            int r0 = p0 * Taps, r1 = r0 + Taps;
            double yl = 0, yr = 0;
            for (int k = 0; k < Taps; k++)
            {
                double c = table[r0 + k] + a * (table[r1 + k] - table[r0 + k]);
                int idx = (start + k) & mask;
                yl += c * bl[idx]; yr += c * br[idx];
            }

            l[i] = xl + _xf * (yl - xl);
            r[i] = xr + _xf * (yr - xr);
            w = (w + 1) & mask;
        }
        _w = w;
    }
}
