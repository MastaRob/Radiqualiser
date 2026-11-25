namespace Radiqualiser.Core.Dsp;

/// <summary>
/// Transient softening: rounds off the leading edge of drum hits, plucks and consonants. Two level-followers watch the
/// music, one quick and one slow; when the quick one jumps ahead of the slow one, something has just started, and the
/// gain is dipped for those few milliseconds. The audio is delayed by 1.5 ms so the dip is already in place when the
/// attack arrives.
/// </summary>
internal sealed class TransientSoftener
{
    readonly double[] _dl, _dr;
    int _idx;
    double _fast = 1e-4, _slow = 1e-4, _gr, _amt;
    readonly double _aFast, _aSlow, _aRel, _aGrDown, _aGrUp, _aAmt;

    public int LatencySamples => _dl.Length;

    public TransientSoftener(double fs)
    {
        int d = Math.Max(4, (int)Math.Round(fs * 0.0015));
        _dl = new double[d]; _dr = new double[d];
        _aFast = Fx.Coef(0.4, fs);
        _aSlow = Fx.Coef(22, fs);
        _aRel = Fx.Coef(45, fs);
        _aGrDown = Fx.Coef(0.6, fs);
        _aGrUp = Fx.Coef(18, fs);
        _aAmt = Fx.Coef(20, fs);
    }

    public void Process(Span<double> l, Span<double> r, double amount)
    {
        double[] dl = _dl, dr = _dr;
        int idx = _idx, len = dl.Length;
        bool idle = amount <= 0 && _amt < 1e-4 && _gr > -0.01;
        if (idle)
        {
            for (int i = 0; i < l.Length; i++)
            {
                double xl = l[i], xr = r[i];
                l[i] = dl[idx]; r[i] = dr[idx];
                dl[idx] = xl; dr[idx] = xr;
                if (++idx == len) idx = 0;
            }
            _idx = idx; _amt = 0; _gr = 0;
            _fast = _slow = 1e-4;
            return;
        }

        double fast = _fast, slow = _slow, gr = _gr, amt = _amt;
        for (int i = 0; i < l.Length; i++)
        {
            double xl = l[i], xr = r[i];
            amt += (amount - amt) * _aAmt;
            double p = Math.Max(Math.Max(Math.Abs(xl), Math.Abs(xr)), 1e-4);
            fast += (p - fast) * (p > fast ? _aFast : _aRel);
            slow += (p - slow) * (p > slow ? _aSlow : _aRel);

            double lead = fast > slow ? 20 * Math.Log10(fast / slow) : 0;      // dB by which the attack leads
            double target = -Math.Min(14.0, 0.9 * lead) * amt;
            gr += (target - gr) * (target < gr ? _aGrDown : _aGrUp);
            double g = Math.Pow(10, gr / 20.0);

            l[i] = dl[idx] * g; r[i] = dr[idx] * g;
            dl[idx] = xl; dr[idx] = xr;
            if (++idx == len) idx = 0;
        }
        _idx = idx; _fast = fast; _slow = slow; _gr = gr; _amt = amt;
    }
}

/// <summary>
/// Dynamic de-harsh: a dip in the upper mids that only appears while that region is too prominent. It compares the
/// energy around the focus frequency with the energy of the whole signal; when the band's share goes over a threshold
/// the band is turned down by most of the excess, and it recovers as soon as the passage is over. Quiet detail in the
/// same region is left alone, which a fixed EQ cut cannot do.
/// </summary>
internal sealed class Deharsh
{
    readonly double _fs;
    BiquadCoeffs _bp;
    double _freq;
    BiquadState _sl, _sr;
    double _pBand, _pTot, _cut, _amt, _maxCut;
    readonly double _aBandUp, _aBandDown, _aTot, _aCutUp, _aCutDown, _aAmt;

    public Deharsh(double fs)
    {
        _fs = fs;
        _aBandUp = Fx.Coef(2, fs); _aBandDown = Fx.Coef(60, fs);
        _aTot = Fx.Coef(50, fs);
        _aCutUp = Fx.Coef(4, fs); _aCutDown = Fx.Coef(120, fs);
        _aAmt = Fx.Coef(20, fs);
        SetFreq(3500);
    }

