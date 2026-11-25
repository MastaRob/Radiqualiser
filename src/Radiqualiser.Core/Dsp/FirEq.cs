namespace Radiqualiser.Core.Dsp;

/// <summary>Plain radix-2 complex FFT in double precision.</summary>
internal sealed class Fft
{
    readonly int _n;
    readonly int[] _rev;
    readonly double[] _cos, _sin;

    public Fft(int n)
    {
        _n = n;
        int bits = 0;
        while ((1 << bits) < n) bits++;
        _rev = new int[n];
        for (int i = 0; i < n; i++)
        {
            int r = 0;
            for (int b = 0; b < bits; b++) if ((i & (1 << b)) != 0) r |= 1 << (bits - 1 - b);
            _rev[i] = r;
        }
        _cos = new double[n / 2]; _sin = new double[n / 2];
        for (int i = 0; i < n / 2; i++)
        {
            _cos[i] = Math.Cos(2 * Math.PI * i / n);
            _sin[i] = Math.Sin(2 * Math.PI * i / n);
        }
    }

    /// <summary>In place. The inverse includes the 1/n scaling.</summary>
    public void Transform(double[] re, double[] im, bool inverse)
    {
        int n = _n;
        for (int i = 0; i < n; i++)
        {
            int j = _rev[i];
            if (j > i) { (re[i], re[j]) = (re[j], re[i]); (im[i], im[j]) = (im[j], im[i]); }
        }
        for (int len = 2; len <= n; len <<= 1)
        {
            int half = len >> 1, step = n / len;
            for (int i = 0; i < n; i += len)
            {
                for (int k = 0, t = 0; k < half; k++, t += step)
                {
                    double wr = _cos[t], wi = inverse ? _sin[t] : -_sin[t];
                    int a = i + k, b = a + half;
                    double xr = re[b] * wr - im[b] * wi, xi = re[b] * wi + im[b] * wr;
                    re[b] = re[a] - xr; im[b] = im[a] - xi;
                    re[a] += xr; im[a] += xi;
                }
            }
        }
        if (inverse)
        {
            double s = 1.0 / n;
            for (int i = 0; i < n; i++) { re[i] *= s; im[i] *= s; }
        }
    }
}

/// <summary>A finished EQ filter, in the form the convolver uses: the first few milliseconds as plain coefficients, the rest as spectra.</summary>
internal sealed class FirKernel
{
    public readonly bool Identity;
    public readonly double[] Head = Array.Empty<double>();
    public readonly double[][] Re = Array.Empty<double[]>(), Im = Array.Empty<double[]>();
    /// <summary>The whole impulse response (kept for tests and inspection).</summary>
    public readonly double[] Taps = Array.Empty<double>();

    public static readonly FirKernel Flat = new();

    FirKernel() { Identity = true; }

    public FirKernel(double[] taps, double[] head, double[][] re, double[][] im)
    {
        Taps = taps; Head = head; Re = re; Im = im;
    }
}

/// <summary>
/// Turns a drawn EQ curve into a filter that has exactly that frequency response.
///
/// A response of any shape can be had from an FIR filter (one long list of coefficients). Of the many filters with a
/// given response, this builds the "minimum-phase" one: it responds at once, with no added delay and no ringing before
/// a sound starts, which is also how an analogue equaliser behaves. The method is the standard one: take the logarithm
/// of the wanted response, move to the cepstral domain, discard the half that corresponds to "before", and return.
/// About a third of a second long, which is what it takes to resolve detail a few hertz wide in the bass.
/// </summary>
internal sealed class FirDesigner
{
    readonly double _fs;
    readonly int _n, _b, _m;
    readonly Fft _big, _small;
    readonly double[] _re, _im, _sr, _si;

    public int Length => _n;
    public int Block => _b;

