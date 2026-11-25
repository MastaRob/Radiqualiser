using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using Radiqualiser.Core.Audio;
using Radiqualiser.Core.Dsp;

string mode = args.Length > 0 ? args[0] : "dsp";
switch (mode)
{
    case "dsp": DspChecks.Run(); break;
    case "probe": DeviceChecks.Probe(); break;
    case "cable": DeviceChecks.CableVolume(); break;
    case "link": DeviceChecks.VolumeLink(); break;
    case "noise": DeviceChecks.Noise(args); break;
    case "run": DeviceChecks.RunEngine(args.Length > 1 ? int.Parse(args[1]) : 8, args.Length > 2 && args[2] == "tone"); break;
    default: Console.WriteLine("modes: dsp | probe | cable | run [seconds] [tone]"); break;
}

static class M
{
    public static double Db(double x) => 20 * Math.Log10(Math.Max(x, 1e-20));

    /// <summary>Amplitude of the component at freq (least-squares sine fit).</summary>
    public static double Tone(ReadOnlySpan<double> x, double freq, double fs)
    {
        double s = 0, c = 0;
        for (int i = 0; i < x.Length; i++)
        {
            double ph = 2 * Math.PI * freq * i / fs;
            s += x[i] * Math.Sin(ph); c += x[i] * Math.Cos(ph);
        }
        return 2 * Math.Sqrt(s * s + c * c) / x.Length;
    }

    /// <summary>Residual RMS after removing the best-fit sine at freq, relative to that sine's RMS (dB).</summary>
    public static double ResidualDb(ReadOnlySpan<double> x, double freq, double fs)
    {
        double ss = 0, cc = 0, sc = 0, xs = 0, xc = 0;
        for (int i = 0; i < x.Length; i++)
        {
            double ph = 2 * Math.PI * freq * i / fs;
            double sn = Math.Sin(ph), cs = Math.Cos(ph);
            ss += sn * sn; cc += cs * cs; sc += sn * cs; xs += x[i] * sn; xc += x[i] * cs;
        }
        double det = ss * cc - sc * sc;
        double a = (xs * cc - xc * sc) / det, b = (xc * ss - xs * sc) / det;
        double res = 0, sig = 0;
        for (int i = 0; i < x.Length; i++)
        {
            double ph = 2 * Math.PI * freq * i / fs;
            double fit = a * Math.Sin(ph) + b * Math.Cos(ph);
            res += (x[i] - fit) * (x[i] - fit); sig += fit * fit;
        }
        return 10 * Math.Log10(Math.Max(res, 1e-300) / sig);
    }

    public static double[] Sine(double freq, double fs, int n, double amp) =>
        Enumerable.Range(0, n).Select(i => amp * Math.Sin(2 * Math.PI * freq * i / fs)).ToArray();
}

static class DspChecks
{
    static int _fail;

