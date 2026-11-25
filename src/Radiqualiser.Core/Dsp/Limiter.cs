namespace Radiqualiser.Core.Dsp;

/// <summary>
/// Look-ahead peak limiter (stereo-linked). It sees each peak a moment before it is played and eases the gain down in
/// time, so the output never exceeds the ceiling and never hard-clips. Idle (gain exactly 1) unless something overshoots.
/// </summary>
public sealed class Limiter
{
    readonly int _d;                 // look-ahead in samples
    readonly double _ceiling;
    readonly double _relCoef;
    readonly double[] _bufL, _bufR;  // delay line, length d
    int _bufIdx;

    // sliding minimum over d+1 samples (monotonic deque)
    readonly double[] _dqVal;
    readonly long[] _dqIdx;
    int _dqHead, _dqCount;
    long _n;

    double _rel = 1;
    readonly double[] _box;          // moving average over d samples
    int _boxIdx;
    double _boxSum;

    public int LatencySamples => _d;

    /// <summary>Deepest gain reduction (linear, ≤ 1) since last read.</summary>
    public double MinGain { get; private set; } = 1;
    public double TakeMinGain() { double g = MinGain; MinGain = 1; return g; }

    /// <param name="ceilingDb">0 dBFS by default, so audio that already fits passes through completely untouched.</param>
    public Limiter(double fs, double ceilingDb = 0.0, double lookaheadMs = 1.5, double releaseMs = 120)
    {
        _d = Math.Max(8, (int)Math.Round(fs * lookaheadMs / 1000.0));
        _ceiling = Math.Pow(10, ceilingDb / 20.0);
        _relCoef = 1 - Math.Exp(-1.0 / (fs * releaseMs / 1000.0));
        _bufL = new double[_d];
        _bufR = new double[_d];
        _dqVal = new double[_d + 2];
        _dqIdx = new long[_d + 2];
        _box = new double[_d];
        Array.Fill(_box, 1.0);
        _boxSum = _d;
    }

    public void Process(Span<double> l, Span<double> r)
    {
        int d = _d, cap = _dqVal.Length;
        double ceiling = _ceiling, rel = _rel, relCoef = _relCoef, minGain = MinGain;
        for (int i = 0; i < l.Length; i++)
        {
            double xl = l[i], xr = r[i];
            double peak = Math.Max(Math.Abs(xl), Math.Abs(xr));
            double gt = peak > ceiling ? ceiling / peak : 1.0;

            // sliding minimum of the target gain over the last d+1 samples
            while (_dqCount > 0)
            {
                int back = _dqHead + _dqCount - 1; if (back >= cap) back -= cap;
                if (_dqVal[back] >= gt) _dqCount--; else break;
            }
            {
                int slot = _dqHead + _dqCount; if (slot >= cap) slot -= cap;
                _dqVal[slot] = gt; _dqIdx[slot] = _n; _dqCount++;
            }
            if (_dqIdx[_dqHead] < _n - d) { if (++_dqHead == cap) _dqHead = 0; _dqCount--; }
            double m = _dqVal[_dqHead];
            _n++;

            // instant attack, smooth release
            if (m < rel) rel = m; else rel += (m - rel) * relCoef;

            // moving average turns the instant attack into a ramp that lands exactly as the delayed peak arrives
            _boxSum += rel - _box[_boxIdx];
            _box[_boxIdx] = rel;
            if (++_boxIdx == d)
            {
                _boxIdx = 0;
                double s = 0;               // re-sum once per lap so rounding error cannot build up
                for (int j = 0; j < d; j++) s += _box[j];
                _boxSum = s;
            }
            double g = Math.Min(1.0, _boxSum / d);
            if (g < minGain) minGain = g;

            double yl = _bufL[_bufIdx] * g, yr = _bufR[_bufIdx] * g;
            _bufL[_bufIdx] = xl; _bufR[_bufIdx] = xr;
            if (++_bufIdx == d) _bufIdx = 0;

            // final safety clamp; should never engage
            l[i] = Math.Clamp(yl, -1.0, 1.0);
            r[i] = Math.Clamp(yr, -1.0, 1.0);
        }
        _rel = rel > 0.999999999 ? 1 : rel;
        MinGain = minGain;
    }
}
