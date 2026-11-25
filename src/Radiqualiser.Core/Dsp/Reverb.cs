namespace Radiqualiser.Core.Dsp;

/// <summary>
/// A large-space reverb (hall to cathedral), added on top of the untouched direct sound.
///
/// Structure: a "feedback delay network". Sixteen delay lines of different lengths (31–123 ms, i.e. sound paths of
/// roughly 10–40 m) feed back into one another through a mixing matrix, so one echo becomes sixteen, then 256, and
/// within a fraction of a second a dense wash, the way reflections multiply between the walls of a big stone room.
/// Each trip round loses a little level (that sets the decay time) and a little more treble than bass (air and
/// surfaces absorb highs first, so the tail darkens as it fades). Before the network, a short pre-delay and four
/// "diffusers" smear each sound so the first echoes are soft rather than slappy; the line lengths drift very slightly
/// to stop any one frequency ringing.
/// </summary>
internal sealed class CathedralReverb
{
    const int N = 16;
    static readonly double[] LineMs = { 31.1, 36.7, 41.3, 46.9, 52.3, 58.1, 63.7, 69.1, 75.3, 81.7, 88.1, 94.3, 101.9, 108.7, 115.3, 122.9 };
    static readonly double[] DiffuserMs = { 4.7, 7.9, 12.1, 17.3 };
    const double ModDepth = 6;      // samples of drift on each line

    readonly double _fs;
    readonly double[][] _line = new double[N][];
    readonly int[] _len = new int[N];
    readonly int[] _mask = new int[N];
    int _w;
    readonly double[] _damp = new double[N];        // one-pole state per line
    readonly double[] _gain = new double[N], _pole = new double[N];
    readonly double[] _modPhase = new double[N], _modRate = new double[N], _modNow = new double[N], _modStep = new double[N];
    readonly double[] _v = new double[N];

    readonly double[][] _apL = new double[4][], _apR = new double[4][];
    readonly int[] _apIdxL = new int[4], _apIdxR = new int[4];
    readonly double[] _preL, _preR;
    int _preIdx;

    readonly BiquadCoeffs _inHp, _inLp;
    BiquadState _hpL, _hpR, _lpL, _lpR;

    double _wet, _decay = -1, _norm;
    readonly double _aWet;
    bool _active;

    public CathedralReverb(double fs)
    {
        _fs = fs;
        for (int i = 0; i < N; i++)
        {
            _len[i] = NextPrime((int)(LineMs[i] * fs / 1000.0));
            int size = 1;
            while (size < _len[i] + ModDepth * 2 + 8) size <<= 1;
            _line[i] = new double[size];
            _mask[i] = size - 1;
            _modRate[i] = 2 * Math.PI * (0.11 + 0.031 * i);        // 0.11 … 0.58 Hz, all different
            _modPhase[i] = i * 2.399;
        }
        for (int i = 0; i < 4; i++)
        {
            _apL[i] = new double[NextPrime((int)(DiffuserMs[i] * fs / 1000.0))];
            _apR[i] = new double[NextPrime((int)(DiffuserMs[i] * 1.13 * fs / 1000.0))];
        }
        int pre = (int)(0.025 * fs);
        _preL = new double[pre]; _preR = new double[pre];
        _inHp = BiquadCoeffs.Design(BandType.HighPass, 130, 0, 0.707, fs);      // keep the tail from turning to mud
        _inLp = BiquadCoeffs.Design(BandType.LowPass, 9000, 0, 0.707, fs);
        _aWet = Fx.Coef(40, fs);
    }

    static int NextPrime(int n)
    {
        for (n = Math.Max(n, 3) | 1; ; n += 2)
        {
            bool prime = true;
            for (int d = 3; (long)d * d <= n; d += 2) if (n % d == 0) { prime = false; break; }
            if (prime) return n;
        }
    }

    void Retune(double seconds)
    {
        _decay = seconds;
        double rtHigh = Math.Max(0.4, seconds * 0.38);      // treble dies in well under half the time
        for (int i = 0; i < N; i++)
        {
            double g = Math.Pow(10, -3.0 * _len[i] / (seconds * _fs));
            double gHigh = Math.Pow(10, -3.0 * _len[i] / (rtHigh * _fs));
            double ratio = gHigh / g;
            _pole[i] = (1 - ratio) / (1 + ratio);
            _gain[i] = g * (1 - _pole[i]);
        }
        _norm = 2.2 / Math.Sqrt(8 * seconds);               // same loudness whatever the decay time; full = level with the direct sound
    }