    static void Check(string name, bool ok, string detail)
    {
        Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name,-46} {detail}");
        if (!ok) _fail++;
    }

    /// <summary>Runs a stereo signal through a chain in 480-frame blocks, returns the left channel.</summary>
    static double[] RunChain(DspChain chain, double[] input, int block = 480)
    {
        var l = (double[])input.Clone();
        var r = (double[])input.Clone();
        for (int o = 0; o < l.Length; o += block)
        {
            int n = Math.Min(block, l.Length - o);
            chain.Process(l.AsSpan(o, n), r.AsSpan(o, n));
        }
        return l;
    }

    static DspChain Ref(int fs, TubeParams tp)
    {
        var c = new DspChain(fs);
        c.SetParams(DspParams.Default with { Tube = tp });
        return c;
    }

    public static void Run()
    {
        foreach (int fs in new[] { 44100, 48000, 96000 })
        {
            Console.WriteLine($"--- {fs} Hz ---");
            int n = fs * 2;
            int skip = fs / 2;

            // EQ: the response passes through the drawn points and matches the curve between and beyond them
            {
                var pts = new[] { new BandParams(1, 100, -4), new BandParams(2, 1000, 6), new BandParams(3, 5000, 0) };
                var curve = new EqCurve(pts.Select(b => (b.Freq, b.GainDb)), 1);
                double worst = 0; string detail = "";
                foreach (double f in new[] { 40.0, 100, 316, 1000, 2236, 5000, 15000 })
                {
                    var chain = new DspChain(fs);
                    chain.SetParams(DspParams.Default with { Bands = pts });
                    chain.WaitForEq();
                    var y = RunChain(chain, M.Sine(f, fs, n, 0.1));
                    double g = M.Db(M.Tone(y.AsSpan(skip, fs), f, fs) / 0.1);
                    worst = Math.Max(worst, Math.Abs(g - curve.At(f)));
                    if (f is 100 or 1000 or 5000) detail += $"{f:0} Hz {g:+0.00;-0.00} dB  ";
                }
                Check("EQ passes through its points", worst < 0.1, detail + $"(worst error anywhere {worst:F3} dB)");
            }
            {
                // needle: a 12 dB cut only 20 Hz wide, and a step in the deep bass
                var needle = new[] { new BandParams(1, 990, 0), new BandParams(2, 1000, -12), new BandParams(3, 1010, 0) };
                var bass = new[] { new BandParams(1, 30, 6), new BandParams(2, 45, 0) };
                double Gain(BandParams[] pts, double f)
                {
                    var chain = new DspChain(fs);
                    chain.SetParams(DspParams.Default with { Bands = pts, EqRounding = 0 });
                    chain.WaitForEq();
                    var y = RunChain(chain, M.Sine(f, fs, fs * 3, 0.1));
                    return M.Db(M.Tone(y.AsSpan(fs * 2, fs), f, fs) / 0.1);
                }
                double atNeedle = Gain(needle, 1000), beside = Gain(needle, 980), lowOn = Gain(bass, 30), lowOff = Gain(bass, 50);
                Check("EQ resolves fine detail", Math.Abs(atNeedle + 12) < 1.0 && Math.Abs(beside) < 0.3 && Math.Abs(lowOn - 6) < 0.3 && Math.Abs(lowOff) < 0.3,
                    $"needle {atNeedle:F1} dB at 1000 Hz, {beside:F2} dB at 980 Hz; bass step {lowOn:F2} dB at 30 Hz, {lowOff:F2} dB at 50 Hz");
            }
            {
                // no added delay, and changing the curve mid-stream does not click
                var chain = new DspChain(fs);
                chain.SetParams(DspParams.Default with { Bands = new[] { new BandParams(1, 200, 6), new BandParams(2, 2000, -6) } });
                chain.WaitForEq();
                RunChain(chain, new double[fs / 2]);
                var imp = new double[8192]; imp[100] = 0.5;
                var y = RunChain(chain, imp);
                int first = Array.FindIndex(y, v => Math.Abs(v) > 1e-9);
                int peak = Array.IndexOf(y, y.Max(Math.Abs)) >= 0 ? Array.IndexOf(y.Select(Math.Abs).ToArray(), y.Max(Math.Abs)) : -1;
                var tone = M.Sine(300, fs, fs, 0.3);
                var a1 = RunChain(chain, tone.AsSpan(0, fs / 2).ToArray());
                chain.SetParams(DspParams.Default with { Bands = new[] { new BandParams(1, 200, -6), new BandParams(2, 2000, 6) } });
                chain.WaitForEq();
                var a2 = RunChain(chain, tone.AsSpan(fs / 2).ToArray());
                double maxStep = 0;
                var all = a1.Concat(a2).ToArray();
                for (int i = fs / 4; i < all.Length; i++) maxStep = Math.Max(maxStep, Math.Abs(all[i] - all[i - 1]));
                double natural = 0.3 * 2 * 2 * Math.PI * 300 / fs;       // largest step a +6 dB 300 Hz tone makes on its own
                Check("EQ adds no delay; curve changes are click-free", first - 100 == chain.LatencySamples && peak == first && maxStep < natural * 1.3,
                    $"response starts at {first - 100} samples (chain latency {chain.LatencySamples}); largest step during change {maxStep / natural:F2}× a clean tone's");
            }

            // Clean path is transparent and latency is as reported
            {
                var chain = new DspChain(fs);
                chain.SetParams(DspParams.Default);
                var imp = new double[4096]; imp[100] = 0.5;
                var y = RunChain(chain, imp);
                int peak = Array.IndexOf(y, y.Max());
                Check("Clean path: latency as reported", peak - 100 == chain.LatencySamples && Math.Abs(y[peak] - 0.5) < 1e-12, $"{peak - 100} samples ({(peak - 100) * 1000.0 / fs:F2} ms), x{chain.OversampleFactor} oversampling");
            }

            // The oversampler alone, with the curve mixed out, must be a pure delay (checks flatness and timing)
            foreach (double f in new[] { 1000.0, 15000.0 })
            {
                var sh = new Oversampler(fs);
                var x = M.Sine(f, fs, n, 0.5);
                var y = (double[])x.Clone();
                var lin = new TubeCoefs { Gain = 1, Dry = 1, Wet = 0 };
                var shaper = ValveShaper.Create(lin, lin, n, 0);
                sh.Process(y, 0, ref shaper);
                double err = 0, sig = 0;
                int d = sh.DelaySamples;
                for (int i = skip; i < n; i++) { double e = y[i] - x[i - d]; err += e * e; sig += x[i - d] * x[i - d]; }
                double db = 10 * Math.Log10(Math.Max(err, 1e-300) / sig);
                Check($"Oversampler transparent at {f / 1000:0.#} kHz", db < -80, $"error {db:F1} dB");
            }

            // Harmonics match the model, and aliasing is low
            {
                var tp = new TubeParams(true, 6, 0.6, 0, 0, 1.0);
                var chain = new DspChain(fs);
                chain.SetParams(DspParams.Default with { Tube = tp });
                double amp = Math.Pow(10, -12 / 20.0);
                var y = RunChain(chain, M.Sine(1000, fs, n, amp));
                var seg = y.AsSpan(skip);
                double fund = M.Tone(seg, 1000, fs);
                double h2 = M.Db(M.Tone(seg, 2000, fs) / fund), h3 = M.Db(M.Tone(seg, 3000, fs) / fund);
                var model = TubeMath.Harmonics(tp, -12, 4);
                Check("Valve 2nd harmonic matches model", Math.Abs(h2 - model[0]) < 0.5, $"{h2:F1} dBc (model {model[0]:F1})");
                Check("Valve 3rd harmonic matches model", Math.Abs(h3 - model[1]) < 0.5, $"{h3:F1} dBc (model {model[1]:F1})");

                var pp = new TubeParams(true, 6, 0.0, 0, 0, 1.0);
                var ppModel = TubeMath.Harmonics(pp, -12, 4);
                Check("Push-pull setting: no even harmonics", ppModel[0] < -120 && ppModel[1] > -60, $"H2 {ppModel[0]:F0} dBc, H3 {ppModel[1]:F1} dBc");
            }
            {
                // Hard-driven 6 kHz tone: harmonics above Nyquist must not fold back. Measure a few fold-back frequencies.
                var chain = new DspChain(fs);
                chain.SetParams(DspParams.Default with { Tube = new TubeParams(true, 24, 0.5, 0, 0, 1.0) });
                double f0 = 6100;
                var y = RunChain(chain, M.Sine(f0, fs, n, 0.5));
                var seg = y.AsSpan(skip, fs);      // exactly one second: every whole-Hz frequency is orthogonal
                double fund = M.Tone(seg, f0, fs);
                double worst = -300;
                int tested = 0;
                for (int k = 2; k <= 60; k++)
                {
                    double fh = k * f0;
                    if (fh < fs / 2.0) continue;
                    double fa = Math.Abs(fh - Math.Round(fh / fs) * fs);      // where it would alias to
                    if (fa < 20 || fa > 20000 || Math.Abs(fa % f0) < 1 || Math.Abs(fa % f0 - f0) < 1) continue;
                    worst = Math.Max(worst, M.Db(M.Tone(seg, fa, fs) / fund));
                    tested++;
                }
                Check("Aliasing at heavy drive (6.1 kHz, +24 dB)", tested > 5 && worst < -80, $"worst of {tested} fold-back tones {worst:F1} dBc");
            }

            // Limiter holds the ceiling
            {
                var chain = new DspChain(fs);
                chain.SetParams(DspParams.Default with { PreampDb = 14 });
                var x = M.Sine(60, fs, n, 0.9);
                for (int i = fs; i < fs + 40; i++) x[i] = i % 2 == 0 ? 0.99 : -0.99;   // nasty burst
                var y = RunChain(chain, x);
                double max = y.Max(Math.Abs);
                Check("Limiter ceiling held at +14 dB overload", max <= 1.0 + 1e-12, $"max {M.Db(max):F3} dBFS, reduction {-M.Db(chain.TakeLimiterGain()):F1} dB");
                var quiet = new DspChain(fs); quiet.SetParams(DspParams.Default);
                var z = RunChain(quiet, M.Sine(1000, fs, n, 1.0));     // full scale must still pass untouched
                Check("Limiter idle at full scale", quiet.TakeLimiterGain() == 1f && M.ResidualDb(z.AsSpan(skip), 1000, fs) < -140, $"distortion {M.ResidualDb(z.AsSpan(skip), 1000, fs):F0} dB");
            }

            // Listening volume is applied after the valve stage: harmonics (relative) must not change with it
            {
                var tp = new TubeParams(true, 6, 0.6, 0, 0, 1.0);
                var chain = new DspChain(fs);
                chain.SetParams(DspParams.Default with { Tube = tp });
                chain.SetVolume(0.1);
                var y = RunChain(chain, M.Sine(1000, fs, n, 0.25));
                var seg = y.AsSpan(skip, fs);
                double h2 = M.Db(M.Tone(seg, 2000, fs) / M.Tone(seg, 1000, fs));
                double model = TubeMath.Harmonics(tp, M.Db(0.25), 2)[0];
                Check("Volume does not change valve behaviour", Math.Abs(h2 - model) < 0.5 && Math.Abs(M.Db(M.Tone(seg, 1000, fs) / M.Tone(RunChain(Ref(fs, tp), M.Sine(1000, fs, n, 0.25)).AsSpan(skip, fs), 1000, fs)) + 20) < 0.01, $"H2 {h2:F1} dBc at 10% volume (model {model:F1})");
            }

            // CPU
            {
                var chain = new DspChain(fs);
                var bands = Enumerable.Range(0, 12).Select(i => new BandParams(i, 50 * Math.Pow(1.7, i), i % 2 == 0 ? 3 : -3)).ToArray();
                chain.SetParams(DspParams.Default with { Bands = bands, Tube = new TubeParams(true, 12, 0.6, 0.5, 0.5, 1) });
                chain.WaitForEq();
                var x = M.Sine(440, fs, fs * 10, 0.3);
                RunChain(chain, x.AsSpan(0, fs).ToArray());
                var sw = Stopwatch.StartNew();
                RunChain(chain, x);
                double pct = sw.Elapsed.TotalSeconds / 10 * 100;
                Check("CPU: 12-point EQ + valve stage", pct < 25, $"{pct:F1}% of one core");
            }
        }

        // ---- auto level, sweep, drive calibration (48 kHz) ----
        Console.WriteLine("--- auto level / sweep / drive ---");
        {
            const int fs = 48000;
            var rnd = new Random(7);
            // Music-like test signal: noise with a falling spectrum, peaking close to full scale.
            double[] Noise(int n)
            {
                var x = new double[n]; double lp = 0, lp2 = 0;
                for (int i = 0; i < n; i++)
                {
                    double w = rnd.NextDouble() * 2 - 1;
                    lp += 0.05 * (w - lp); lp2 += 0.4 * (w - lp2);
                    x[i] = 2.4 * lp + 0.35 * lp2;
                }
                double pk = x.Max(Math.Abs);
                for (int i = 0; i < n; i++) x[i] *= 0.97 / pk;
                return x;
            }
            double Rms(ReadOnlySpan<double> x) { double s = 0; foreach (double v in x) s += v * v; return Math.Sqrt(s / x.Length); }

            var src = Noise(fs * 12);
            var tail = (fs * 8)..;       // judge the settled state

            var flat = new DspChain(fs);
            flat.SetParams(DspParams.Default with { AutoLevel = true });
            var yf = RunChain(flat, src);
            Check("Auto level: flat settings left alone", Math.Abs(flat.AutoGainDb) < 0.05 && Math.Abs(M.Db(Rms(yf.AsSpan()[tail]) / Rms(src.AsSpan()[tail]))) < 0.05, $"auto {flat.AutoGainDb:F2} dB");

            // Broad +9 dB midrange boost: without help this would run 9 dB into the limiter.
            var boost = new[] { new BandParams(1, 150, 0), new BandParams(2, 900, 9), new BandParams(3, 5000, 0) };
            var manual = new DspChain(fs);
            manual.SetParams(DspParams.Default with { Bands = boost });
            manual.WaitForEq();
            RunChain(manual, src.AsSpan(0, fs * 8).ToArray()); manual.TakeLimiterGain();
            RunChain(manual, src.AsSpan()[tail].ToArray());
            double manualGr = -M.Db(manual.TakeLimiterGain());

            var auto = new DspChain(fs);
            auto.SetParams(DspParams.Default with { Bands = boost, AutoLevel = true });
            auto.WaitForEq();
            RunChain(auto, src.AsSpan(0, fs * 8).ToArray()); auto.TakeLimiterGain();
            var ya = RunChain(auto, src.AsSpan()[tail].ToArray());
            double autoGr = -M.Db(auto.TakeLimiterGain());
            Check("Auto level: boost no longer hits the limiter", autoGr < 0.5 && manualGr > 3 && ya.Max(Math.Abs) <= 1.0, $"limiter {autoGr:F2} dB (was {manualGr:F1} dB by hand), auto {auto.AutoGainDb:F1} dB");

            // Bass shelf: loudness-weighted match should take off far less than the 8 dB of the shelf itself.
            var shelf = new DspChain(fs);
            shelf.SetParams(DspParams.Default with { Bands = new[] { new BandParams(1, 60, 8), new BandParams(2, 160, 0) }, AutoLevel = true });
            shelf.WaitForEq();
            RunChain(shelf, src);
            Check("Auto level: bass boost costs little level", shelf.AutoGainDb < 0 && shelf.AutoGainDb > -6.5, $"auto {shelf.AutoGainDb:F1} dB for +8 dB below 60 Hz");

            // Heavy valve drive with auto level: output as loud as input, no clipping.
            var hot = new DspChain(fs);
            hot.SetParams(DspParams.Default with { Tube = new TubeParams(true, 12, 0.8, 0.5, 0.5, 1), AutoLevel = true });
            RunChain(hot, src.AsSpan(0, fs * 8).ToArray()); hot.TakeLimiterGain();
            var yh = RunChain(hot, src.AsSpan()[tail].ToArray());
            Check("Auto level: heavy drive stays unclipped", -M.Db(hot.TakeLimiterGain()) < 0.5 && yh.Max(Math.Abs) <= 1.0, $"limiter {-M.Db(hot.TakeLimiterGain()):F2} dB, auto {hot.AutoGainDb:F1} dB, level change {M.Db(Rms(yh) / Rms(src.AsSpan()[tail])):F1} dB");

            // Sweep
            var sw = new DspChain(fs);
            sw.SetParams(DspParams.Default);
            sw.SetSweep(new SweepConfig(true, 2, -20, false));
            var ys = RunChain(sw, new double[fs]);       // one second = half a pass
            double hzMid = sw.SweepHz;
            double pkS = ys.AsSpan(fs / 2).ToArray().Max(Math.Abs);
            sw.SetSweep(new SweepConfig(true, 2, -20, true));
            RunChain(sw, new double[fs / 4]); double hold1 = sw.SweepHz; RunChain(sw, new double[fs / 4]); double hold2 = sw.SweepHz;
            sw.SetSweep(new SweepConfig(true, 2, -20, true, 3000));
            RunChain(sw, new double[fs / 5]); double dragged = sw.SweepHz;
            sw.SetSweep(new SweepConfig(true, 2, -20, true));
            RunChain(sw, new double[fs / 5]); double stayed = sw.SweepHz;
            Check("Sweep: tone can be dragged to a frequency and stays", Math.Abs(dragged - 3000) < 3 && Math.Abs(stayed - dragged) < 0.01, $"asked for 3000 Hz, got {dragged:F1} Hz, then {stayed:F1} Hz after letting go");
            sw.SetSweep(SweepConfig.Off);
            var yo = RunChain(sw, new double[fs / 2]);
            Check("Sweep: tone at -20 dB, half-way ≈ 632 Hz", Math.Abs(M.Db(pkS) + 20) < 0.1 && Math.Abs(hzMid - 632.5) < 12, $"peak {M.Db(pkS):F2} dBFS, at {hzMid:F0} Hz after half a pass");
            Check("Sweep: hold parks it, off fades to silence", Math.Abs(hold1 - hold2) < 0.01 && yo.AsSpan(fs / 4).ToArray().Max(Math.Abs) == 0 && sw.SweepHz == 0, $"held at {hold1:F0} Hz");

            // Drive calibration
            var clean = TubeMath.Harmonics(new TubeParams(true, TubeMath.MinDriveDb, 1, 0, 0, 1), 0, 3);
            Check("Drive at 0% is exactly clean", clean.Max() <= -159, $"harmonics {clean.Max():F0} dBc");
            Console.WriteLine("      drive   |  full-scale tone: H2 / H3 (single-ended)   H3 (push-pull)  |  quiet (-14 dB) tone: H2");
            foreach (int pct in new[] { 10, 20, 30, 40, 50, 65, 80, 100 })
            {
                double db = TubeMath.MinDriveDb + (TubeMath.MaxDriveDb - TubeMath.MinDriveDb) * pct / 100.0;
                var se = TubeMath.Harmonics(new TubeParams(true, db, 1, 0, 0, 1), 0, 3);
                var pp = TubeMath.Harmonics(new TubeParams(true, db, 0, 0, 0, 1), 0, 3);
                var q = TubeMath.Harmonics(new TubeParams(true, db, 1, 0, 0, 1), -14, 3);
                string Pc(double d) => $"{Math.Pow(10, d / 20) * 100,5:F2}%";
                Console.WriteLine($"      {pct,3}% ({db,5:F1} dB) |  {Pc(se[0])} / {Pc(se[1])}                    {Pc(pp[1])}          |  {Pc(q[0])}");
            }
        }

        // ---- the effects (48 kHz) ----
        Console.WriteLine("--- effects ---");
        {
            const int fs = 48000;
            int n = fs * 2, skip = fs / 2;
            DspChain Chain(TubeParams? tube = null, TapeParams? tape = null, FxParams? fx = null)
            {
                var c = new DspChain(fs);
                c.SetParams(DspParams.Default with { Tube = tube ?? TubeParams.Off, Tape = tape, Fx = fx });
                return c;
            }
            double Level(double[] y, double f) => M.Db(M.Tone(y.AsSpan(skip, fs), f, fs));
            var fxOff = FxParams.Off;

            // every section switched on with its amounts at zero must change nothing
            {
                var x = M.Sine(1000, fs, n, 0.5);
                var a = RunChain(Chain(), x);
                var b = RunChain(Chain(fx: new FxParams(true, 0, 0, 3500, true, 0, 90, true, 0, true, 0, 6)), x);
                double err = 0, sig = 0;
                for (int i = skip; i < n; i++) { err += (a[i] - b[i]) * (a[i] - b[i]); sig += a[i] * a[i]; }
                double db = 10 * Math.Log10(Math.Max(err, 1e-300) / sig);
                // Tape at zero passes through its band-split, which may turn the phase but must not change any level
                double worst = 0;
                foreach (double f in new[] { 100.0, 1000, 4500, 9000, 16000 })
                {
                    var y = RunChain(Chain(tape: new TapeParams(true, 0, 0, 0)), M.Sine(f, fs, n, 0.5));
                    worst = Math.Max(worst, Math.Abs(M.Db(M.Tone(y.AsSpan(skip, fs), f, fs) / 0.5)));
                    worst = Math.Max(worst, M.ResidualDb(y.AsSpan(skip, fs), f, fs) < -90 ? 0 : 99);
                }
                Check("All effects on at zero = no change", db < -90 && worst < 0.1, $"difference {db:F1} dB; tape at zero changes levels by at most {worst:F3} dB");
            }

            // valve feedback
            {
                var tp = new TubeParams(true, -8, 0.8, 0, 0, 1.0, 0.6);
                var y = RunChain(Chain(tube: tp), M.Sine(1000, fs, n, 0.5));
                var seg = y.AsSpan(skip, fs);
                double h2 = M.Db(M.Tone(seg, 2000, fs) / M.Tone(seg, 1000, fs)), h3 = M.Db(M.Tone(seg, 3000, fs) / M.Tone(seg, 1000, fs));
                var model = TubeMath.Harmonics(tp, M.Db(0.5), 3);
                Check("Feedback: audio matches the model", Math.Abs(h2 - model[0]) < 0.5 && Math.Abs(h3 - model[1]) < 1.0, $"H2 {h2:F1} (model {model[0]:F1}), H3 {h3:F1} (model {model[1]:F1}) dBc");
                double none = TubeMath.Harmonics(tp with { Feedback = 0 }, -12, 2)[0], full = TubeMath.Harmonics(tp with { Feedback = 1 }, -12, 2)[0];
                Check("Feedback: cleans up below clipping", none - full > 12, $"H2 {none:F1} → {full:F1} dBc with full feedback");
                var hard0 = TubeMath.Harmonics(new TubeParams(true, 12, 0, 0, 0, 1, 0), 0, 8);
                var hard1 = TubeMath.Harmonics(new TubeParams(true, 12, 0, 0, 0, 1, 1), 0, 8);
                Check("Feedback: harder clipping when overdriven", hard1[5] > hard0[5] + 3, $"7th harmonic {hard0[5]:F1} → {hard1[5]:F1} dBc");
            }

            // tape
            {
                var y = RunChain(Chain(tape: new TapeParams(true, 0.5, 0, 0)), M.Sine(1000, fs, n, 0.5));
                var seg = y.AsSpan(skip, fs);
                double f1 = M.Tone(seg, 1000, fs);
                double h2 = M.Db(M.Tone(seg, 2000, fs) / f1), h3 = M.Db(M.Tone(seg, 3000, fs) / f1);
                Check("Tape: odd harmonics only", h2 < -100 && h3 > -45 && h3 < -20, $"H2 {h2:F0} dBc, H3 {h3:F1} dBc");

                // Treble on its own, with Drive at zero: typical loud treble (-20 dB) is squashed, quiet treble and bass are not
                var tp = new TapeParams(true, 0, 1.0, 0);
                double loudHf = Level(RunChain(Chain(tape: tp), M.Sine(8000, fs, n, 0.1)), 8000) - M.Db(0.1);
                double quietHf = Level(RunChain(Chain(tape: tp), M.Sine(8000, fs, n, 0.002)), 8000) - M.Db(0.002);
                double loudLf = Level(RunChain(Chain(tape: tp), M.Sine(200, fs, n, 0.7)), 200) - M.Db(0.7);
                double half = Level(RunChain(Chain(tape: tp with { Squash = 0.5 }), M.Sine(8000, fs, n, 0.1)), 8000) - M.Db(0.1);
                Check("Tape treble: works without Drive; loud squashed, quiet and bass untouched", loudHf < -8 && half < -2 && half > loudHf + 3 && Math.Abs(quietHf) < 0.6 && Math.Abs(loudLf) < 0.2,
                    $"8 kHz at -20 dB: {half:F1} dB at half, {loudHf:F1} dB at full; 8 kHz at -54 dB: {quietHf:F2} dB; 200 Hz loud: {loudLf:F2} dB");
            }

            // wow and flutter
            {
                var steady = RunChain(Chain(tape: new TapeParams(true, 0, 0, 0)), M.Sine(1000, fs, fs * 6, 0.5));
                var wob = RunChain(Chain(tape: new TapeParams(true, 0, 0, 1.0)), M.Sine(1000, fs, fs * 6, 0.5));
                // pitch from zero-crossing spacing over the last three seconds
                double minF = 1e9, maxF = 0; int last = -1; int count = 0, window = 0;
                for (int i = fs * 3; i < wob.Length - 1; i++)
                {
                    if (wob[i] <= 0 && wob[i + 1] > 0)
                    {
                        if (last >= 0 && ++count == 40) { double f = 40.0 * fs / (i - window); minF = Math.Min(minF, f); maxF = Math.Max(maxF, f); count = 0; window = i; }
                        if (last < 0) window = i;
                        last = i;
                    }
                }
                double dev = (maxF - minF) / 2 / 1000 * 100;
                double rmsA = Math.Sqrt(steady.AsSpan(fs * 3).ToArray().Sum(v => v * v)), rmsB = Math.Sqrt(wob.AsSpan(fs * 3).ToArray().Sum(v => v * v));
                Check("Wow & flutter: pitch wanders, level does not", dev > 2.0 && dev < 6.0 && Math.Abs(M.Db(rmsB / rmsA)) < 0.2 && M.ResidualDb(steady.AsSpan(fs * 3, fs), 1000, fs) < -90,
                    $"±{dev:F2}% pitch at full, level change {M.Db(rmsB / rmsA):F2} dB");
            }

            // transient softener: tone bursts
            {
                var x = new double[fs * 3];
                for (int i = 0; i < x.Length; i++) x[i] = (i % (fs / 2)) < fs / 4 ? 0.5 * Math.Sin(2 * Math.PI * 1000 * i / fs) : 0.01 * Math.Sin(2 * Math.PI * 1000 * i / fs);
                double Attack(double amount)
                {
                    var c = Chain(fx: fxOff with { SoftenOn = true, Transient = amount, Deharsh = 0 });
                    var y = RunChain(c, x);
                    int on = fs * 2 + c.LatencySamples;     // a burst starts every half second
                    double early = y.AsSpan(on, fs / 200).ToArray().Max(Math.Abs), late = y.AsSpan(on + fs / 8, fs / 10).ToArray().Max(Math.Abs);
                    return M.Db(early / late);
                }
                double off = Attack(0), full = Attack(1);
                Check("Transient softener: attack rounded, sustain kept", Math.Abs(off) < 0.2 && full < -6, $"first 5 ms vs sustain: {off:F1} dB off, {full:F1} dB at full");
            }

            // de-harsh
            {
                var fx = fxOff with { SoftenOn = true, Transient = 0, Deharsh = 1 };
                double alone = Level(RunChain(Chain(fx: fx), M.Sine(3500, fs, n, 0.3)), 3500) - M.Db(0.3);
                var mix = M.Sine(100, fs, n, 0.5); var hf = M.Sine(3500, fs, n, 0.005);
                for (int i = 0; i < n; i++) mix[i] += hf[i];
                double buried = Level(RunChain(Chain(fx: fx), mix), 3500) - M.Db(0.005);
                Check("De-harsh: cuts when prominent, not when buried", alone < -6 && Math.Abs(buried) < 0.3, $"prominent {alone:F1} dB, buried {buried:F2} dB");
            }

            // glue
            {
                var rnd = new Random(3);
                var loud = Enumerable.Range(0, n).Select(_ => (rnd.NextDouble() * 2 - 1) * 0.6).ToArray();
                var cLoud = Chain(fx: fxOff with { GlueOn = true, Glue = 1 });
                RunChain(cLoud, loud); cLoud.TakeGlueDb(); RunChain(cLoud, loud);
                double grLoud = cLoud.TakeGlueDb();
                var cQuiet = Chain(fx: fxOff with { GlueOn = true, Glue = 0.3 });
                var quiet = loud.Select(v => v * 0.01).ToArray();
                RunChain(cQuiet, quiet); cQuiet.TakeGlueDb(); RunChain(cQuiet, quiet);
                double grQuiet = cQuiet.TakeGlueDb();
                Check("Glue: leans on loud passages only", grLoud > 6 && grQuiet < 0.05, $"loud {grLoud:F1} dB at full, quiet {grQuiet:F2} dB at 30%");
            }

            // phantom bass
            {
                var fx = fxOff with { BassOn = true, BassAmount = 1, BassHz = 90 };
                var y = RunChain(Chain(fx: fx), M.Sine(50, fs, n, 0.3));
                var seg = y.AsSpan(skip, fs);
                double f0 = M.Tone(seg, 50, fs);
                double h2 = M.Db(M.Tone(seg, 100, fs) / f0), h3 = M.Db(M.Tone(seg, 150, fs) / f0), h4 = M.Db(M.Tone(seg, 200, fs) / f0);
                var mid = RunChain(Chain(fx: fx), M.Sine(1000, fs, n, 0.3));
                Check("Phantom bass: harmonics of low notes only", h2 > -25 && h3 > -25 && h4 > -35 && M.ResidualDb(mid.AsSpan(skip, fs), 1000, fs) < -90,
                    $"50 Hz note gains 100/150/200 Hz at {h2:F0}/{h3:F0}/{h4:F0} dBc; 1 kHz untouched ({M.ResidualDb(mid.AsSpan(skip, fs), 1000, fs):F0} dB)");
            }

            // reverb
            {
                const double decay = 4;
                var c = Chain(fx: fxOff with { SpaceOn = true, SpaceAmount = 1, SpaceDecay = decay });
                var imp = new double[fs * 8]; imp[1000] = 0.5;
                var rnd = new Random(5);
                for (int i = 1000; i < 1000 + fs / 20; i++) imp[i] = (rnd.NextDouble() * 2 - 1) * 0.5;      // 50 ms noise burst
                var y = RunChain(c, imp);
                double E(double t0, double t1) { double e = 0; for (int i = (int)(t0 * fs); i < (int)(t1 * fs); i++) e += y[i] * y[i]; return 10 * Math.Log10(e + 1e-300); }
                double drop = E(0.5, 1.0) - E(2.5, 3.0);
                Check("Reverb: tail decays at the set rate", Math.Abs(drop - 30) < 6 && y.Max(Math.Abs) < 1.0, $"{drop:F1} dB in 2 s (set: 30 dB), i.e. RT60 ≈ {120 / drop:F1} s for a setting of {decay} s");

                var noise = Enumerable.Range(0, fs * 12).Select(_ => (rnd.NextDouble() * 2 - 1) * 0.2).ToArray();
                foreach (double d in new[] { 2.0, 8.0 })
                {
                    var c2 = Chain(fx: fxOff with { SpaceOn = true, SpaceAmount = 1, SpaceDecay = d });
                    var dry = RunChain(Chain(), noise);
                    var wet = RunChain(c2, noise);
                    double ew = 0, ed = 0;
                    for (int i = fs * 10; i < noise.Length; i++) { ew += (wet[i] - dry[i]) * (wet[i] - dry[i]); ed += dry[i] * dry[i]; }
                    Console.WriteLine($"      reverb level at 100%, decay {d} s: {10 * Math.Log10(ew / ed):F1} dB relative to the direct sound");
                }
            }

            // CPU with everything running
            {
                var c = new DspChain(fs);
                var bands = Enumerable.Range(0, 12).Select(i => new BandParams(i, 50 * Math.Pow(1.7, i), i % 2 == 0 ? 3 : -3)).ToArray();
                c.SetParams(DspParams.Default with
                {
                    Bands = bands, AutoLevel = true,
                    Tube = new TubeParams(true, -8, 0.6, 0.5, 0.5, 1, 0.5),
                    Tape = new TapeParams(true, 0.5, 0.6, 0.4),
                    Fx = new FxParams(true, 0.5, 0.5, 3500, true, 0.5, 90, true, 0.5, true, 0.5, 8),
                });
                c.WaitForEq();
                var rnd = new Random(9);
                var x = Enumerable.Range(0, fs * 10).Select(_ => (rnd.NextDouble() * 2 - 1) * 0.5).ToArray();
                RunChain(c, x.AsSpan(0, fs).ToArray());
                var sw = Stopwatch.StartNew();
                var y = RunChain(c, x);
                double pct = sw.Elapsed.TotalSeconds / 10 * 100;
                Check("CPU: everything on at once", pct < 35 && y.All(double.IsFinite), $"{pct:F1}% of one core, latency {c.LatencySamples * 1000.0 / fs:F1} ms");
            }
        }

        // Resampler quality
        Console.WriteLine("--- resampler ---");
        foreach (var (inRate, outRate, adj) in new[] { (48000, 48000, 150e-6), (44100, 44100, -150e-6), (44100, 48000, 50e-6), (96000, 48000, 0.0), (48000, 96000, 0.0) })
        {
            foreach (double f in new[] { 1000.0, 15000.0 })
            {
                var ring = new SpscRing(1 << 20);
                var rs = new AsyncResampler(inRate, outRate) { Adjust = adj };
                int inN = inRate * 2;
                var src = new float[inN * 2];
                for (int i = 0; i < inN; i++) src[2 * i] = src[2 * i + 1] = (float)(0.5 * Math.Sin(2 * Math.PI * f * i / inRate));
                ring.Write(src);
                var outL = new List<double>();
                var l = new double[480]; var r = new double[480];
                while (rs.Process(ring, l, r)) outL.AddRange(l);
                double fOut = f * (1 + adj) * inRate / inRate;   // tone lands at f·(1+adj) relative to the output clock
                var seg = outL.Skip(4000).Take(outRate).ToArray();
                double res = M.ResidualDb(seg, f * (1 + adj), outRate);
                double g = M.Db(M.Tone(seg, f * (1 + adj), outRate) / 0.5);
                Check($"Resample {inRate}→{outRate} {(adj * 1e6):+0;-0;0} ppm @ {f / 1000:0.#} kHz", res < -118 && Math.Abs(g) < 0.01, $"noise {res:F1} dB, gain {g:F3} dB");
            }
        }

        Console.WriteLine(_fail == 0 ? "\nALL PASS" : $"\n{_fail} FAILED");
        Environment.ExitCode = _fail == 0 ? 0 : 1;
    }
}

