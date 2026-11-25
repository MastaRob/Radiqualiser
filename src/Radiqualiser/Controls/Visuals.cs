using System.Globalization;
using System.Windows;
using System.Windows.Media;
using Radiqualiser.Core.Dsp;

namespace Radiqualiser.Controls;

/// <summary>
/// Shows what the valve stage is doing: on the left its transfer curve (input across, output up; a straight diagonal
/// means "no change"), on the right the harmonics it adds to a pure tone.
/// </summary>
public sealed class HarmonicsView : FrameworkElement
{
    const double LevelDb = -12;
    const int Count = 8;

    static readonly Typeface Face = new("Segoe UI");
    static readonly Brush Bg = Fz(new SolidColorBrush(Color.FromRgb(0x15, 0x18, 0x1D)));
    static readonly Brush Dim = Fz(new SolidColorBrush(Color.FromRgb(0x7E, 0x86, 0x94)));
    static readonly Brush Even = Fz(new SolidColorBrush(Color.FromRgb(0xFF, 0xB4, 0x54)));
    static readonly Brush Odd = Fz(new SolidColorBrush(Color.FromRgb(0x5C, 0xC8, 0xFF)));
    static readonly Brush Off = Fz(new SolidColorBrush(Color.FromRgb(0x3A, 0x40, 0x4B)));
    static readonly Pen Grid = Fz(new Pen(Fz(new SolidColorBrush(Color.FromRgb(0x2C, 0x31, 0x3B))), 1));
    static readonly Pen Diagonal = Fz(new Pen(Fz(new SolidColorBrush(Color.FromRgb(0x4A, 0x51, 0x60))), 1) { DashStyle = DashStyles.Dash });
    static readonly Pen CurveOn = Fz(new Pen(Even, 2));
    static readonly Pen CurveOff = Fz(new Pen(Off, 2));

    static T Fz<T>(T f) where T : Freezable { f.Freeze(); return f; }

    MainViewModel? _vm;

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

    protected override void OnRender(DrawingContext dc)
    {
        double ppd = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        double w = ActualWidth, h = ActualHeight;
        dc.DrawRoundedRectangle(Bg, null, new Rect(0, 0, w, h), 6, 6);
        if (_vm == null || w < 120 || h < 60) return;

        var tp = _vm.GetTubeParams();
        bool on = tp.Enabled && !_vm.Bypass;
        var coefs = TubeMath.Coefs(tp);

        // ---- transfer curve ----
        double side = Math.Min(h - 16, w * 0.36);
        var box = new Rect(8, (h - side) / 2, side, side);
        dc.DrawLine(Grid, new Point(box.Left, box.Top + side / 2), new Point(box.Right, box.Top + side / 2));
        dc.DrawLine(Grid, new Point(box.Left + side / 2, box.Top), new Point(box.Left + side / 2, box.Bottom));
        dc.DrawLine(Diagonal, new Point(box.Left, box.Bottom), new Point(box.Right, box.Top));
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            const int n = 80;
            for (int i = 0; i <= n; i++)
            {
                double x = -1 + 2.0 * i / n;
                double y = Math.Clamp(coefs.Shape(x), -1, 1);
                var p = new Point(box.Left + (x + 1) / 2 * side, box.Bottom - (y + 1) / 2 * side);
                if (i == 0) c.BeginFigure(p, false, false); else c.LineTo(p, true, true);
            }
        }
        g.Freeze();
        dc.PushClip(new RectangleGeometry(box));
        dc.DrawGeometry(null, on ? CurveOn : CurveOff, g);
        dc.Pop();

        // ---- harmonic bars ----
        var harm = TubeMath.Harmonics(tp, LevelDb, Count);
        double left = box.Right + 30, right = w - 8, top = 8, bottom = h - 20;
        const double floorDb = -100;
        foreach (double db in new[] { 0.0, -40, -80 })
        {
            double y = Math.Round(top + (bottom - top) * (db / floorDb)) + 0.5;
            dc.DrawLine(Grid, new Point(left, y), new Point(right, y));
            var t = Text(db.ToString("0"), 9.5, Dim, ppd);
            dc.DrawText(t, new Point(left - t.Width - 4, y - t.Height / 2));
        }
        double slot = (right - left) / Count;
        for (int i = 0; i < Count; i++)
        {
            int order = i + 2;
            double db = Math.Max(floorDb, harm[i]);
            double bh = (bottom - top) * (1 - db / floorDb);
            var bar = new Rect(left + i * slot + slot * 0.2, bottom - bh, slot * 0.6, Math.Max(0, bh));
            dc.DrawRoundedRectangle(!on ? Off : order % 2 == 0 ? Even : Odd, null, bar, 2, 2);
            var t = Text(order.ToString(), 10, Dim, ppd);
            dc.DrawText(t, new Point(left + i * slot + (slot - t.Width) / 2, bottom + 3));
        }
    }

    static FormattedText Text(string s, double size, Brush brush, double ppd) =>
        new(s, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, Face, size, brush, ppd);
}

/// <summary>A horizontal level bar.</summary>
public sealed class MeterBar : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(nameof(Value), typeof(double), typeof(MeterBar),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty MinimumProperty = DependencyProperty.Register(nameof(Minimum), typeof(double), typeof(MeterBar),
        new FrameworkPropertyMetadata(-60.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(nameof(Maximum), typeof(double), typeof(MeterBar),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(nameof(Fill), typeof(Brush), typeof(MeterBar),
        new FrameworkPropertyMetadata(Brushes.Orange, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty HotFromProperty = DependencyProperty.Register(nameof(HotFrom), typeof(double), typeof(MeterBar),
        new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsRender));

    static readonly Brush Track = Fz(new SolidColorBrush(Color.FromRgb(0x24, 0x28, 0x30)));
    static readonly Brush Hot = Fz(new SolidColorBrush(Color.FromRgb(0xFF, 0x5C, 0x5C)));
    static T Fz<T>(T f) where T : Freezable { f.Freeze(); return f; }

    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public double Minimum { get => (double)GetValue(MinimumProperty); set => SetValue(MinimumProperty, value); }
    public double Maximum { get => (double)GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public Brush Fill { get => (Brush)GetValue(FillProperty); set => SetValue(FillProperty, value); }
    /// <summary>Above this value the bar turns red (e.g. the last decibel before full scale).</summary>
    public double HotFrom { get => (double)GetValue(HotFromProperty); set => SetValue(HotFromProperty, value); }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        dc.DrawRoundedRectangle(Track, null, new Rect(0, 0, w, h), h / 2, h / 2);
        double frac = Math.Clamp((Value - Minimum) / (Maximum - Minimum), 0, 1);
        if (frac <= 0.001) return;
        bool hot = !double.IsNaN(HotFrom) && Value >= HotFrom;
        dc.DrawRoundedRectangle(hot ? Hot : Fill, null, new Rect(0, 0, Math.Max(h, w * frac), h), h / 2, h / 2);
    }
}