    void SetFreq(double hz)
    {
        _freq = hz;
        // band-pass with exactly unity gain at its centre, so "signal − k·band" is a clean dip of depth k
        double w0 = 2 * Math.PI * Math.Clamp(hz, 500, _fs * 0.45) / _fs;
        double alpha = Math.Sin(w0) / (2 * 1.0);
        double a0 = 1 + alpha;
        _bp = new BiquadCoeffs { B0 = alpha / a0, B1 = 0, B2 = -alpha / a0, A1 = -2 * Math.Cos(w0) / a0, A2 = (1 - alpha) / a0 };
    }

    /// <summary>Deepest cut (dB, positive) since last read.</summary>
    public double TakeMaxCut() { double c = _maxCut; _maxCut = 0; return c; }

    public void Process(Span<double> l, Span<double> r, double amount, double freqHz)
    {
        if (amount <= 0 && _amt < 1e-4 && _cut < 0.01)
        {
            _amt = 0; _cut = 0; _sl = _sr = default; _pBand = _pTot = 0;
            return;
        }
        if (Math.Abs(freqHz - _freq) > 0.5) SetFreq(freqHz);

        double b0 = _bp.B0, b2 = _bp.B2, a1 = _bp.A1, a2 = _bp.A2;
        double z1l = _sl.Z1, z2l = _sl.Z2, z1r = _sr.Z1, z2r = _sr.Z2;
        double pBand = _pBand, pTot = _pTot, cut = _cut, amt = _amt, maxCut = _maxCut;
        for (int i = 0; i < l.Length; i++)
        {
            double xl = l[i], xr = r[i];
            amt += (amount - amt) * _aAmt;
            double bl = b0 * xl + z1l; z1l = -a1 * bl + z2l; z2l = b2 * xl - a2 * bl;
            double br = b0 * xr + z1r; z1r = -a1 * br + z2r; z2r = b2 * xr - a2 * br;

            double pb = 0.5 * (bl * bl + br * br), pt = 0.5 * (xl * xl + xr * xr);
            pBand += (pb - pBand) * (pb > pBand ? _aBandUp : _aBandDown);
            pTot += (pt - pTot) * _aTot;

            double target = 0;
            if (pTot > 1e-7 && pBand > 1e-12)
            {
                double shareDb = 10 * Math.Log10(pBand / pTot);         // how much of the music is in the band
                double threshold = -6 - 20 * amt;                       // more amount = steps in sooner
                target = Math.Clamp(0.8 * (shareDb - threshold), 0, 3 + 9 * amt);
            }
            cut += (target - cut) * (target > cut ? _aCutUp : _aCutDown);
            if (cut > maxCut) maxCut = cut;
            double k = 1 - Math.Pow(10, -cut / 20.0);

            l[i] = xl - k * bl;
            r[i] = xr - k * br;
        }
        _sl.Z1 = z1l; _sl.Z2 = z2l; _sr.Z1 = z1r; _sr.Z2 = z2r;
        _sl.Flush(); _sr.Flush();
        _pBand = pBand < 1e-30 ? 0 : pBand; _pTot = pTot < 1e-30 ? 0 : pTot;
        _cut = cut; _amt = amt; _maxCut = maxCut;
    }
}

/// <summary>
/// "Glue" compression: the slow, gentle, low-ratio compressor engineers put across a whole mix. It leans on the loud
/// passages by a decibel or three and lets go slowly, which pulls the parts of a recording together and makes it feel
/// denser. Stereo-linked, 2:1, soft knee, 30 ms attack so drum hits still punch through, 300 ms release.
/// </summary>
internal sealed class GlueComp
{
    double _p, _gr, _amt, _maxGr;
    readonly double _aRms, _aAtt, _aRel, _aAmt;

    public GlueComp(double fs)
    {
        _aRms = Fx.Coef(10, fs);
        _aAtt = Fx.Coef(30, fs);
        _aRel = Fx.Coef(300, fs);
        _aAmt = Fx.Coef(30, fs);
    }

    /// <summary>Deepest gain reduction (dB, positive) since last read.</summary>
    public double TakeMaxReduction() { double c = _maxGr; _maxGr = 0; return c; }