static class DeviceChecks
{
    public static void Probe()
    {
        var renders = Devices.List(DataFlow.Render);
        var captures = Devices.List(DataFlow.Capture);
        string? def = Devices.DefaultRenderId();
        Console.WriteLine("Playback devices:");
        foreach (var d in renders) Console.WriteLine($"  {(d.Id == def ? "*" : " ")} {d.Name}  [{d.SampleRate} Hz, {d.Channels} ch, {d.Bits} bit]");
        Console.WriteLine("Recording devices:");
        foreach (var d in captures) Console.WriteLine($"    {d.Name}  [{d.SampleRate} Hz, {d.Channels} ch, {d.Bits} bit]");
        Console.WriteLine($"Cable playback side: {Devices.FindCableRender(renders)?.Name ?? "NOT FOUND"}");
        Console.WriteLine($"Cable recording side: {Devices.FindCableCapture(captures)?.Name ?? "NOT FOUND"}");

        using var en = new MMDeviceEnumerator();
        foreach (var d in renders)
        {
            using var dev = en.GetDevice(d.Id);
            var v = dev.AudioEndpointVolume;
            using var c = dev.CreateAudioClient();
            Console.WriteLine($"  {d.Name}: volume {v.MasterVolumeLevel:F1} dB ({v.MasterVolumeLevelScalar * 100:F0}%), range {v.VolumeRange.MinDecibels:F1}…{v.VolumeRange.MaxDecibels:F1} dB, mute {v.Mute}, hw {v.HardwareSupport}, period default {c.DefaultDevicePeriod / 10000.0:F2} ms min {c.MinimumDevicePeriod / 10000.0:F2} ms, mix {c.MixFormat}");
        }
    }

