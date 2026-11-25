namespace Radiqualiser.Core.Dsp;

public enum BandType { Peak, LowShelf, HighShelf, LowPass, HighPass, Notch }

/// <summary>Normalised biquad coefficients (a0 = 1).</summary>
public struct BiquadCoeffs
{
    public double B0, B1, B2, A1, A2;

    public static readonly BiquadCoeffs Identity = new() { B0 = 1 };

    /// <summary>RBJ "Audio EQ Cookbook" designs.</summary>
    public static BiquadCoeffs Design(BandType type, double freq, double gainDb, double q, double fs)
    {
        freq = Math.Clamp(freq, 5.0, fs * 0.49);
        q = Math.Clamp(q, 0.05, 200.0);
        double w0 = 2 * Math.PI * freq / fs;
        double cos = Math.Cos(w0), sin = Math.Sin(w0);
        double alpha = sin / (2 * q);
        double a = Math.Pow(10, gainDb / 40.0);
        double b0, b1, b2, a0, a1, a2;

        switch (type)
        {
            case BandType.Peak:
                b0 = 1 + alpha * a; b1 = -2 * cos; b2 = 1 - alpha * a;
                a0 = 1 + alpha / a; a1 = -2 * cos; a2 = 1 - alpha / a;
                break;
            case BandType.LowShelf:
            {
                double s = 2 * Math.Sqrt(a) * alpha;
                b0 = a * ((a + 1) - (a - 1) * cos + s);
                b1 = 2 * a * ((a - 1) - (a + 1) * cos);
                b2 = a * ((a + 1) - (a - 1) * cos - s);
                a0 = (a + 1) + (a - 1) * cos + s;
                a1 = -2 * ((a - 1) + (a + 1) * cos);
                a2 = (a + 1) + (a - 1) * cos - s;
                break;
            }
            case BandType.HighShelf:
            {
                double s = 2 * Math.Sqrt(a) * alpha;
                b0 = a * ((a + 1) + (a - 1) * cos + s);
                b1 = -2 * a * ((a - 1) + (a + 1) * cos);
                b2 = a * ((a + 1) + (a - 1) * cos - s);
                a0 = (a + 1) - (a - 1) * cos + s;
                a1 = 2 * ((a - 1) - (a + 1) * cos);
                a2 = (a + 1) - (a - 1) * cos - s;
                break;
            }
            case BandType.LowPass:
                b0 = (1 - cos) / 2; b1 = 1 - cos; b2 = (1 - cos) / 2;
                a0 = 1 + alpha; a1 = -2 * cos; a2 = 1 - alpha;
                break;
            case BandType.HighPass:
                b0 = (1 + cos) / 2; b1 = -(1 + cos); b2 = (1 + cos) / 2;
                a0 = 1 + alpha; a1 = -2 * cos; a2 = 1 - alpha;
                break;
            case BandType.Notch:
                b0 = 1; b1 = -2 * cos; b2 = 1;
                a0 = 1 + alpha; a1 = -2 * cos; a2 = 1 - alpha;
                break;
            default:
                return Identity;
        }

        double inv = 1.0 / a0;
        return new BiquadCoeffs { B0 = b0 * inv, B1 = b1 * inv, B2 = b2 * inv, A1 = a1 * inv, A2 = a2 * inv };
    }

    public readonly double MagnitudeDb(double freq, double fs)
    {
        double w = 2 * Math.PI * freq / fs;
        double c1 = Math.Cos(w), c2 = Math.Cos(2 * w);
        double num = B0 * B0 + B1 * B1 + B2 * B2 + 2 * (B0 * B1 + B1 * B2) * c1 + 2 * B0 * B2 * c2;
        double den = 1 + A1 * A1 + A2 * A2 + 2 * (A1 + A1 * A2) * c1 + 2 * A2 * c2;
        if (num < 1e-30) return -150;
        return 10 * Math.Log10(num / Math.Max(den, 1e-30));
    }

    public static bool Same(in BiquadCoeffs a, in BiquadCoeffs b) =>
        a.B0 == b.B0 && a.B1 == b.B1 && a.B2 == b.B2 && a.A1 == b.A1 && a.A2 == b.A2;
}

public struct BiquadState
{
    public double Z1, Z2;

    /// <summary>Kills denormals so a silent input does not slow the filter down.</summary>
    public void Flush()
    {
        if (Math.Abs(Z1) < 1e-25) Z1 = 0;
        if (Math.Abs(Z2) < 1e-25) Z2 = 0;
    }
}

public static class Biquad
{
    /// <summary>Transposed direct form II, in place.</summary>
    public static void Process(in BiquadCoeffs c, ref BiquadState s, Span<double> x)
    {
        double b0 = c.B0, b1 = c.B1, b2 = c.B2, a1 = c.A1, a2 = c.A2;
        double z1 = s.Z1, z2 = s.Z2;
        for (int i = 0; i < x.Length; i++)
        {
            double v = x[i];
            double y = b0 * v + z1;
            z1 = b1 * v - a1 * y + z2;
            z2 = b2 * v - a2 * y;
            x[i] = y;
        }
        s.Z1 = z1; s.Z2 = z2;
        s.Flush();
    }

    /// <summary>Same filter, but glides from one coefficient set to another across the block (no zipper noise while dragging).</summary>
    public static void ProcessRamp(in BiquadCoeffs from, in BiquadCoeffs to, ref BiquadState s, Span<double> x)
    {
        int n = x.Length;
        if (n == 0) return;
        double inv = 1.0 / n;
        double b0 = from.B0, b1 = from.B1, b2 = from.B2, a1 = from.A1, a2 = from.A2;
        double db0 = (to.B0 - b0) * inv, db1 = (to.B1 - b1) * inv, db2 = (to.B2 - b2) * inv;
        double da1 = (to.A1 - a1) * inv, da2 = (to.A2 - a2) * inv;
        double z1 = s.Z1, z2 = s.Z2;
        for (int i = 0; i < n; i++)
        {
            b0 += db0; b1 += db1; b2 += db2; a1 += da1; a2 += da2;
            double v = x[i];
            double y = b0 * v + z1;
            z1 = b1 * v - a1 * y + z2;
            z2 = b2 * v - a2 * y;
            x[i] = y;
        }
        s.Z1 = z1; s.Z2 = z2;
        s.Flush();
    }
}