    void Clear()
    {
        foreach (var a in _line) Array.Clear(a);
        foreach (var a in _apL) Array.Clear(a);
        foreach (var a in _apR) Array.Clear(a);
        Array.Clear(_preL); Array.Clear(_preR); Array.Clear(_damp);
        _hpL = _hpR = _lpL = _lpR = default;
    }

    /// <param name="amount">0…1; how much reverb is added.</param>
    /// <param name="seconds">Time for the tail to fade by 60 dB.</param>
    public void Process(Span<double> l, Span<double> r, double amount, double seconds)
    {
        double wetTarget = amount * amount;
        if (wetTarget <= 0 && _wet < 1e-6)
        {
            if (_active) { Clear(); _active = false; _wet = 0; }
            return;
        }
        _active = true;
        seconds = Math.Clamp(seconds, 0.3, 20);
        if (Math.Abs(seconds - _decay) > 1e-3) Retune(seconds);

        int n = l.Length;
        for (int i = 0; i < N; i++)
        {
            double next = ModDepth * (1 + Math.Sin(_modPhase[i] + _modRate[i] * n / _fs));
            _modStep[i] = (next - _modNow[i]) / n;
            _modPhase[i] += _modRate[i] * n / _fs;
            if (_modPhase[i] > 2 * Math.PI) _modPhase[i] -= 2 * Math.PI;
        }

        Span<double> v = _v;
        int w = _w, preIdx = _preIdx, preLen = _preL.Length;
        double wet = _wet, norm = _norm;
        for (int s = 0; s < n; s++)
        {
            wet += (wetTarget - wet) * _aWet;

            // pre-delay, band-limit, diffuse
            double inL = _preL[preIdx], inR = _preR[preIdx];
            _preL[preIdx] = l[s]; _preR[preIdx] = r[s];
            if (++preIdx == preLen) preIdx = 0;
            inL = Step(_inLp, ref _lpL, Step(_inHp, ref _hpL, inL));
            inR = Step(_inLp, ref _lpR, Step(_inHp, ref _hpR, inR));
            for (int a = 0; a < 4; a++)
            {
                inL = Allpass(_apL[a], ref _apIdxL[a], inL);
                inR = Allpass(_apR[a], ref _apIdxR[a], inR);
            }

            // read each line (at a slowly drifting point), absorb, collect the stereo output
            double outL = 0, outR = 0;
            for (int i = 0; i < N; i++)
            {
                double mod = _modNow[i] += _modStep[i];
                double pos = w - _len[i] + ModDepth - mod;
                int p0 = (int)Math.Floor(pos);
                double f = pos - p0;
                double[] line = _line[i];
                int mask = _mask[i];
                double x = line[p0 & mask] + f * (line[(p0 + 1) & mask] - line[p0 & mask]);
                double d = _damp[i] = x * _gain[i] + _pole[i] * _damp[i];
                v[i] = d;
                if ((i & 1) == 0) outL += (i & 2) == 0 ? d : -d;
                else outR += (i & 2) == 0 ? d : -d;
            }

            // mix every line into every other (Hadamard matrix: lossless, maximally spreading)
            for (int h = 1; h < N; h <<= 1)
            {
                for (int i = 0; i < N; i += h << 1)
                {
                    for (int j = i; j < i + h; j++)
                    {
                        double a = v[j], b = v[j + h];
                        v[j] = a + b; v[j + h] = a - b;
                    }
                }
            }

            const double anti = 1e-24;      // keeps the fading tail out of the slow "denormal" number range
            for (int i = 0; i < N; i++)
                _line[i][w & _mask[i]] = v[i] * 0.25 + ((i & 1) == 0 ? inL : inR) + anti;
            w++;

            l[s] += wet * norm * outL;
            r[s] += wet * norm * outR;
        }
        _w = w & 0x3FFFFFFF;
        _preIdx = preIdx;
        _wet = wet;
        _hpL.Flush(); _hpR.Flush(); _lpL.Flush(); _lpR.Flush();
    }

    static double Step(in BiquadCoeffs c, ref BiquadState s, double x)
    {
        double y = c.B0 * x + s.Z1;
        s.Z1 = c.B1 * x - c.A1 * y + s.Z2;
        s.Z2 = c.B2 * x - c.A2 * y;
        return y;
    }

    static double Allpass(double[] buf, ref int idx, double x)
    {
        const double g = 0.62;
        double delayed = buf[idx];
        double input = x + g * delayed;
        buf[idx] = input;
        if (++idx == buf.Length) idx = 0;
        return delayed - g * input;
    }
}
