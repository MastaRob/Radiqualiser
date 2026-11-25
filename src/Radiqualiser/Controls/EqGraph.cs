using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Radiqualiser.Core.Dsp;

namespace Radiqualiser.Controls;

/// <summary>
/// The EQ curve editor. The line is drawn through the points: double-click to add one, drag to move it, right-click
/// for options. Beyond the outermost points the line runs flat. The live output spectrum is drawn behind it.
/// </summary>
public sealed class EqGraph : FrameworkElement
{
    const double MinF = 20, MaxF = 20000, RangeDb = 24;
    const double NodeRadius = 7;

    static readonly Typeface Face = new("Segoe UI");
    static readonly Brush BgBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x15, 0x18, 0x1D)));
    static readonly Brush TextDim = Frozen(new SolidColorBrush(Color.FromRgb(0x7E, 0x86, 0x94)));
    static readonly Brush TextBright = Frozen(new SolidColorBrush(Color.FromRgb(0xE6, 0xE8, 0xEC)));
    static readonly Pen GridMinor = FrozenPen(Color.FromRgb(0x20, 0x24, 0x2B), 1);
    static readonly Pen GridMajor = FrozenPen(Color.FromRgb(0x2C, 0x31, 0x3B), 1);
    static readonly Pen ZeroLine = FrozenPen(Color.FromRgb(0x4A, 0x51, 0x60), 1);
    static readonly Color Accent = Color.FromRgb(0xFF, 0xB4, 0x54);
    static readonly Color Cool = Color.FromRgb(0x5C, 0xC8, 0xFF);
    static readonly Pen CurvePen = FrozenPen(Accent, 2.2);
    static readonly Pen CurvePenOff = FrozenPen(Color.FromRgb(0x6A, 0x70, 0x7C), 2);
    static readonly Brush CurveFill = Frozen(new SolidColorBrush(Color.FromArgb(0x2A, 0xFF, 0xB4, 0x54)));
    static readonly Brush SpectrumFill = Frozen(new LinearGradientBrush(Color.FromArgb(0x50, 0x8A, 0x93, 0xA6), Color.FromArgb(0x10, 0x8A, 0x93, 0xA6), 90));
    static readonly Pen SpectrumPen = FrozenPen(Color.FromArgb(0x70, 0xA8, 0xB0, 0xC0), 1);
    static readonly Brush NodeFill = Frozen(new SolidColorBrush(Color.FromRgb(0x24, 0x29, 0x32)));
    static readonly Brush NodeSelFill = Frozen(new SolidColorBrush(Accent));
    static readonly Pen NodePen = FrozenPen(Accent, 1.8);
    static readonly Pen NodeSelPen = FrozenPen(Colors.White, 1.8);
    static readonly Pen NodeOffPen = FrozenPen(Color.FromRgb(0x6A, 0x70, 0x7C), 1.5);
    static readonly Pen TonePen = FrozenPen(Color.FromArgb(0xD0, 0x5C, 0xC8, 0xFF), 1.5);
    static readonly Brush ToneText = Frozen(new SolidColorBrush(Cool));
    static readonly Brush ToneHandle = Frozen(new SolidColorBrush(Cool));
    static readonly Brush ToneHandleHot = Frozen(new SolidColorBrush(Color.FromRgb(0xB5, 0xE6, 0xFF)));
    static readonly Pen ToneGrip = FrozenPen(Color.FromRgb(0x15, 0x18, 0x1D), 1.2);
    const double HandleW = 24, HandleH = 16;

    static T Frozen<T>(T f) where T : Freezable { f.Freeze(); return f; }
    static Pen FrozenPen(Color c, double w) => Frozen(new Pen(Frozen(new SolidColorBrush(c)), w) { LineJoin = PenLineJoin.Round });

    MainViewModel? _vm;
    BandVm? _drag, _hover;
    Point _dragOffset;
    bool _toneDrag, _toneHover;

    // a drag moves every selected point together; these remember where they all started
    readonly List<(BandVm Band, double Freq, double Gain)> _dragStart = new();
    double _grabFreq, _grabGain;
    Point _pressPoint;
    bool _dragMoved;
    BandVm? _collapseOnUp;

    void BeginDrag(BandVm grabbed, Point pt, bool exactlyOnPoint)
    {
        _drag = grabbed;
        _dragStart.Clear();
        foreach (var b in _vm!.Selection) _dragStart.Add((b, b.Freq, b.GainDb));
        _grabFreq = grabbed.Freq; _grabGain = grabbed.GainDb;
        var np = NodePoint(grabbed);
        _dragOffset = exactlyOnPoint ? default : new Point(pt.X - np.X, pt.Y - np.Y);
        _pressPoint = pt;
        _dragMoved = exactlyOnPoint;        // a point just created follows the mouse at once
        CaptureMouse();
    }
    double _toneDragHz;

    /// <summary>Where the tone marker is drawn: under the mouse while it is being dragged, otherwise where the engine says the tone is.</summary>
    double ToneHz => _vm == null ? 0 : _toneDrag ? _toneDragHz : _vm.SweepHz;

    Rect ToneHandleRect()
    {
        double x = XOf(Math.Clamp(ToneHz, MinF, MaxF));
        return new Rect(x - HandleW / 2, Plot.Top - 10, HandleW, HandleH);
    }

    bool OverToneHandle(Point pt)
    {
        if (ToneHz < MinF) return false;
        var r = ToneHandleRect();
        r.Inflate(4, 4);
        return r.Contains(pt);
    }

    public EqGraph()
    {
        Focusable = true;
        FocusVisualStyle = null;
        ClipToBounds = true;
        SnapsToDevicePixels = true;
    }

    public MainViewModel? Vm
    {
        get => _vm;
        set
        {
            if (_vm != null) _vm.VisualChanged -= InvalidateVisual;
            _vm = value;
            if (_vm != null) _vm.VisualChanged += InvalidateVisual;
            InvalidateVisual();
        }
    }

    // ------------------------------------------------------------------ geometry

    Rect Plot => new(42, 12, Math.Max(10, ActualWidth - 56), Math.Max(10, ActualHeight - 38));

    double XOf(double f) { var p = Plot; return p.Left + p.Width * Math.Log(f / MinF) / Math.Log(MaxF / MinF); }
    double FOf(double x) { var p = Plot; return MinF * Math.Pow(MaxF / MinF, Math.Clamp((x - p.Left) / p.Width, 0, 1)); }
    double YOf(double db) { var p = Plot; return p.Top + p.Height * (0.5 - db / (2 * RangeDb)); }
    double DbOf(double y) { var p = Plot; return Math.Clamp((0.5 - (y - p.Top) / p.Height) * 2 * RangeDb, -RangeDb, RangeDb); }

    Point NodePoint(BandVm b) => new(XOf(b.Freq), YOf(b.GainDb));

    BandVm? HitTestNode(Point pt)
    {
        if (_vm == null) return null;
        BandVm? best = null;
        double bestD = (NodeRadius + 5) * (NodeRadius + 5);
        foreach (var b in _vm.Bands)
        {
            var np = NodePoint(b);
            double d = (np.X - pt.X) * (np.X - pt.X) + (np.Y - pt.Y) * (np.Y - pt.Y);
            if (d < bestD || (d <= bestD && b == _vm.SelectedBand)) { best = b; bestD = d; }
        }
        return best;
    }

    // ------------------------------------------------------------------ drawing

    protected override void OnRender(DrawingContext dc)
    {
        double ppd = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        dc.DrawRoundedRectangle(BgBrush, null, new Rect(0, 0, ActualWidth, ActualHeight), 6, 6);
        var plot = Plot;
        if (plot.Width < 20 || plot.Height < 20) return;

        // grid
        foreach (double decade in new[] { 10.0, 100, 1000, 10000 })
        {
            for (int m = 1; m <= 9; m++)
            {
                double f = decade * m;
                if (f < MinF || f > MaxF) continue;
                double x = Math.Round(XOf(f)) + 0.5;
                dc.DrawLine(m == 1 ? GridMajor : GridMinor, new Point(x, plot.Top), new Point(x, plot.Bottom));
            }
        }
        foreach (var (f, label) in new[] { (20.0, "20"), (50, "50"), (100, "100"), (200, "200"), (500, "500"), (1000, "1k"), (2000, "2k"), (5000, "5k"), (10000, "10k"), (20000, "20k") })
        {
            var t = Text(label, 11, TextDim, ppd);
            dc.DrawText(t, new Point(XOf(f) - t.Width / 2, plot.Bottom + 5));
        }
        for (double db = -RangeDb; db <= RangeDb; db += 6)
        {
            double y = Math.Round(YOf(db)) + 0.5;
            dc.DrawLine(db == 0 ? ZeroLine : GridMajor, new Point(plot.Left, y), new Point(plot.Right, y));
            var t = Text(db > 0 ? "+" + db.ToString("0") : db.ToString("0"), 11, TextDim, ppd);
            dc.DrawText(t, new Point(plot.Left - t.Width - 7, y - t.Height / 2));
        }

        if (_vm == null) return;
        dc.PushClip(new RectangleGeometry(plot));

        // live spectrum
        var spec = _vm.Spectrum;
        if (spec != null)
        {
            var g = new StreamGeometry();
            using (var c = g.Open())
            {
                c.BeginFigure(new Point(plot.Left, plot.Bottom), true, true);
                for (int i = 0; i < spec.Length; i++)
                {
                    double x = plot.Left + plot.Width * (i + 0.5) / spec.Length;
                    double norm = Math.Clamp((spec[i] + 96) / 90.0, 0, 1);      // -96 … -6 dB over the plot height
                    c.LineTo(new Point(x, plot.Bottom - norm * plot.Height), true, true);
                }
                c.LineTo(new Point(plot.Right, plot.Bottom), false, false);
            }
            g.Freeze();
            dc.DrawGeometry(SpectrumFill, SpectrumPen, g);
        }

        // the curve: an even grid of frequencies plus every point's own, so corners land exactly on their points
        var bands = _vm.Bands;
        var curve = _vm.Curve();
        var freqs = new List<double>(420 + bands.Count);
        for (int k = 0; k <= 400; k++) freqs.Add(MinF * Math.Pow(MaxF / MinF, k / 400.0));
        foreach (var b in bands) if (b.Enabled && b.Freq > MinF && b.Freq < MaxF) freqs.Add(b.Freq);
        freqs.Sort();
        bool live = _vm.EqEnabled && !_vm.Bypass;
        if (live) dc.DrawGeometry(CurveFill, null, CurveGeometry(freqs, curve, plot, true));
        dc.DrawGeometry(null, live ? CurvePen : CurvePenOff, CurveGeometry(freqs, curve, plot, false));
        dc.Pop();

        // points (selected last so it sits on top)
        for (int pass = 0; pass < 2; pass++)
        {
            foreach (var b in bands)
            {
                bool isSel = _vm.IsSelected(b);
                if (isSel != (pass == 1)) continue;
                var np = NodePoint(b);
                double r = b == _hover || isSel ? NodeRadius + 1 : NodeRadius;
                // selected points are filled; the one shown in the Freq/Gain boxes also gets a white ring
                var pen = b == _vm.SelectedBand ? NodeSelPen : isSel ? NodePen : b.Enabled ? NodePen : NodeOffPen;
                dc.DrawEllipse(isSel ? NodeSelFill : NodeFill, pen, np, r, r);
            }
        }

        // test-tone marker, with a handle at the top to drag it by
        double tone = ToneHz;
        if (tone >= MinF)
        {
            double x = Math.Round(XOf(Math.Min(tone, MaxF))) + 0.5;
            dc.DrawLine(TonePen, new Point(x, plot.Top), new Point(x, plot.Bottom));
            var hr = ToneHandleRect();
            dc.DrawRoundedRectangle(_toneDrag || _toneHover ? ToneHandleHot : ToneHandle, null, hr, 4, 4);
            for (int k = -1; k <= 1; k++)
                dc.DrawLine(ToneGrip, new Point(x + k * 4, hr.Top + 4), new Point(x + k * 4, hr.Bottom - 4));
            var t = Text(FormatHz(tone), 11.5, ToneText, ppd, true);
            double tx = x + 6 + t.Width > plot.Right ? x - 6 - t.Width : x + 6;
            dc.DrawText(t, new Point(tx, plot.Bottom - t.Height - 5));
        }

        // readout
        var show = _drag ?? _hover ?? _vm.SelectedBand;
        if (show != null)
        {
            string s = $"{FormatHz(show.Freq)}   {show.GainDb:+0.0;-0.0;0.0} dB" + (show.Enabled ? "" : "   (off)");
            if (_vm.Selection.Count > 1) s = $"{_vm.Selection.Count} points selected   ·   " + s;
            dc.DrawText(Text(s, 12, TextBright, ppd), new Point(plot.Left + 10, plot.Top + 6));
        }
        else if (bands.Count == 0)
        {
            var t = Text("Double-click anywhere to add a point; the line is drawn through your points", 13, TextDim, ppd);
            dc.DrawText(t, new Point(plot.Left + (plot.Width - t.Width) / 2, plot.Top + plot.Height * 0.3));
        }
    }

    StreamGeometry CurveGeometry(List<double> freqs, EqCurve curve, Rect plot, bool closeToZero)
    {
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            double y0 = YOf(0);
            if (closeToZero) c.BeginFigure(new Point(plot.Left, y0), true, true);
            for (int k = 0; k < freqs.Count; k++)
            {
                var p = new Point(XOf(freqs[k]), YOf(curve.At(freqs[k])));
                if (k == 0 && !closeToZero) c.BeginFigure(p, false, false);
                else c.LineTo(p, true, true);
            }
            if (closeToZero) c.LineTo(new Point(plot.Right, y0), true, false);
        }
        g.Freeze();
        return g;
    }

    static FormattedText Text(string s, double size, Brush brush, double ppd, bool bold = false)
    {
        var t = new FormattedText(s, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, Face, size, brush, ppd);
        if (bold) t.SetFontWeight(FontWeights.SemiBold);
        return t;
    }

    public static string FormatHz(double f) => f >= 1000 ? $"{f / 1000:0.00} kHz" : $"{f:0.#} Hz";

    // ------------------------------------------------------------------ interaction

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (_vm == null) return;
        Focus();
        var pt = e.GetPosition(this);
        if (OverToneHandle(pt))
        {
            _toneDrag = true;
            _toneDragHz = ToneHz;
            CaptureMouse();
            InvalidateVisual();
            e.Handled = true;
            return;
        }
        var hit = HitTestNode(pt);
        var mods = Keyboard.Modifiers;
        _collapseOnUp = null;
        if (hit != null && (mods & ModifierKeys.Control) != 0)
        {
            _vm.ToggleSelect(hit);          // this one point in or out; nothing moves
        }
        else if (hit != null && (mods & ModifierKeys.Shift) != 0)
        {
            _vm.SelectRange(hit);           // from the last point clicked to this one, and all between
        }
        else if (hit != null)
        {
            if (_vm.IsSelected(hit) && _vm.Selection.Count > 1)
            {
                // Pressing on one of several selected points: a drag moves them all; a plain click (no movement)
                // goes back to just this one.
                _vm.MakePrimary(hit);
                _collapseOnUp = hit;
            }
            else
            {
                _vm.SelectedBand = hit;
            }
            BeginDrag(hit, pt, false);
        }
        else if (e.ClickCount == 2 && Plot.Contains(pt))
        {
            // On (or close to) the line: the new point goes exactly on it, so the shape does not change until it is
            // moved. Anywhere else: the point goes where the click was.
            double f = FOf(pt.X);
            double onLine = _vm.Curve().At(f);
            double db = Math.Abs(YOf(onLine) - pt.Y) < 10 ? Math.Round(onLine, 2) : Math.Round(DbOf(pt.Y), 1);
            BeginDrag(_vm.AddBand(f, db), pt, true);
        }
        else
        {
            _vm.SelectedBand = null;
        }
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_vm == null) return;
        var pt = e.GetPosition(this);
        if (_toneDrag && IsMouseCaptured)
        {
            _toneDragHz = FOf(pt.X);
            _vm.ScrubSweep(_toneDragHz);
            InvalidateVisual();
            return;
        }
        bool overHandle = _drag == null && OverToneHandle(pt);
        if (overHandle != _toneHover)
        {
            _toneHover = overHandle;
            InvalidateVisual();
        }
        if (overHandle)
        {
            if (_hover != null) _hover = null;
            Cursor = Cursors.SizeWE;
            return;
        }
        if (_drag != null && IsMouseCaptured)
        {
            if (!_dragMoved)
            {
                // no slack: the point follows the mouse from the first pixel (Windows also sends "moves" that go nowhere)
                if (pt == _pressPoint) return;
                _dragMoved = true;
            }
            bool gainOnly = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
            bool freqOnly = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
            double f = gainOnly ? _grabFreq : FOf(pt.X - _dragOffset.X);
            double db = _grabGain;
            if (!freqOnly)
            {
                db = DbOf(pt.Y - _dragOffset.Y);
                db = Math.Abs(db) < 0.25 ? 0 : Math.Round(db, 1);               // gentle snap to 0 dB
            }

            // Every selected point moves by the same musical interval and the same number of decibels as the one
            // under the mouse, stopping together at the edges so the shape of the group is kept.
            double ratio = f / _grabFreq, delta = db - _grabGain;
            foreach (var st in _dragStart)
            {
                ratio = Math.Clamp(ratio, MinF / st.Freq, MaxF / st.Freq);
                delta = Math.Clamp(delta, -RangeDb - st.Gain, RangeDb - st.Gain);
            }
            _vm.Batch(() =>
            {
                foreach (var st in _dragStart)
                {
                    st.Band.Freq = st.Freq * ratio;
                    st.Band.GainDb = st.Gain + delta;
                }
            });
            return;
        }
        var hover = HitTestNode(pt);
        if (hover != _hover)
        {
            _hover = hover;
            InvalidateVisual();
        }
        Cursor = hover != null ? Cursors.Hand : null;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (_toneDrag)
        {
            _toneDrag = false;
            ReleaseMouseCapture();
            InvalidateVisual();
            return;
        }
        if (_drag != null)
        {
            if (!_dragMoved && _collapseOnUp != null) _vm!.SelectedBand = _collapseOnUp;
            _drag = null;
            _collapseOnUp = null;
            _dragStart.Clear();
            ReleaseMouseCapture();
            InvalidateVisual();
        }
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        if (_hover != null || _toneHover) { _hover = null; _toneHover = false; Cursor = null; InvalidateVisual(); }
    }

    /// <summary>Wheel over a point: fine gain, a quarter of a decibel per notch.</summary>
    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        if (_vm == null) return;
        var target = HitTestNode(e.GetPosition(this));
        if (target == null) return;
        if (!_vm.IsSelected(target)) _vm.SelectedBand = target;
        double step = 0.25 * Math.Sign(e.Delta);
        _vm.Batch(() => { foreach (var b in _vm.Selection) b.GainDb += step; });
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (_vm == null) return;
        bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        if (ctrl && e.Key == Key.A) { _vm.SelectAll(); e.Handled = true; return; }
        if (e.Key == Key.Escape) { _vm.SelectedBand = null; e.Handled = true; return; }
        if (_vm.Selection.Count == 0) return;

        double gain = 0, freq = 1;
        switch (e.Key)
        {
            case Key.Delete or Key.Back: _vm.RemoveSelected(); e.Handled = true; return;
            case Key.Up: gain = shift ? 0.1 : 0.5; break;
            case Key.Down: gain = shift ? -0.1 : -0.5; break;
            case Key.Left: freq = 1 / (shift ? 1.002 : 1.02); break;
            case Key.Right: freq = shift ? 1.002 : 1.02; break;
            default: return;
        }
        _vm.Batch(() =>
        {
            foreach (var b in _vm.Selection) { b.GainDb += gain; b.Freq *= freq; }
        });
        e.Handled = true;
    }

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        if (_vm == null) return;
        var pt = e.GetPosition(this);
        var hit = HitTestNode(pt);
        var menu = new ContextMenu();
        if (hit != null)
        {
            if (!_vm.IsSelected(hit)) _vm.SelectedBand = hit; else _vm.MakePrimary(hit);
            int count = _vm.Selection.Count;
            bool turnOn = !hit.Enabled;
            var en = new MenuItem { Header = count > 1 ? $"Use these {count} points" : "Use this point", IsCheckable = true, IsChecked = hit.Enabled };
            en.Click += (_, _) => _vm.Batch(() => { foreach (var b in _vm.Selection) b.Enabled = turnOn; });
            menu.Items.Add(en);
            var reset = new MenuItem { Header = "Set to 0 dB" };
            reset.Click += (_, _) => _vm.Batch(() => { foreach (var b in _vm.Selection) b.GainDb = 0; });
            menu.Items.Add(reset);
            var del = new MenuItem { Header = count > 1 ? $"Delete {count} points" : "Delete", InputGestureText = "Del" };
            del.Click += (_, _) => _vm.RemoveSelected();
            menu.Items.Add(del);
        }
        else if (Plot.Contains(pt))
        {
            double f = FOf(pt.X), db = Math.Round(DbOf(pt.Y), 1);
            double onLine = Math.Round(_vm.Curve().At(f), 2);
            var here = new MenuItem { Header = "Add point here" };
            here.Click += (_, _) => _vm.AddBand(f, db);
            menu.Items.Add(here);
            var line = new MenuItem { Header = $"Add point on the line at {FormatHz(f)}" };
            line.Click += (_, _) => _vm.AddBand(f, onLine);
            menu.Items.Add(line);
            if (_vm.Bands.Count > 0)
            {
                menu.Items.Add(new Separator());
                var all = new MenuItem { Header = "Select all points", InputGestureText = "Ctrl+A" };
                all.Click += (_, _) => { _vm.SelectAll(); Focus(); };
                menu.Items.Add(all);
                var clear = new MenuItem { Header = "Remove all points" };
                clear.Click += (_, _) => _vm.ClearBands();
                menu.Items.Add(clear);
            }
        }
        else
        {
            return;
        }
        menu.PlacementTarget = this;
        menu.IsOpen = true;
        e.Handled = true;
    }
}