    /// <summary>
    /// Plays a tone into the cable only (nothing reaches the speakers) and measures what comes out the other side at
    /// different cable volume settings. Restores the volume afterwards.
    /// </summary>
    public static void CableVolume()
    {
        var renders = Devices.List(DataFlow.Render);
        var captures = Devices.List(DataFlow.Capture);
        var cr = Devices.FindCableRender(renders) ?? throw new Exception("cable playback side not found");
        var cc = Devices.FindCableCapture(captures) ?? throw new Exception("cable recording side not found");
        using var en = new MMDeviceEnumerator();
        using var rdev = en.GetDevice(cr.Id);
        using var cdev = en.GetDevice(cc.Id);
        var vol = rdev.AudioEndpointVolume;
        float original = vol.MasterVolumeLevelScalar;
        bool wasMuted = vol.Mute;
        try
        {
            using var player = new WasapiPlayerBuilder().WithDevice(rdev).WithSharedMode().WithEventSync().Build();
            int rate = player.DeviceMixFormat.SampleRate;
            player.Init(new ToneProvider(rate, 1000, 0.25f));
            using var rec = new WasapiRecorderBuilder().WithDevice(cdev).WithSharedMode().WithEventSync().Build();
            Console.WriteLine($"capture format: {rec.WaveFormat} | {rec.WaveFormat.GetType().Name} enc {rec.WaveFormat.Encoding} bits {rec.WaveFormat.BitsPerSample} ch {rec.WaveFormat.Channels}");
            double peak = 0; int chunks = 0, bytes = 0; int minChunk = int.MaxValue, maxChunk = 0;
            var wf = rec.WaveFormat;
            rec.DataAvailable += (data, flags, _, _) =>
            {
                chunks++; bytes += data.Length; minChunk = Math.Min(minChunk, data.Length); maxChunk = Math.Max(maxChunk, data.Length);
                if (wf.BitsPerSample == 32 && (flags & AudioClientBufferFlags.Silent) == 0)
                {
                    var s = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(data);
                    foreach (var v in s) peak = Math.Max(peak, Math.Abs(v));
                }
            };
            vol.Mute = false;
            rec.StartRecording();
            player.Play();
            foreach (float scalar in new[] { 1.0f, 0.5f, 0.2f })
            {
                vol.MasterVolumeLevelScalar = scalar;
                Thread.Sleep(400);
                peak = 0; chunks = 0; bytes = 0; minChunk = int.MaxValue; maxChunk = 0;
                Thread.Sleep(600);
                double db = vol.MasterVolumeLevel;
                Console.WriteLine($"cable volume {scalar * 100,3:F0}% = {db,6:F2} dB → captured peak {M.Db(peak / 0.25),6:F2} dB re tone | {chunks} chunks in 0.6 s, {minChunk / wf.BlockAlign}–{maxChunk / wf.BlockAlign} frames each");
            }
            player.Stop();
            Thread.Sleep(300);
            chunks = 0;
            Thread.Sleep(500);
            Console.WriteLine($"with nothing playing: {chunks} chunks in 0.5 s (0 would mean the cable stops delivering when idle)");
            rec.StopRecording();
        }
        finally
        {
            vol.MasterVolumeLevelScalar = original;
            vol.Mute = wasMuted;
        }
    }