    public FirDesigner(double fs)
    {
        _fs = fs;
        _n = fs < 60000 ? 16384 : fs < 120000 ? 32768 : 65536;
        _b = _n / 64;                    // 256 at 48 kHz: the first ~5 ms are convolved directly
        _m = _n * 4;                     // design on a finer grid than the final filter needs
        _big = new Fft(_m);
        _small = new Fft(2 * _b);
        _re = new double[_m]; _im = new double[_m];
        _sr = new double[2 * _b]; _si = new double[2 * _b];
    }

    public FirKernel Design(EqCurve curve)
    {
        if (curve.IsFlat) return FirKernel.Flat;
        int m = _m, n = _n, b = _b;
        double[] re = _re, im = _im;
        const double toLog = 0.11512925464970229;       // ln(10) / 20

        // wanted response, as log-magnitude, mirrored for a real filter
        for (int k = 0; k <= m / 2; k++)
        {
            double a = curve.At(k * _fs / m) * toLog;
            re[k] = a;
            if (k > 0 && k < m / 2) re[m - k] = a;
        }
        Array.Clear(im);
        _big.Transform(re, im, true);                   // → cepstrum

        // keep the causal half (doubled): this is what makes it minimum-phase
        for (int i = 1; i < m / 2; i++) re[i] *= 2;
        Array.Clear(re, m / 2 + 1, m / 2 - 1);
        Array.Clear(im);
        _big.Transform(re, im, false);                  // → log-magnitude + j·phase

        for (int k = 0; k < m; k++)
        {
            double mag = Math.Exp(re[k]);
            (double s, double c) = Math.SinCos(im[k]);
            re[k] = mag * c; im[k] = mag * s;
        }
        _big.Transform(re, im, true);                   // → impulse response

        var taps = new double[n];
        Array.Copy(re, taps, n);
        int fade = n / 4;                               // ease the far end to zero so the cut-off adds no ripple
        for (int i = 0; i < fade; i++)
        {
            double w = Math.Cos(0.5 * Math.PI * (i + 1) / fade);
            taps[n - fade + i] *= w * w;
        }

        var head = new double[b];
        Array.Copy(taps, head, b);
        int parts = n / b - 1;
        var pr = new double[parts][]; var pi = new double[parts][];
        for (int p = 0; p < parts; p++)
        {
            Array.Clear(_sr); Array.Clear(_si);
            Array.Copy(taps, b + p * b, _sr, 0, b);
            _small.Transform(_sr, _si, false);
            pr[p] = (double[])_sr.Clone(); pi[p] = (double[])_si.Clone();
        }
        return new FirKernel(taps, head, pr, pi);
    }
}

/// <summary>
/// Runs the EQ filter over the audio (stereo). Convolving sixteen thousand coefficients sample by sample would be far
/// too slow, so only the first block of the filter is done that way; the rest is done a block at a time with FFTs
/// (uniformly partitioned overlap-save). Because the FFT part only ever covers the filter from one block onwards, its
/// one-block wait is exactly the delay those coefficients have anyway, so the whole thing adds no latency.
/// A change of filter is crossfaded over one block.
/// </summary>
internal sealed class FirConvolver
{
    sealed class Channel
    {
        public double[] Hist, Block, Prev, Tail, TailOld;
        public double[][] FdlRe, FdlIm;

        public Channel(int b, int parts)
        {
            Hist = new double[2 * b]; Block = new double[b]; Prev = new double[b]; Tail = new double[b]; TailOld = new double[b];
            FdlRe = new double[parts][]; FdlIm = new double[parts][];
            for (int i = 0; i < parts; i++) { FdlRe[i] = new double[2 * b]; FdlIm[i] = new double[2 * b]; }
        }
    }

    readonly int _b, _parts;
    readonly Fft _fft;
    readonly Channel[] _ch;
    readonly double[] _wr, _wi, _ar, _ai;
    int _hw, _pos, _fdl;
    FirKernel _cur = FirKernel.Flat, _old = FirKernel.Flat;
    bool _fading;

