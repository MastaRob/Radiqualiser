using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace Radiqualiser
{
    public partial class MainWindow : Window
    {
        readonly MainViewModel _vm;
        readonly DispatcherTimer _timer;
        Forms.NotifyIcon? _tray;
        bool _exiting;

        public MainWindow(MainViewModel vm, bool withTray)
        {
            InitializeComponent();
            _vm = vm;
            DataContext = vm;
            Graph.Vm = vm;
            Harmonics.Vm = vm;

            _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
            _timer.Tick += (_, _) =>
            {
                bool visible = IsVisible && WindowState != WindowState.Minimized;
                _vm.Tick(visible);
                if (visible && (_vm.Spectrum != null || _hadSpectrum)) Graph.InvalidateVisual();   // also moves the tone marker
                _hadSpectrum = _vm.Spectrum != null;
            };
            _timer.Start();

            vm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is nameof(MainViewModel.Running) or nameof(MainViewModel.Bypass)) UpdateIndicators();
                if (e.PropertyName is nameof(MainViewModel.SweepOn)) Graph.InvalidateVisual();
            };
            UpdateIndicators();
            if (withTray) CreateTray();
        }

        bool _hadSpectrum;

        void UpdateIndicators()
        {
            StatusDot.Fill = (Brush)FindResource(!_vm.Running ? "Line" : _vm.Bypass ? "Danger" : "Good");
            if (_tray != null) _tray.Text = !_vm.Running ? "Radiqualiser (off)" : _vm.Bypass ? "Radiqualiser (bypassed)" : "Radiqualiser";
        }

        // ------------------------------------------------------------------ tray

        void CreateTray()
        {
            System.Drawing.Icon? icon = null;
            try
            {
                var res = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/radiqualiser.ico"));
                if (res != null) icon = new System.Drawing.Icon(res.Stream, Forms.SystemInformation.SmallIconSize);
            }
            catch
            {
                // fall through to the stock icon
            }
            var menu = new Forms.ContextMenuStrip();
            menu.Items.Add("Open Radiqualiser", null, (_, _) => ShowFromTray());
            var bypass = new Forms.ToolStripMenuItem("Bypass") { CheckOnClick = true };
            bypass.Click += (_, _) => _vm.Bypass = bypass.Checked;
            menu.Items.Add(bypass);
            var power = new Forms.ToolStripMenuItem("Power") { CheckOnClick = true };
            power.Click += (_, _) => _vm.Power = power.Checked;
            menu.Items.Add(power);
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add("Exit", null, (_, _) => ExitApp());
            menu.Opening += (_, _) => { bypass.Checked = _vm.Bypass; power.Checked = _vm.Power; };

            _tray = new Forms.NotifyIcon
            {
                Icon = icon ?? System.Drawing.SystemIcons.Application,
                Text = "Radiqualiser",
                Visible = true,
                ContextMenuStrip = menu,
            };
            _tray.MouseClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) ShowFromTray(); };
            UpdateIndicators();
        }

        public void ShowFromTray()
        {
            Show();
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Activate();
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            if (!_exiting && _tray != null)
            {
                // Closing the window keeps the audio running; Exit lives in the tray menu.
                e.Cancel = true;
                Hide();
                if (!_vm.TrayHintShown)
                {
                    _vm.TrayHintShown = true;
                    _tray.ShowBalloonTip(4000, "Radiqualiser is still running", "It lives in the system tray. Right-click the icon and choose Exit to quit.", Forms.ToolTipIcon.Info);
                }
                return;
            }
            base.OnClosing(e);
        }

        public void ExitApp()
        {
            if (_exiting) return;
            _exiting = true;
            _timer.Stop();
            _vm.Shutdown();
            if (_tray != null) { _tray.Visible = false; _tray.Dispose(); _tray = null; }
            Close();
            Application.Current.Shutdown();
        }

        // ------------------------------------------------------------------ handlers

        void AddBand_Click(object sender, RoutedEventArgs e)
        {
            // drop the new point in the widest gap between existing points
            var freqs = _vm.Bands.Select(b => Math.Log(b.Freq)).Append(Math.Log(20)).Append(Math.Log(20000)).OrderBy(x => x).ToArray();
            double best = Math.Log(1000), gap = 0;
            for (int i = 1; i < freqs.Length; i++)
            {
                if (freqs[i] - freqs[i - 1] > gap) { gap = freqs[i] - freqs[i - 1]; best = 0.5 * (freqs[i] + freqs[i - 1]); }
            }
            double f = _vm.Bands.Count == 0 ? 1000 : Math.Exp(best);
            _vm.AddBand(f, Math.Round(_vm.Curve().At(f), 2));
            Graph.Focus();
        }

        void ZeroBand_Click(object sender, RoutedEventArgs e)
        {
            _vm.Batch(() => { foreach (var b in _vm.Selection) b.GainDb = 0; });
            Graph.Focus();
        }

        void DeleteBand_Click(object sender, RoutedEventArgs e)
        {
            _vm.RemoveSelected();
        }

        void Field_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter || sender is not TextBox tb) return;
            tb.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
            tb.GetBindingExpression(TextBox.TextProperty)?.UpdateTarget();
            tb.SelectAll();
            e.Handled = true;
        }

        void Rescan_Click(object sender, RoutedEventArgs e) => _vm.RefreshDevices();

        void SaveNow_Click(object sender, RoutedEventArgs e)
        {
            if (!_vm.SaveCurrentPreset()) SavePreset_Click(sender, e);
        }

        void SavePreset_Click(object sender, RoutedEventArgs e)
        {
            string suggested = _vm.CanDeletePreset ? _vm.SelectedPreset : "";
            while (true)
            {
                var dlg = new NameDialog("Save preset", "Preset name", suggested) { Owner = this };
                if (dlg.ShowDialog() != true) return;
                string? err = _vm.SavePresetAs(dlg.Value);
                if (err == null) return;
                MessageBox.Show(this, err, "Radiqualiser", MessageBoxButton.OK, MessageBoxImage.Information);
                suggested = dlg.Value;
            }
        }

        void DeletePreset_Click(object sender, RoutedEventArgs e)
        {
            if (!_vm.CanDeletePreset) return;
            if (MessageBox.Show(this, $"Delete the preset \"{_vm.SelectedPreset}\"?", "Radiqualiser", MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK)
                _vm.DeleteSelectedPreset();
        }
    }

    /// <summary>A minimal single-field prompt.</summary>
    public sealed class NameDialog : Window
    {
        readonly TextBox _box;
        public string Value => _box.Text;

        public NameDialog(string title, string label, string initial)
        {
            Title = title;
            SizeToContent = SizeToContent.WidthAndHeight;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            Background = (Brush)Application.Current.FindResource("Bg");
            Foreground = (Brush)Application.Current.FindResource("Text");
            FontSize = 12.5;

            _box = new TextBox { Text = initial, Width = 280, Margin = new Thickness(0, 6, 0, 14) };
            var ok = new Button { Content = "Save", IsDefault = true, MinWidth = 80 };
            var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 80, Margin = new Thickness(8, 0, 0, 0) };
            ok.Click += (_, _) => DialogResult = true;
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            buttons.Children.Add(ok);
            buttons.Children.Add(cancel);
            var panel = new StackPanel { Margin = new Thickness(18) };
            panel.Children.Add(new TextBlock { Text = label });
            panel.Children.Add(_box);
            panel.Children.Add(buttons);
            Content = panel;
            Loaded += (_, _) => { _box.Focus(); _box.SelectAll(); };
        }
    }
}