    /// <summary>
    /// Read-only: reports each device's real output format (bit depth), then listens for one second to what the cable
    /// is delivering and to what is being sent to the real output, and reports the levels. Changes nothing.
    /// </summary>
    public static void Noise(string[]? argv = null)
    {
        var args = argv ?? Array.Empty<string>();
        var renders = Devices.List(DataFlow.Render);
        var captures = Devices.List(DataFlow.Capture);
        string? def = Devices.DefaultRenderId();
        using var en = new MMDeviceEnumerator();
        foreach (var d in args.Contains("formats") ? Enumerable.Empty<DeviceInfo>() : renders.Concat(captures))
        {
            using var dev = en.GetDevice(d.Id);
            string fmt = "?";
            try
            {
                var v = dev.Properties[PropertyKeys.PKEY_AudioEngine_DeviceFormat].Value;
                if (v is byte[] b && b.Length >= 16)
                {
                    int ch = BitConverter.ToUInt16(b, 2), rate = BitConverter.ToInt32(b, 4), bits = BitConverter.ToUInt16(b, 14);
                    int valid = b.Length >= 20 ? BitConverter.ToUInt16(b, 18) : bits;
                    fmt = $"{rate} Hz, {ch} ch, {bits}-bit container, {valid} valid bits";
                }
            }
            catch (Exception ex) { fmt = "unreadable: " + ex.Message; }
            using var vol = dev.AudioEndpointVolume;
            Console.WriteLine($"{(d.Id == def ? "*" : " ")} {d.Name}: device format {fmt}; volume {vol.MasterVolumeLevelScalar * 100:F0}% ({vol.MasterVolumeLevel:F1} dB)");
        }

        void Listen(string label, WasapiRecorder rec)
        {
            double sum = 0, peak = 0; long count = 0, nonZero = 0;
            var wf = rec.WaveFormat;
            rec.DataAvailable += (data, flags, _, _) =>
            {
                if (wf.BitsPerSample != 32) return;
                var f = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(data);
                foreach (float x in f) { sum += (double)x * x; peak = Math.Max(peak, Math.Abs(x)); count++; if (x != 0) nonZero++; }
            };
            rec.StartRecording();
            Thread.Sleep(1200);
            rec.StopRecording();
            string rms = count == 0 ? "no data" : nonZero == 0 ? "exact digital silence" : $"RMS {10 * Math.Log10(sum / count):F1} dBFS, peak {M.Db(peak):F1} dBFS";
            Console.WriteLine($"{label}: {rms} ({count} samples, {nonZero} non-zero)");
        }

        if (args.Contains("formats"))
        {
            // which output formats the S/PDIF device itself accepts (a query only; nothing is opened or changed)
            var sp = renders.FirstOrDefault(d => d.Name.Contains("S/PDIF", StringComparison.OrdinalIgnoreCase));
            if (sp != null)
            {
                using var sdev = en.GetDevice(sp.Id);
                using var client = sdev.CreateAudioClient();
                var pcm = new Guid("00000001-0000-0010-8000-00aa00389b71");
                foreach (int rate in new[] { 44100, 48000, 96000, 192000 })
                    foreach (var (container, valid) in new[] { (16, 16), (24, 24), (32, 24), (32, 32) })
                    {
                        bool ok = false;
                        try { ok = client.IsFormatSupported(AudioClientShareMode.Exclusive, new WaveFormatExtensible(rate, container, 2, pcm, valid, 3)); } catch { }
                        Console.WriteLine($"  S/PDIF accepts {valid}-bit (in {container}) {rate} Hz: {(ok ? "yes" : "no")}");
                    }
            }
            return;
        }

        var cc = Devices.FindCableCapture(captures);
        if (cc != null)
        {
            using var cdev = en.GetDevice(cc.Id);
            using var rec = new WasapiRecorderBuilder().WithDevice(cdev).WithSharedMode().WithEventSync().Build();
            Listen("what the cable hands over", rec);
        }
        var outDev = renders.FirstOrDefault(d => d.Name.Contains("S/PDIF", StringComparison.OrdinalIgnoreCase));
        if (outDev != null)
        {
            using var odev = en.GetDevice(outDev.Id);
            using var rec = new WasapiRecorderBuilder().WithDevice(odev).WithSharedMode().WithEventSync().WithLoopbackCapture().Build();
            Listen("what is sent to S/PDIF (before Windows volume and final conversion)", rec);
        }
        Console.WriteLine("Radiqualiser running: " + (System.Diagnostics.Process.GetProcessesByName("Radiqualiser").Length > 0));
    }

