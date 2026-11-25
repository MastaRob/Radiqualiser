using System.IO;
using System.Runtime;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Radiqualiser.Core.Dsp;

namespace Radiqualiser;

public partial class App : Application
{
    const string InstanceName = "Radiqualiser.SingleInstance.6f1d";
    Mutex? _mutex;
    EventWaitHandle? _showSignal;
    MainViewModel? _vm;
    MainWindow? _window;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        bool minimized = e.Args.Contains("--minimized");
        int shot = Array.IndexOf(e.Args, "--screenshot");
        string? shotPath = shot >= 0 && shot + 1 < e.Args.Length ? e.Args[shot + 1] : null;
        bool demo = e.Args.Contains("--demo");

        // Only one copy may own the audio path. A second launch just brings the first one's window forward.
        if (!demo)
        {
            _mutex = new Mutex(true, InstanceName, out bool first);
            if (!first)
            {
                try { EventWaitHandle.OpenExisting(InstanceName + ".show").Set(); } catch { /* first instance is mid-exit */ }
                Shutdown();
                return;
            }
            _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, InstanceName + ".show");
            var signal = _showSignal;
            new Thread(() =>
            {
                while (signal.WaitOne())
                    Dispatcher.BeginInvoke(() => _window?.ShowFromTray());
            }) { IsBackground = true, Name = "show-signal" }.Start();
        }

        GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;
        DispatcherUnhandledException += (_, _) => _vm?.EmergencyRestore();
        AppDomain.CurrentDomain.UnhandledException += (_, _) => _vm?.EmergencyRestore();
        SessionEnding += (_, _) => _vm?.Shutdown();

        _vm = new MainViewModel(persist: !demo);
        if (demo) LoadDemo(_vm);
        _window = new MainWindow(_vm, withTray: !demo);
        if (!minimized) _window.Show();

        if (!demo) await _vm.Initialise();

        if (shotPath != null)
        {
            await Task.Delay(1500);
            SaveScreenshot(_window, shotPath);
            if (demo) Shutdown(); else _window.ExitApp();
        }
    }

    static void LoadDemo(MainViewModel vm)
    {
        vm.AddBand(35, 6);
        vm.AddBand(120, 1.5);
        vm.AddBand(300, -3);
        vm.AddBand(610, 0);
        vm.AddBand(640, -9);
        vm.AddBand(670, 0);
        vm.AddBand(2800, 2.5);
        vm.AddBand(6000, 0);
        vm.AddBand(12000, 4);
        vm.PreampDb = -5;
        vm.TubeEnabled = true;
        vm.Drive = 35;
        vm.Character = 80;
        vm.Feedback = 20;
        vm.ShowDemoTone(1500);
        vm.TapeOn = true; vm.Wow = 20;
        vm.SoftenOn = true;
        vm.SpaceOn = true; vm.SpaceDecay = 8;
        // show an ordinary output device in the picture, whatever this machine happens to list first
        vm.SelectedOutput = vm.Outputs.FirstOrDefault(d => d.Name.StartsWith("Speakers", StringComparison.OrdinalIgnoreCase))
                            ?? vm.Outputs.FirstOrDefault(d => d.Name.Contains("S/PDIF", StringComparison.OrdinalIgnoreCase))
                            ?? vm.SelectedOutput;
    }

    static void SaveScreenshot(Window w, string path)
    {
        var root = (FrameworkElement)w.Content;
        var dpi = VisualTreeHelper.GetDpi(root);
        var rtb = new RenderTargetBitmap((int)(root.ActualWidth * dpi.DpiScaleX), (int)(root.ActualHeight * dpi.DpiScaleY),
            dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        rtb.Render(root);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(path);
        enc.Save(fs);
    }

    /// <summary>Double-click a slider to put it back to its default (held in Tag).</summary>
    void Slider_Reset(object sender, MouseButtonEventArgs e)
    {
        if (sender is Slider s && s.Tag is string tag && double.TryParse(tag, System.Globalization.CultureInfo.InvariantCulture, out double v))
        {
            s.Value = v;
            e.Handled = true;
        }
    }
}
