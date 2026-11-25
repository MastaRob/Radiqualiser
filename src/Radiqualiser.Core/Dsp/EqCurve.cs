namespace Radiqualiser.Core.Dsp;

/// <summary>
/// The EQ as the user draws it: a line through a set of points on the frequency/gain graph. The line passes exactly
/// through every point, runs flat beyond the first and last points, and between points is anywhere from straight
/// segments (rounding 0) to a smooth curve (rounding 1). The smooth curve never overshoots: a point that is the
/// highest or lowest in its neighbourhood stays the highest or lowest thing on the line.
/// </summary>
public sealed class EqCurve
{
    readonly double[] _x, _y, _m;       // ln(frequency), gain in dB, slope at each point
    readonly double _round;

    public static readonly EqCurve Flat = new(Array.Empty<(double, double)>(), 1);

    public EqCurve(IEnumerable<(double Freq, double GainDb)> points, double rounding)
    {
        var pts = points.Where(p => p.Freq > 0).OrderBy(p => p.Freq).ToArray();
        int n = pts.Length;
        _x = new double[n]; _y = new double[n]; _m = new double[n];
        _round = Math.Clamp(rounding, 0, 1);
        for (int i = 0; i < n; i++)
        {
            _x[i] = Math.Log(pts[i].Freq);
            if (i > 0 && _x[i] <= _x[i - 1] + 1e-6) _x[i] = _x[i - 1] + 1e-6;     // two points on one frequency: keep them apart
            _y[i] = pts[i].GainDb;
        }
        // Shape-preserving slopes (Fritsch–Carlson). Zero at the ends, so the curve meets the flat extension smoothly,
        // and zero wherever the line changes direction, so the turning point is the point itself.
        for (int i = 1; i < n - 1; i++)
        {
            double h0 = _x[i] - _x[i - 1], h1 = _x[i + 1] - _x[i];
            double d0 = (_y[i] - _y[i - 1]) / h0, d1 = (_y[i + 1] - _y[i]) / h1;
            if (d0 * d1 <= 0) continue;
            double w1 = 2 * h1 + h0, w2 = h1 + 2 * h0;
            _m[i] = (w1 + w2) / (w1 / d0 + w2 / d1);
        }
    }

    public int Count => _x.Length;
    public bool IsFlat => _y.All(v => Math.Abs(v) < 1e-9);

    /// <summary>Gain in dB at a frequency.</summary>
    public double At(double freq)
    {
        int n = _x.Length;
        if (n == 0) return 0;
        double x = Math.Log(Math.Max(freq, 1e-3));
        if (x <= _x[0]) return _y[0];
        if (x >= _x[n - 1]) return _y[n - 1];
        int lo = 0, hi = n - 1;
        while (hi - lo > 1)
        {
            int mid = (lo + hi) >> 1;
            if (_x[mid] <= x) lo = mid; else hi = mid;
        }
        double h = _x[hi] - _x[lo], t = (x - _x[lo]) / h;
        double straight = _y[lo] + t * (_y[hi] - _y[lo]);
        if (_round <= 0) return straight;
        double t2 = t * t, t3 = t2 * t;
        double smooth = (2 * t3 - 3 * t2 + 1) * _y[lo] + (t3 - 2 * t2 + t) * h * _m[lo]
                        + (-2 * t3 + 3 * t2) * _y[hi] + (t3 - t2) * h * _m[hi];
        return straight + _round * (smooth - straight);
    }

    public bool SameAs(EqCurve other) =>
        _round == other._round && _x.AsSpan().SequenceEqual(other._x) && _y.AsSpan().SequenceEqual(other._y);
}