    /// <summary>Checks that the cable's volume slider and the real output's stay in step, both ways. Nudges by 1% and restores.</summary>
    public static void VolumeLink()
    {
        var renders = Devices.List(DataFlow.Render);
        var captures = Devices.List(DataFlow.Capture);
        var cr = Devices.FindCableRender(renders) ?? throw new Exception("cable playback side not found");
        var cc = Devices.FindCableCapture(captures) ?? throw new Exception("cable recording side not found");
        string? def = Devices.DefaultRenderId();
        var outDev = renders.FirstOrDefault(d => d.Id == def && !Devices.IsCable(d)) ?? throw new Exception("default output is not a real device");
        using var en = new MMDeviceEnumerator();
        using var cdev = en.GetDevice(cr.Id);
        using var odev = en.GetDevice(outDev.Id);
        var cv = cdev.AudioEndpointVolume; var ov = odev.AudioEndpointVolume;
        float original = ov.MasterVolumeLevelScalar;
        using var engine = new AudioEngine();
        engine.Start(cc.Id, outDev.Id, cr.Id, new EngineOptions());
        try
        {
            Thread.Sleep(300);
            Console.WriteLine($"start:           cable {cv.MasterVolumeLevelScalar * 100:F1}%  output {ov.MasterVolumeLevelScalar * 100:F1}%  (output was {original * 100:F1}%)");
            cv.MasterVolumeLevelScalar = original - 0.01f;
            Thread.Sleep(250);
            float o1 = ov.MasterVolumeLevelScalar;
            Console.WriteLine($"cable nudged -1%: cable {cv.MasterVolumeLevelScalar * 100:F1}%  output {o1 * 100:F1}%  → {(Math.Abs(o1 - (original - 0.01f)) < 0.002 ? "PASS" : "FAIL")}");
            ov.MasterVolumeLevelScalar = original;
            Thread.Sleep(250);
            float c2 = cv.MasterVolumeLevelScalar;
            Console.WriteLine($"output restored:  cable {c2 * 100:F1}%  output {ov.MasterVolumeLevelScalar * 100:F1}%  → {(Math.Abs(c2 - original) < 0.002 ? "PASS" : "FAIL")}");
            Thread.Sleep(400);
            Console.WriteLine($"settled:          cable {cv.MasterVolumeLevelScalar * 100:F1}%  output {ov.MasterVolumeLevelScalar * 100:F1}%");
        }
        finally
        {
            engine.Stop();
            ov.MasterVolumeLevelScalar = original;
        }
    }