    public FirConvolver(int block, int length)
    {
        _b = block;
        _parts = length / block - 1;
        _fft = new Fft(2 * block);
        _ch = new[] { new Channel(block, _parts), new Channel(block, _parts) };
        _wr = new double[2 * block]; _wi = new double[2 * block];
        _ar = new double[2 * block]; _ai = new double[2 * block];
    }

    public void Process(Span<double> l, Span<double> r, FirKernel wanted)
    {
        int n = l.Length, i = 0, b = _b;
        while (i < n)
        {
            int seg = Math.Min(n - i, b - _pos);
            Run(_ch[0], l.Slice(i, seg));
            Run(_ch[1], r.Slice(i, seg));
            _hw = ((_hw - seg) % b + b) % b;
            _pos += seg;
            i += seg;
            if (_pos == b) BlockDone(wanted);
        }
    }

    void Run(Channel c, Span<double> x)
    {
        int b = _b, hw = _hw, pos = _pos;
        double[] hist = c.Hist, block = c.Block;
        var cur = _cur; var old = _old;
        bool fading = _fading;
        if (!fading && cur.Identity)
        {
            // nothing to do to the sound, but keep the history current so the EQ can come in seamlessly
            for (int j = 0; j < x.Length; j++)
            {
                double v = x[j];
                hw = (hw == 0 ? b : hw) - 1;
                hist[hw] = hist[hw + b] = v;
                block[pos++] = v;
            }
            return;
        }
        double inv = 1.0 / b;
        for (int j = 0; j < x.Length; j++)
        {
            double v = x[j];
            hw = (hw == 0 ? b : hw) - 1;
            hist[hw] = hist[hw + b] = v;
            block[pos] = v;
            ReadOnlySpan<double> h = hist.AsSpan(hw, b);
            double y = cur.Identity ? v : System.Numerics.Tensors.TensorPrimitives.Dot(cur.Head, h) + c.Tail[pos];
            if (fading)
            {
                double yo = old.Identity ? v : System.Numerics.Tensors.TensorPrimitives.Dot(old.Head, h) + c.TailOld[pos];
                y = yo + (pos + 1) * inv * (y - yo);
            }
            x[j] = y;
            pos++;
        }
    }

    void BlockDone(FirKernel wanted)
    {
        if (_fading)
        {
            _fading = false;
            _old = FirKernel.Flat;
        }
        else if (!ReferenceEquals(wanted, _cur))
        {
            _old = _cur;
            _cur = wanted;
            _fading = true;
        }

        int b = _b;
        _fdl = (_fdl + 1) % _parts;
        foreach (var c in _ch)
        {
            Array.Copy(c.Prev, 0, _wr, 0, b);
            Array.Copy(c.Block, 0, _wr, b, b);
            Array.Clear(_wi);
            _fft.Transform(_wr, _wi, false);
            Array.Copy(_wr, c.FdlRe[_fdl], 2 * b);
            Array.Copy(_wi, c.FdlIm[_fdl], 2 * b);
            (c.Prev, c.Block) = (c.Block, c.Prev);

            if (_fading) Tail(c, _old, c.TailOld);
            Tail(c, _cur, c.Tail);
        }
        _pos = 0;
    }

    void Tail(Channel c, FirKernel k, double[] dest)
    {
        if (k.Identity) { Array.Clear(dest); return; }
        int size = 2 * _b;
        double[] ar = _ar, ai = _ai;
        Array.Clear(ar); Array.Clear(ai);
        for (int p = 0; p < _parts; p++)
        {
            int slot = _fdl - p; if (slot < 0) slot += _parts;
            double[] xr = c.FdlRe[slot], xi = c.FdlIm[slot], gr = k.Re[p], gi = k.Im[p];
            for (int q = 0; q < size; q++)
            {
                ar[q] += gr[q] * xr[q] - gi[q] * xi[q];
                ai[q] += gr[q] * xi[q] + gi[q] * xr[q];
            }
        }
        _fft.Transform(ar, ai, true);
        Array.Copy(ar, _b, dest, 0, _b);
    }
}
