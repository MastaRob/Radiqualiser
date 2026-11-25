namespace Radiqualiser.Core.Audio;

/// <summary>Lock-free single-producer / single-consumer ring of floats (capture thread writes, render thread reads).</summary>
public sealed class SpscRing
{
    readonly float[] _buf;
    readonly int _mask;
    long _write, _read;

    public SpscRing(int minCapacity)
    {
        int cap = 1;
        while (cap < minCapacity) cap <<= 1;
        _buf = new float[cap];
        _mask = cap - 1;
    }

    public int Capacity => _buf.Length;
    public int Available => (int)(Volatile.Read(ref _write) - Volatile.Read(ref _read));

    /// <summary>Producer side. Returns how many values were stored (fewer than requested if full).</summary>
    public int Write(ReadOnlySpan<float> src)
    {
        long w = _write;
        int free = _buf.Length - (int)(w - Volatile.Read(ref _read));
        int n = Math.Min(free, src.Length);
        if (n <= 0) return 0;
        int pos = (int)(w & _mask);
        int first = Math.Min(n, _buf.Length - pos);
        src.Slice(0, first).CopyTo(_buf.AsSpan(pos));
        if (n > first) src.Slice(first, n - first).CopyTo(_buf);
        Volatile.Write(ref _write, w + n);
        return n;
    }

    /// <summary>Consumer side.</summary>
    public int Read(Span<float> dst)
    {
        long r = _read;
        int avail = (int)(Volatile.Read(ref _write) - r);
        int n = Math.Min(avail, dst.Length);
        if (n <= 0) return 0;
        int pos = (int)(r & _mask);
        int first = Math.Min(n, _buf.Length - pos);
        _buf.AsSpan(pos, first).CopyTo(dst);
        if (n > first) _buf.AsSpan(0, n - first).CopyTo(dst.Slice(first));
        Volatile.Write(ref _read, r + n);
        return n;
    }

    /// <summary>Consumer side: discard up to n values.</summary>
    public int Skip(int n)
    {
        long r = _read;
        int avail = (int)(Volatile.Read(ref _write) - r);
        n = Math.Min(avail, n);
        if (n > 0) Volatile.Write(ref _read, r + n);
        return Math.Max(0, n);
    }
}