    /// <summary>Runs the real engine. With "tone", a quiet tone is fed into the cable so the path carries signal.</summary>
    public static void RunEngine(int seconds, bool tone)
    {
        var renders = Devices.List(DataFlow.Render);
        var captures = Devices.List(DataFlow.Capture);
        var cr = Devices.FindCableRender(renders) ?? throw new Exception("cable playback side not found");
        var cc = Devices.FindCableCapture(captures) ?? throw new Exception("cable recording side not found");
        string? def = Devices.DefaultRenderId();
        var outDev = renders.FirstOrDefault(d => d.Id == def && !Devices.IsCable(d))
                     ?? renders.First(d => d.Name.Contains("S/PDIF", StringComparison.OrdinalIgnoreCase));
        Console.WriteLine($"cable: {cc.Name} → output: {outDev.Name}");

        using var engine = new AudioEngine();
        engine.Faulted += m => Console.WriteLine("FAULT: " + m);
        engine.SetParams(DspParams.Default with { Tube = new TubeParams(true, 6, 0.6, 0.3, 0.3, 1) });
        engine.Start(cc.Id, outDev.Id, cr.Id, new EngineOptions());
        if (tone) engine.SetSessionMute(true);      // keep the test tone out of the speakers; meters still see it
        Console.WriteLine(engine.Notes ?? "rates match");

        WasapiPlayer? tonePlayer = null;
        MMDeviceEnumerator? en = null; MMDevice? rdev = null;
        if (tone)
        {
            en = new MMDeviceEnumerator(); rdev = en.GetDevice(cr.Id);
            tonePlayer = new WasapiPlayerBuilder().WithDevice(rdev).WithSharedMode().WithEventSync().Build();
            tonePlayer.Init(new ToneProvider(tonePlayer.DeviceMixFormat.SampleRate, 440, 0.003f));
            tonePlayer.Play();
        }
        for (int i = 0; i < seconds * 2; i++)
        {
            Thread.Sleep(500);
            var s = engine.Stats();
            float inPk = engine.Chain!.TakeInputPeak(), outPk = engine.Chain.TakeOutputPeak();
            Console.WriteLine($"t={(i + 1) * 0.5,4:F1}s run={s.Running} {s.CaptureRate}→{s.OutputRate} buf {s.BufferMs,5:F1} ms drift {s.DriftPpm,7:F1} ppm under {s.Underruns} resync {s.Resyncs} over {s.Overflows} latency≈{s.TotalLatencyMs:F0} ms lowlat cap/out {s.CaptureLowLatency}/{s.OutputLowLatency} vol {s.VolumePercent:F0}%{(s.Muted ? " muted" : "")} in {M.Db(inPk):F1} out {M.Db(outPk):F1} dBFS");
        }
        tonePlayer?.Stop(); tonePlayer?.Dispose(); rdev?.Dispose(); en?.Dispose();
        if (tone) engine.SetSessionMute(false);
        engine.Stop();
        Console.WriteLine("stopped cleanly");
    }

    sealed class ToneProvider(int rate, double freq, float amp) : IWaveProvider
    {
        long _n;
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(rate, 2);
        public int Read(Span<byte> buffer)
        {
            var f = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(buffer);
            for (int i = 0; i < f.Length / 2; i++)
            {
                float v = amp * (float)Math.Sin(2 * Math.PI * freq * _n++ / rate);
                f[2 * i] = v; f[2 * i + 1] = v;
            }
            return buffer.Length;
        }
    }
}
