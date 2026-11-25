using System.Numerics.Tensors;
using Radiqualiser.Core.Audio;

namespace Radiqualiser.Core.Dsp;

/// <summary>
/// Variable-ratio windowed-sinc resampler (stereo). The virtual cable and the real output run on separate clocks that
/// drift apart by a few parts per million; this nudges the playback rate by an inaudible fraction to keep them in step,
/// and also converts between sample rates if the two devices are set differently.
/// </summary>
public sealed class AsyncResampler
{
    const int Phases = 1024;
    readonly int _taps, _half;
    readonly float[] _table;        // (Phases + 1) rows of _taps
    readonly double _nominalStep;
    float[] _inL, _inR, _tmp;
    int _count;
    double _pos;

    /// <summary>Relative speed trim, e.g. +0.0001 = consume input 100 ppm faster.</summary>
    public double Adjust;

    public AsyncResampler(int inRate, int outRate)
    {
        _nominalStep = (double)inRate / outRate;
        double c = Math.Min(1.0, (double)outRate / inRate);
        const int zeroCrossings = 48;
        _taps = (int)Math.Ceiling(2 * zeroCrossings / c);
        if ((_taps & 1) != 0) _taps++;
        _half = _taps / 2;
        double cutoff = c * 0.965;
        const double beta = 13.0;      // ~125 dB stop-band
        double i0b = Window.BesselI0(beta);

        _table = new float[(Phases + 1) * _taps];
        var row = new double[_taps];
        for (int p = 0; p <= Phases; p++)
        {
            double f = p / (double)Phases;
            double sum = 0;
            for (int k = 0; k < _taps; k++)
            {
                double t = (k - _half + 1) - f;
                double r = t / _half;
                double w = Math.Abs(r) < 1 ? Window.BesselI0(beta * Math.Sqrt(1 - r * r)) / i0b : 0;
                row[k] = cutoff * Window.Sinc(cutoff * t) * w;
                sum += row[k];
            }
            for (int k = 0; k < _taps; k++) _table[p * _taps + k] = (float)(row[k] / sum);
        }

        _inL = new float[_taps + 4096];
        _inR = new float[_taps + 4096];
        _tmp = new float[8192];
        Reset();
    }

    public void Reset()
    {
        Array.Clear(_inL); Array.Clear(_inR);
        _count = _taps;
        _pos = _half - 1;
        Adjust = 0;
    }

    /// <summary>How many input frames the next call for <paramref name="frames"/> output frames will need from the ring.</summary>
    public int InputNeeded(int frames)
    {
        double step = _nominalStep * (1 + Adjust);
        int lastI = (int)(_pos + (frames - 1) * step);
        return Math.Max(0, lastI + _half + 1 - _count);
    }

    /// <summary>Produces <c>l.Length</c> frames. Returns false (and produces nothing) if the ring does not hold enough input.</summary>
    public bool Process(SpscRing ring, Span<double> l, Span<double> r)
    {
        int n = l.Length;
        if (n == 0) return true;
        double step = _nominalStep * (1 + Adjust);
        int need = InputNeeded(n);
        if (need > 0)
        {
            if (ring.Available < need * 2) return false;
            if (_count + need > _inL.Length)
            {
                Array.Resize(ref _inL, _count + need + 1024);
                Array.Resize(ref _inR, _count + need + 1024);
            }
            if (_tmp.Length < need * 2) _tmp = new float[need * 2];
            ring.Read(_tmp.AsSpan(0, need * 2));
            for (int i = 0; i < need; i++)
            {
                _inL[_count + i] = _tmp[2 * i];
                _inR[_count + i] = _tmp[2 * i + 1];
            }
            _count += need;
        }

        int taps = _taps, half = _half;
        double pos = _pos;
        ReadOnlySpan<float> table = _table;
        for (int j = 0; j < n; j++)
        {
            int i = (int)pos;
            double ph = (pos - i) * Phases;
            int p0 = (int)ph;
            double a = ph - p0;
            int start = i - half + 1;
            ReadOnlySpan<float> h0 = table.Slice(p0 * taps, taps);
            ReadOnlySpan<float> h1 = table.Slice((p0 + 1) * taps, taps);
            ReadOnlySpan<float> xl = _inL.AsSpan(start, taps);
            ReadOnlySpan<float> xr = _inR.AsSpan(start, taps);
            double l0 = TensorPrimitives.Dot(xl, h0), l1 = TensorPrimitives.Dot(xl, h1);
            double r0 = TensorPrimitives.Dot(xr, h0), r1 = TensorPrimitives.Dot(xr, h1);
            l[j] = l0 + a * (l1 - l0);
            r[j] = r0 + a * (r1 - r0);
            pos += step;
        }

        int shift = Math.Min((int)pos - half + 1, _count);
        if (shift > 0)
        {
            Array.Copy(_inL, shift, _inL, 0, _count - shift);
            Array.Copy(_inR, shift, _inR, 0, _count - shift);
            _count -= shift;
            pos -= shift;
        }
        _pos = pos;
        return true;
    }
}