    public void Process(Span<double> l, Span<double> r, double amount)
    {
        if (amount <= 0 && _amt < 1e-4 && _gr > -0.01)
        {
            _amt = 0; _gr = 0; _p = 0;
            return;
        }
        const double knee = 8, slope = 0.5;      // 2:1
        double p = _p, gr = _gr, amt = _amt, maxGr = _maxGr;
        for (int i = 0; i < l.Length; i++)
        {
            double xl = l[i], xr = r[i];
            amt += (amount - amt) * _aAmt;
            p += (0.5 * (xl * xl + xr * xr) - p) * _aRms;

            double threshold = -36 * amt;
            double over = 10 * Math.Log10(p + 1e-12) - threshold;
            double excess = over <= -knee / 2 ? 0 : over >= knee / 2 ? over : (over + knee / 2) * (over + knee / 2) / (2 * knee);
            double target = -excess * slope;
            gr += (target - gr) * (target < gr ? _aAtt : _aRel);
            if (-gr > maxGr) maxGr = -gr;

            // a little make-up so quiet passages come up rather than everything going down
            double g = Math.Pow(10, (gr - 0.2 * threshold) / 20.0);
            l[i] = xl * g; r[i] = xr * g;
        }
        _p = p < 1e-30 ? 0 : p; _gr = gr; _amt = amt; _maxGr = maxGr;
    }
}

/// <summary>
/// Phantom bass. Small speakers cannot move enough air to play the lowest notes, but the ear does not need the note
/// itself: given its harmonics (2×, 3×, 4× the frequency) the brain fills in the missing fundamental. This takes
/// everything below the chosen frequency, manufactures those harmonics from it, and adds them back in. The bass is not
/// boosted; it is, in effect, described to the listener an octave or two higher up, where the speaker can cope.
/// </summary>
internal sealed class PhantomBass
{
    readonly double _fs;
    double _fc;
    BiquadCoeffs _lp, _hp, _top;
    BiquadState _lp1, _lp2, _hp1, _hp2, _top1;
    double _env, _amt;
    readonly double _aEnvUp, _aEnvDown, _aAmt;

    public PhantomBass(double fs)
    {
        _fs = fs;
        _aEnvUp = Fx.Coef(2, fs);
        _aEnvDown = Fx.Coef(150, fs);
        _aAmt = Fx.Coef(30, fs);
        SetFreq(90);
    }

    void SetFreq(double hz)
    {
        _fc = hz;
        _lp = BiquadCoeffs.Design(BandType.LowPass, hz, 0, 0.707, _fs);
        _hp = BiquadCoeffs.Design(BandType.HighPass, hz * 1.25, 0, 0.707, _fs);
        _top = BiquadCoeffs.Design(BandType.LowPass, hz * 4.5, 0, 0.707, _fs);
    }

    public void Process(Span<double> l, Span<double> r, double amount, double freqHz)
    {
        if (amount <= 0 && _amt < 1e-4)
        {
            if (_amt != 0) { _amt = 0; _env = 0; _lp1 = _lp2 = _hp1 = _hp2 = _top1 = default; }
            return;
        }
        if (Math.Abs(freqHz - _fc) > 0.5) SetFreq(Math.Clamp(freqHz, 30, 300));

        double env = _env, amt = _amt;
        var lp = _lp; var hp = _hp; var top = _top;
        for (int i = 0; i < l.Length; i++)
        {
            amt += (amount - amt) * _aAmt;
            double m = 0.5 * (l[i] + r[i]);

            // the bass on its own (24 dB/octave)
            double b = Step(lp, ref _lp1, m);
            b = Step(lp, ref _lp2, b);

            // follow its size so the harmonics keep the same proportion whether the bass is loud or quiet
            double mag = Math.Abs(b);
            env += (mag - env) * (mag > env ? _aEnvUp : _aEnvDown);
            double n = Math.Clamp(b / (env + 1e-5), -1, 1);

            // Chebyshev polynomials turn a unit sine into its exact 2nd, 3rd and 4th harmonics
            double n2 = n * n;
            double h = env * (1.0 * (2 * n2 - 1) + 0.6 * (4 * n2 * n - 3 * n) + 0.3 * (8 * n2 * n2 - 8 * n2 + 1));

            // keep only the harmonics: nothing below the cut-off, nothing fizzy above
            h = Step(hp, ref _hp1, h);
            h = Step(hp, ref _hp2, h);
            h = Step(top, ref _top1, h);

            double add = 1.6 * amt * h;
            l[i] += add; r[i] += add;
        }
        _env = env < 1e-30 ? 0 : env; _amt = amt;
        _lp1.Flush(); _lp2.Flush(); _hp1.Flush(); _hp2.Flush(); _top1.Flush();
    }

    static double Step(in BiquadCoeffs c, ref BiquadState s, double x)
    {
        double y = c.B0 * x + s.Z1;
        s.Z1 = c.B1 * x - c.A1 * y + s.Z2;
        s.Z2 = c.B2 * x - c.A2 * y;
        return y;
    }
}
