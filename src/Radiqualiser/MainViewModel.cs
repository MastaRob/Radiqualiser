using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows.Threading;
using Microsoft.Win32;
using NAudio.CoreAudioApi;
using NAudio.Dsp;
using Radiqualiser.Core.Audio;
using Radiqualiser.Core.Dsp;
using Radiqualiser.Core.Model;

namespace Radiqualiser;

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }

    protected void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>One point of the EQ curve.</summary>
public sealed class BandVm : Observable
{
    double _freq = 1000, _gain;
    bool _enabled = true;

    public int Id { get; init; }
    public event Action? Changed;

    public double Freq { get => _freq; set { if (Set(ref _freq, Math.Clamp(Math.Round(value, 1), 20, 20000))) Changed?.Invoke(); } }
    public double GainDb { get => _gain; set { if (Set(ref _gain, Math.Clamp(Math.Round(value, 2), -24, 24))) Changed?.Invoke(); } }
    /// <summary>A point that is switched off is ignored: the line is drawn as if it were not there.</summary>
    public bool Enabled { get => _enabled; set { if (Set(ref _enabled, value)) Changed?.Invoke(); } }

    public BandParams ToParams() => new(Id, Freq, GainDb, Enabled);
    public BandData ToData() => new() { Id = Id, Freq = Freq, GainDb = GainDb, Enabled = Enabled };
}

public sealed class MainViewModel : Observable
{
    public const string CustomPreset = "(custom)";

    readonly AudioEngine _engine = new();
    readonly AppSettings _settings;
    readonly bool _persist;
    readonly Dispatcher _ui = Dispatcher.CurrentDispatcher;
    readonly DispatcherTimer _saveTimer, _retryTimer;
    bool _loading;
    int _startGen, _nextBandId = 1;
    DeviceInfo? _cableRender;
    int _statusTick;

    public ObservableCollection<BandVm> Bands { get; } = new();
    public ObservableCollection<string> PresetNames { get; } = new();
    public ObservableCollection<DeviceInfo> Sources { get; } = new();
    public ObservableCollection<DeviceInfo> Outputs { get; } = new();

    /// <summary>Raised when anything the graph or the harmonic display draws has changed.</summary>
    public event Action? VisualChanged;

    public MainViewModel(bool persist = true)
    {
        _persist = persist;
        _settings = persist ? Store.LoadSettings() : new AppSettings { Power = false };
        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); SaveNow(); };
        _retryTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _retryTimer.Tick += async (_, _) => { if (_power && !_engine.IsRunning) await StartEngine(); else _retryTimer.Stop(); };
        _engine.Faulted += msg => _ui.BeginInvoke(() => OnEngineFailed(msg));

        _autoSwitch = _settings.AutoSwitchDefault;
        _followVolume = _settings.FollowWindowsVolume;
        _lowLatency = _settings.LowLatency;
        _safetyMs = _settings.SafetyBufferMs;
        _autoLevel = _settings.AutoLevel;
        _sweepSpeed = Math.Clamp(_settings.SweepSpeed, 0, 100);

        RefreshDevices();
        if (persist) Store.SeedStarterPresets(_settings);
        RefreshPresetNames();
        Apply(_settings.Current);
        _selectedPreset = _settings.PresetName != null && PresetNames.Contains(_settings.PresetName) ? _settings.PresetName : CustomPreset;
        _presetModified = _settings.PresetModified && _selectedPreset != CustomPreset;
        _baseline = _presetModified ? null : Snapshot();
        _power = _settings.Power;
    }

    // ------------------------------------------------------------------ sound parameters

    bool _eqEnabled = true, _bypass, _tubeEnabled;
    double _preamp, _output, _drive = 30, _character = 60, _transformer = 30, _feedback;
    bool _autoLevel = true;
    bool _softenOn, _tapeOn, _bassOn, _glueOn, _spaceOn;
    double _transient = 30, _deharsh = 40, _deharshHz = 3500, _tapeDrive = 35, _tapeSquash = 50, _wow,
           _bassAmount = 40, _bassHz = 90, _glue = 30, _spaceAmount = 30, _spaceDecay = 6;
    double _eqRounding = 100;
    BandVm? _selectedBand;

    public bool EqEnabled { get => _eqEnabled; set { if (Set(ref _eqEnabled, value)) SoundChanged(); } }
    public bool Bypass { get => _bypass; set { if (Set(ref _bypass, value)) SoundChanged(false); } }
    public double PreampDb { get => _preamp; set { if (Set(ref _preamp, Math.Round(value, 1))) { Raise(nameof(PreampText)); SoundChanged(); } } }
    public double OutputDb { get => _output; set { if (Set(ref _output, Math.Round(value, 1))) { Raise(nameof(OutputText)); SoundChanged(); } } }
    public bool TubeEnabled { get => _tubeEnabled; set { if (Set(ref _tubeEnabled, value)) SoundChanged(); } }
    /// <summary>Valve drive as 0–100% of the usable range (0 = clean).</summary>
    public double Drive { get => _drive; set { if (Set(ref _drive, Math.Clamp(Math.Round(value), 0, 100))) { Raise(nameof(DriveText)); SoundChanged(); } } }

    /// <summary>Hold loudness and headroom automatically instead of using the manual Preamp and Output sliders.</summary>
    public bool AutoLevel
    {
        get => _autoLevel;
        set { if (Set(ref _autoLevel, value)) { Raise(nameof(ManualLevels)); SoundChanged(false); } }
    }
    public bool ManualLevels => !_autoLevel;

    static double DriveToDb(double percent) => TubeMath.MinDriveDb + (TubeMath.MaxDriveDb - TubeMath.MinDriveDb) * percent / 100.0;
    static double DbToDrive(double db) => Math.Clamp((db - TubeMath.MinDriveDb) / (TubeMath.MaxDriveDb - TubeMath.MinDriveDb) * 100.0, 0, 100);
    public double Character { get => _character; set { if (Set(ref _character, Math.Round(value))) { Raise(nameof(CharacterText)); SoundChanged(); } } }
    public double Transformer { get => _transformer; set { if (Set(ref _transformer, Math.Round(value))) { Raise(nameof(TransformerText)); SoundChanged(); } } }

    bool SetFx<T>(ref T field, T value, string? text = null, [CallerMemberName] string? name = null)
    {
        if (!Set(ref field, value, name)) return false;
        if (text != null) Raise(text);
        SoundChanged();
        return true;
    }
    static double Pct(double v) => Math.Clamp(Math.Round(v), 0, 100);

    /// <summary>Negative feedback around the valve stage, 0–100 (= 0–20 dB).</summary>
    public double Feedback { get => _feedback; set => SetFx(ref _feedback, Pct(value), nameof(FeedbackText)); }
    public string FeedbackText => _feedback < 1 ? "none" : $"{_feedback * 0.2:0} dB";

    public bool SoftenOn { get => _softenOn; set => SetFx(ref _softenOn, value); }
    public double Transient { get => _transient; set => SetFx(ref _transient, Pct(value), nameof(TransientText)); }
    public double Deharsh { get => _deharsh; set => SetFx(ref _deharsh, Pct(value), nameof(DeharshText)); }
    public double DeharshHz { get => _deharshHz; set => SetFx(ref _deharshHz, Math.Clamp(Math.Round(value / 100) * 100, 1500, 8000), nameof(DeharshHzText)); }
    public string TransientText => _transient < 1 ? "off" : $"{_transient:0}%";
    public string DeharshText => _deharsh < 1 ? "off" : $"{_deharsh:0}%";
    public string DeharshHzText => $"{_deharshHz / 1000:0.0} kHz";

    public bool TapeOn { get => _tapeOn; set => SetFx(ref _tapeOn, value); }
    public double TapeDrive { get => _tapeDrive; set => SetFx(ref _tapeDrive, Pct(value), nameof(TapeDriveText)); }
    public double TapeSquash { get => _tapeSquash; set => SetFx(ref _tapeSquash, Pct(value), nameof(TapeSquashText)); }
    public double Wow { get => _wow; set => SetFx(ref _wow, Pct(value), nameof(WowText)); }
    public string TapeDriveText => _tapeDrive < 1 ? "off" : $"{_tapeDrive:0}%";
    public string TapeSquashText => $"{_tapeSquash:0}%";
    public string WowText => _wow < 1 ? "off" : $"{_wow:0}%";

    public bool BassOn { get => _bassOn; set => SetFx(ref _bassOn, value); }
    public double BassAmount { get => _bassAmount; set => SetFx(ref _bassAmount, Pct(value), nameof(BassAmountText)); }
    public double BassHz { get => _bassHz; set => SetFx(ref _bassHz, Math.Clamp(Math.Round(value / 5) * 5, 40, 200), nameof(BassHzText)); }
    public string BassAmountText => _bassAmount < 1 ? "off" : $"{_bassAmount:0}%";
    public string BassHzText => $"{_bassHz:0} Hz";

    public bool GlueOn { get => _glueOn; set => SetFx(ref _glueOn, value); }
    public double Glue { get => _glue; set => SetFx(ref _glue, Pct(value), nameof(GlueText)); }
    public string GlueText => _glue < 1 ? "off" : $"{_glue:0}%";

    public bool SpaceOn { get => _spaceOn; set => SetFx(ref _spaceOn, value); }
    public double SpaceAmount { get => _spaceAmount; set => SetFx(ref _spaceAmount, Pct(value), nameof(SpaceAmountText)); }
    public double SpaceDecay { get => _spaceDecay; set => SetFx(ref _spaceDecay, Math.Clamp(Math.Round(value * 2) / 2, 1, 12), nameof(SpaceDecayText)); }
    public string SpaceAmountText => _spaceAmount < 1 ? "off" : $"{_spaceAmount:0}%";
    public string SpaceDecayText => $"{_spaceDecay:0.0} s";

    public TapeParams GetTapeParams() => new(_tapeOn, _tapeDrive / 100, _tapeSquash / 100, _wow / 100);
    public FxParams GetFxParams() => new(_softenOn, _transient / 100, _deharsh / 100, _deharshHz,
        _bassOn, _bassAmount / 100, _bassHz, _glueOn, _glue / 100, _spaceOn, _spaceAmount / 100, _spaceDecay);

    public string PreampText => $"{_preamp:+0.0;-0.0;0.0} dB";
    public string OutputText => $"{_output:+0.0;-0.0;0.0} dB";
    public string DriveText => _drive < 1 ? "off" : $"{_drive:0}% · " + (_drive < 22 ? "subtle" : _drive < 42 ? "warm" : _drive < 60 ? "thick" : _drive < 80 ? "overdriven" : "fuzz");
    public string CharacterText => _character < 8 ? "push-pull" : _character > 92 ? "single-ended" : $"{_character:0}%";
    public string TransformerText => $"{_transformer:0}%";

    readonly HashSet<BandVm> _selection = new();
    int _batch;
    bool _batchDirty;

    /// <summary>
    /// The point shown in the Freq/Gain boxes. Several points can be selected at once (<see cref="Selection"/>); this is
    /// the one most recently clicked. Setting it selects that point alone.
    /// </summary>
    public BandVm? SelectedBand
    {
        get => _selectedBand;
        set
        {
            _selection.Clear();
            if (value != null) _selection.Add(value);
            SetPrimary(value);
        }
    }

    public IReadOnlyCollection<BandVm> Selection => _selection;
    public bool IsSelected(BandVm b) => _selection.Contains(b);

    void SetPrimary(BandVm? b)
    {
        if (Set(ref _selectedBand, b, nameof(SelectedBand))) Raise(nameof(HasSelection));
        VisualChanged?.Invoke();
    }

    /// <summary>Makes an already-selected point the one shown in the boxes, keeping the rest of the selection.</summary>
    public void MakePrimary(BandVm b)
    {
        _selection.Add(b);
        SetPrimary(b);
    }

    /// <summary>Ctrl-click: add this one point to the selection, or take it out again.</summary>
    public void ToggleSelect(BandVm b)
    {
        if (_selection.Remove(b)) SetPrimary(ReferenceEquals(_selectedBand, b) ? _selection.FirstOrDefault() : _selectedBand);
        else { _selection.Add(b); SetPrimary(b); }
    }

    /// <summary>Shift-click: select this point, the last point clicked, and every point between them.</summary>
    public void SelectRange(BandVm to)
    {
        var from = _selectedBand ?? to;
        double lo = Math.Min(from.Freq, to.Freq), hi = Math.Max(from.Freq, to.Freq);
        _selection.Clear();
        foreach (var b in Bands) if (b.Freq >= lo && b.Freq <= hi) _selection.Add(b);
        SetPrimary(from);       // the first point stays the anchor, so another shift-click re-stretches from it
    }

    public void SelectAll()
    {
        foreach (var b in Bands) _selection.Add(b);
        SetPrimary(_selectedBand ?? Bands.FirstOrDefault());
    }

    public void RemoveSelected()
    {
        if (_selection.Count == 0) return;
        foreach (var b in _selection) Bands.Remove(b);
        _selection.Clear();
        SetPrimary(null);
        SoundChanged();
    }

    /// <summary>Makes several changes count as one: the audio and the display are updated once, at the end.</summary>
    public void Batch(Action changes)
    {
        _batch++;
        try { changes(); }
        finally
        {
            if (--_batch == 0 && _batchDirty) { _batchDirty = false; SoundChanged(); }
        }
    }
    public bool HasSelection => _selectedBand != null;

    public TubeParams GetTubeParams() => new(_tubeEnabled, DriveToDb(_drive), _character / 100, _transformer / 100, 0, 1.0, _feedback / 100);

    public DspParams BuildParams() =>
        new(_bypass, _eqEnabled, _preamp, _output, Bands.Select(b => b.ToParams()).ToArray(), GetTubeParams(), _autoLevel, GetTapeParams(), GetFxParams(), _eqRounding / 100);

    public double SampleRate => _engine.Chain?.SampleRate ?? 48000;

    void SoundChanged(bool markCustom = true)
    {
        if (_loading) return;
        if (_batch > 0) { _batchDirty = true; return; }
        _engine.SetParams(BuildParams());
        if (markCustom) UpdateModified();
        VisualChanged?.Invoke();
        QueueSave();
    }

    public BandVm AddBand(double freq, double gainDb)
    {
        var b = new BandVm { Id = _nextBandId++, Freq = freq, GainDb = gainDb };
        Hook(b);
        Bands.Add(b);
        SelectedBand = b;
        SoundChanged();
        return b;
    }

    void Hook(BandVm b) => b.Changed += () => SoundChanged();

    /// <summary>How rounded the line is between points: 0 = straight segments … 100 = smooth curve.</summary>
    public double EqRounding { get => _eqRounding; set => SetFx(ref _eqRounding, Pct(value), nameof(EqRoundingText)); }
    public string EqRoundingText => _eqRounding < 1 ? "straight" : $"{_eqRounding:0}%";

    /// <summary>The line as drawn (and as the audio is filtered): through the enabled points, flat beyond the outermost.</summary>
    public EqCurve Curve() => new(Bands.Where(b => b.Enabled).Select(b => (b.Freq, b.GainDb)), _eqRounding / 100);

    public void RemoveBand(BandVm b)
    {
        if (!Bands.Remove(b)) return;
        _selection.Remove(b);
        if (ReferenceEquals(_selectedBand, b)) SetPrimary(_selection.FirstOrDefault());
        SoundChanged();
    }

    public void ClearBands()
    {
        if (Bands.Count == 0) return;
        Bands.Clear();
        SelectedBand = null;
        SoundChanged();
    }

    // ------------------------------------------------------------------ presets

    string _selectedPreset = CustomPreset;
    string? _baseline;              // the settings as they were when the preset was loaded or saved
    bool _presetModified, _refreshingPresets;

    /// <summary>
    /// The preset the current settings came from. It stays selected while you adjust things (see
    /// <see cref="PresetModified"/>); "(custom)" means settings that have never been saved under a name.
    /// </summary>
    public string SelectedPreset
    {
        get => _selectedPreset;
        set
        {
            if (_refreshingPresets || value == null || !Set(ref _selectedPreset, value)) return;
            RaisePresetState();
            if (value == CustomPreset) { SetModified(false); return; }
            var p = Store.LoadUserPresets().FirstOrDefault(x => x.Name == value);
            if (p == null) return;
            Apply(p);
            _baseline = Snapshot();
            SetModified(false);
            _engine.SetParams(BuildParams());
            VisualChanged?.Invoke();
            QueueSave();
        }
    }

    /// <summary>True when a saved preset is selected (any of them can be overwritten or deleted).</summary>
    public bool CanDeletePreset => _selectedPreset != CustomPreset;

    /// <summary>The current settings differ from the selected preset as saved.</summary>
    public bool PresetModified => _presetModified;
    public string PresetStatus => _presetModified ? "edited" : "";

    void RaisePresetState()
    {
        Raise(nameof(CanDeletePreset));
        Raise(nameof(PresetModified));
        Raise(nameof(PresetStatus));
    }

    void SetModified(bool modified)
    {
        if (_presetModified == modified) return;
        _presetModified = modified;
        RaisePresetState();
        QueueSave();
    }

    /// <summary>
    /// Compares what is set now with what the preset held, rather than assuming any touch is a change: putting a
    /// control back where it was clears the "edited" mark again.
    /// </summary>
    void UpdateModified()
    {
        if (_selectedPreset == CustomPreset) { SetModified(false); return; }
        SetModified(_baseline == null || Snapshot() != _baseline);
    }

    string Snapshot()
    {
        var p = Capture("");
        p.Bands = p.Bands.OrderBy(b => b.Freq).ToList();
        foreach (var b in p.Bands) b.Id = 0;            // point identity is bookkeeping, not sound
        return JsonSerializer.Serialize(p, Store.Json);
    }

    void RefreshPresetNames()
    {
        // Bring the list up to date by adding and removing individual names. Emptying and refilling it would leave
        // the drop-down with no selection for a moment, and it then restores a stale one (the reason a freshly saved
        // preset could still show as "(custom)").
        _refreshingPresets = true;
        try
        {
            var want = new List<string> { CustomPreset };
            want.AddRange(Store.LoadUserPresets().Select(p => p.Name));
            for (int i = PresetNames.Count - 1; i >= 0; i--)
                if (!want.Contains(PresetNames[i])) PresetNames.RemoveAt(i);
            for (int i = 0; i < want.Count; i++)
                if (i >= PresetNames.Count || PresetNames[i] != want[i]) PresetNames.Insert(i, want[i]);
        }
        finally
        {
            _refreshingPresets = false;
        }
        // and once the drop-down has finished reacting, tell it again what is selected
        _ui.BeginInvoke(DispatcherPriority.Background, () => Raise(nameof(SelectedPreset)));
    }

    public Preset Capture(string name) => new()
    {
        Name = name, EqEnabled = _eqEnabled, PreampDb = _preamp, OutputDb = _output,
        Bands = Bands.Select(b => b.ToData()).ToList(), EqRounding = _eqRounding / 100,
        TubeEnabled = _tubeEnabled, DriveDb = DriveToDb(_drive), Character = _character / 100, Transformer = _transformer / 100,
        Bloom = 0, Mix = 1, Feedback = _feedback / 100,
        SoftenOn = _softenOn, Transient = _transient / 100, Deharsh = _deharsh / 100, DeharshHz = _deharshHz,
        TapeOn = _tapeOn, TapeDrive = _tapeDrive / 100, TapeSquash = _tapeSquash / 100, Wow = _wow / 100,
        BassOn = _bassOn, BassAmount = _bassAmount / 100, BassHz = _bassHz,
        GlueOn = _glueOn, Glue = _glue / 100,
        SpaceOn = _spaceOn, SpaceAmount = _spaceAmount / 100, SpaceDecay = _spaceDecay,
    };

    void Apply(Preset p)
    {
        _loading = true;
        try
        {
            Bands.Clear();
            SelectedBand = null;
            foreach (var d in p.Bands)
            {
                var b = new BandVm { Id = _nextBandId++, Freq = d.Freq, GainDb = d.GainDb, Enabled = d.Enabled };
                Hook(b);
                Bands.Add(b);
            }
            EqEnabled = p.EqEnabled; PreampDb = p.PreampDb; OutputDb = p.OutputDb; EqRounding = p.EqRounding * 100;
            TubeEnabled = p.TubeEnabled; Drive = DbToDrive(p.DriveDb); Character = p.Character * 100;
            Transformer = p.Transformer * 100; Feedback = p.Feedback * 100;
            SoftenOn = p.SoftenOn; Transient = p.Transient * 100; Deharsh = p.Deharsh * 100; DeharshHz = p.DeharshHz;
            TapeOn = p.TapeOn; TapeDrive = p.TapeDrive * 100; TapeSquash = p.TapeSquash * 100; Wow = p.Wow * 100;
            BassOn = p.BassOn; BassAmount = p.BassAmount * 100; BassHz = p.BassHz;
            GlueOn = p.GlueOn; Glue = p.Glue * 100;
            SpaceOn = p.SpaceOn; SpaceAmount = p.SpaceAmount * 100; SpaceDecay = p.SpaceDecay;
        }
        finally
        {
            _loading = false;
        }
    }

    /// <summary>Returns an error message, or null on success.</summary>
    public string? SavePresetAs(string name)
    {
        name = name.Trim();
        if (name.Length == 0) return "Give the preset a name.";
        if (name == CustomPreset) return "That name is reserved; pick another.";
        try { Store.SavePreset(Capture(name)); }
        catch (Exception ex) { return "Could not save: " + ex.Message; }
        RefreshPresetNames();
        _selectedPreset = name;
        _baseline = Snapshot();
        _presetModified = false;
        Raise(nameof(SelectedPreset));
        RaisePresetState();
        QueueSave();
        return null;
    }

    /// <summary>Overwrites the selected preset with the current settings. False if there is nothing of yours selected to overwrite.</summary>
    public bool SaveCurrentPreset()
    {
        if (!CanDeletePreset) return false;
        try { Store.SavePreset(Capture(_selectedPreset)); }
        catch { return false; }
        _baseline = Snapshot();
        SetModified(false);
        return true;
    }

    public void DeleteSelectedPreset()
    {
        if (!CanDeletePreset) return;
        string name = _selectedPreset;
        try { Store.DeletePreset(name); } catch { return; }
        _selectedPreset = CustomPreset;
        _presetModified = false;
        RefreshPresetNames();
        Raise(nameof(SelectedPreset));
        RaisePresetState();
        QueueSave();
    }

    // ------------------------------------------------------------------ devices and options

    DeviceInfo? _source, _outputDev;
    bool _autoSwitch, _followVolume, _lowLatency, _power;
    int _safetyMs;

    public DeviceInfo? SelectedSource { get => _source; set { if (Set(ref _source, value) && !_loading) { QueueSave(); Restart(); } } }
    public DeviceInfo? SelectedOutput { get => _outputDev; set { if (Set(ref _outputDev, value) && !_loading) { QueueSave(); Restart(); } } }
    public bool AutoSwitch
    {
        get => _autoSwitch;
        set
        {
            if (!Set(ref _autoSwitch, value)) return;
            QueueSave();
            if (value) Restart(); else RestoreDefault(force: true);
        }
    }
    public bool FollowVolume { get => _followVolume; set { if (Set(ref _followVolume, value)) { QueueSave(); _engine.SetOptions(Options()); } } }
    public bool LowLatency { get => _lowLatency; set { if (Set(ref _lowLatency, value)) { QueueSave(); Restart(); } } }
    public int SafetyMs { get => _safetyMs; set { if (Set(ref _safetyMs, value)) { Raise(nameof(SafetyText)); QueueSave(); _engine.SetOptions(Options()); } } }
    public string SafetyText => $"{_safetyMs} ms";

    public bool Power
    {
        get => _power;
        set { if (Set(ref _power, value)) { QueueSave(); _ = ApplyPower(); } }
    }

    EngineOptions Options() => new(_safetyMs, _lowLatency, _followVolume);

    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public bool StartWithWindows
    {
        get
        {
            try { using var k = Registry.CurrentUser.OpenSubKey(RunKey); return k?.GetValue("Radiqualiser") != null; }
            catch { return false; }
        }
        set
        {
            try
            {
                using var k = Registry.CurrentUser.CreateSubKey(RunKey);
                if (value) k.SetValue("Radiqualiser", $"\"{Environment.ProcessPath}\" --minimized");
                else k.DeleteValue("Radiqualiser", false);
            }
            catch
            {
                // registry not writable: the checkbox simply stays as it was
            }
            Raise();
        }
    }

    public void RefreshDevices()
    {
        bool was = _loading;
        _loading = true;
        try
        {
            var renders = Devices.List(DataFlow.Render);
            var captures = Devices.List(DataFlow.Capture);
            _cableRender = Devices.FindCableRender(renders);
            string? srcId = _source?.Id ?? _settings.SourceId;
            string? outId = _outputDev?.Id ?? _settings.OutputId;

            Sources.Clear();
            foreach (var d in captures) Sources.Add(d);
            Outputs.Clear();
            foreach (var d in renders.Where(d => !Devices.IsCable(d))) Outputs.Add(d);

            SelectedSource = Sources.FirstOrDefault(d => d.Id == srcId) ?? Devices.FindCableCapture(Sources);
            string? def = Devices.DefaultRenderId();
            SelectedOutput = Outputs.FirstOrDefault(d => d.Id == outId)
                             ?? Outputs.FirstOrDefault(d => d.Id == def)
                             ?? Outputs.FirstOrDefault(d => d.Id == _settings.RestoreDefaultId)
                             ?? Outputs.FirstOrDefault();
        }
        catch (Exception ex)
        {
            Status = "Could not list audio devices: " + ex.Message;
        }
        finally
        {
            _loading = was;
        }
    }

    // ------------------------------------------------------------------ test tone

    bool _sweepOn, _sweepHold;
    double _sweepSpeed = 50, _sweepHz, _sweepManualHz;

    /// <summary>Plays a sine sweep (20 Hz → 20 kHz, repeating) through the whole chain in place of the normal audio.</summary>
    public bool SweepOn
    {
        get => _sweepOn;
        set
        {
            if (value && !_engine.IsRunning) { Raise(); return; }       // needs Power on; leave the button up
            if (Set(ref _sweepOn, value)) { if (!value) SweepHold = false; PushSweep(); }
        }
    }
    /// <summary>Stay on the current frequency.</summary>
    public bool SweepHold
    {
        get => _sweepHold;
        set
        {
            if (!Set(ref _sweepHold, value)) return;
            if (!value) _sweepManualHz = 0;         // released: carry on sweeping from wherever the tone is
            PushSweep();
        }
    }

    /// <summary>Puts the tone on a chosen frequency and holds it there (the handle on the graph is being dragged).</summary>
    public void ScrubSweep(double hz)
    {
        if (!_sweepOn) return;
        _sweepManualHz = Math.Clamp(hz, 20, 20000);
        if (!_sweepHold) { _sweepHold = true; Raise(nameof(SweepHold)); }
        PushSweep();
    }
    /// <summary>0 = slowest (2 minutes per pass) … 100 = fastest (4 seconds).</summary>
    public double SweepSpeed { get => _sweepSpeed; set { if (Set(ref _sweepSpeed, Math.Round(value))) { Raise(nameof(SweepText)); PushSweep(); QueueSave(); } } }
    double SweepSeconds => 120 * Math.Pow(4.0 / 120.0, _sweepSpeed / 100.0);
    public string SweepText => $"{SweepSeconds:0} s";
    /// <summary>Frequency the tone is on right now (0 when silent); drawn as a marker on the graph.</summary>
    public double SweepHz => _sweepHz;

    double _demoToneHz;
    /// <summary>Shows the tone marker without an engine (demo screenshots only).</summary>
    public void ShowDemoTone(double hz) => _demoToneHz = _sweepHz = hz;

    void PushSweep() => _engine.SetSweep(new SweepConfig(_sweepOn, SweepSeconds, -20, _sweepHold, _sweepHold ? _sweepManualHz : 0));

    void SweepOffQuietly()
    {
        if (!_sweepOn && !_sweepHold) return;
        _sweepOn = _sweepHold = false;
        _sweepManualHz = 0;
        Raise(nameof(SweepOn)); Raise(nameof(SweepHold));
        PushSweep();
    }

    // ------------------------------------------------------------------ engine lifecycle

    string _status = "Off";
    bool _running;

    public string Status { get => _status; private set => Set(ref _status, value); }
    public bool Running { get => _running; private set => Set(ref _running, value); }

    /// <summary>Call once the window exists. Starts the engine if it was on last time.</summary>
    public async Task Initialise()
    {
        if (_power) await StartEngine();
        else
        {
            // If a previous run ended without handing the default device back, do it now so sound is not stranded.
            RestoreDefault();
            Status = "Off: audio passes straight to your normal output, unprocessed.";
        }
    }

    async Task ApplyPower()
    {
        _retryTimer.Stop();
        if (_power) await StartEngine();
        else
        {
            StopEngine();
            Status = "Off: audio passes straight to your normal output, unprocessed.";
        }
    }

    async void Restart()
    {
        if (_loading || !_power) return;
        StopEngine();
        await StartEngine();
    }

    async Task StartEngine()
    {
        int gen = ++_startGen;
        if (_source == null || _outputDev == null) RefreshDevices();
        var src = _source; var outp = _outputDev; var cable = _cableRender;
        if (src == null)
        {
            Status = "VB-CABLE was not found. Install it (vb-audio.com/Cable), then switch this back on.";
            _retryTimer.Start();
            return;
        }
        if (outp == null)
        {
            Status = "No output device found.";
            _retryTimer.Start();
            return;
        }

        Status = "Starting…";
        var opt = Options();
        try
        {
            await Task.Run(() => _engine.Start(src.Id, outp.Id, cable?.Id, opt));
        }
        catch (Exception ex)
        {
            if (gen == _startGen) OnEngineFailed(ex.Message);
            return;
        }
        if (gen != _startGen) return;
        _engine.SetParams(BuildParams());

        // Only once the engine is really running do we point Windows at the cable; if anything above failed, sound
        // keeps going to the normal device.
        if (_autoSwitch && cable != null)
        {
            string? def = Devices.DefaultRenderId();
            if (def != null && Outputs.Any(d => d.Id == def))
            {
                _settings.RestoreDefaultId = def;
                SaveNow();
            }
            if (def != cable.Id) DefaultDeviceSwitcher.SetDefault(cable.Id);
        }
        _retryTimer.Stop();
        Running = true;
        VisualChanged?.Invoke();
    }

    void OnEngineFailed(string message)
    {
        SweepOffQuietly();
        _engine.Stop();
        Running = false;
        RestoreDefault();
        Status = _power ? $"Stopped ({message}). Sound is back on the normal output; retrying…" : "Off";
        if (_power) _retryTimer.Start();
    }

    void StopEngine()
    {
        SweepOffQuietly();
        _startGen++;
        _retryTimer.Stop();
        RestoreDefault();
        _engine.Stop();
        Running = false;
    }

    /// <summary>Hands the Windows default playback device back to the real output if it is currently the cable.</summary>
    void RestoreDefault(bool force = false)
    {
        if ((!_autoSwitch && !force) || !_persist) return;
        try
        {
            var cable = _cableRender;
            if (cable == null || Devices.DefaultRenderId() != cable.Id) return;
            string? target = _settings.RestoreDefaultId ?? _outputDev?.Id;
            if (target != null) DefaultDeviceSwitcher.SetDefault(target);
        }
        catch
        {
            // best effort
        }
    }

    public void Shutdown()
    {
        _saveTimer.Stop();
        _retryTimer.Stop();
        _startGen++;
        RestoreDefault();
        _engine.Stop();
        SaveNow();
    }

    /// <summary>Last-ditch cleanup for crashes: make sure Windows is not left playing into a cable nobody is listening to.</summary>
    public void EmergencyRestore() => RestoreDefault();

    void QueueSave()
    {
        if (_loading || !_persist) return;
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    void SaveNow()
    {
        if (!_persist) return;
        _settings.Current = Capture("");
        _settings.PresetName = _selectedPreset;
        _settings.PresetModified = _presetModified;
        _settings.Power = _power;
        _settings.SourceId = _source?.Id;
        _settings.OutputId = _outputDev?.Id;
        _settings.AutoSwitchDefault = _autoSwitch;
        _settings.FollowWindowsVolume = _followVolume;
        _settings.LowLatency = _lowLatency;
        _settings.SafetyBufferMs = _safetyMs;
        _settings.AutoLevel = _autoLevel;
        _settings.SweepSpeed = _sweepSpeed;
        Store.SaveSettings(_settings);
    }

    public bool TrayHintShown
    {
        get => _settings.TrayHintShown;
        set { _settings.TrayHintShown = value; QueueSave(); }
    }

    // ------------------------------------------------------------------ meters and analyser (UI timer)

    double _inDb = -90, _outDb = -90, _grDb;
    public double InDb { get => _inDb; private set => Set(ref _inDb, value); }
    public double OutDb { get => _outDb; private set => Set(ref _outDb, value); }
    public double GrDb { get => _grDb; private set => Set(ref _grDb, value); }
    double _autoDb, _deharshDb, _glueDb;
    /// <summary>How hard the de-harsh is working right now (dB of cut).</summary>
    public double DeharshDb { get => _deharshDb; private set => Set(ref _deharshDb, value); }
    /// <summary>How hard the glue compressor is working right now (dB of gain reduction).</summary>
    public double GlueDb { get => _glueDb; private set => Set(ref _glueDb, value); }
    public string LevelText
    {
        get
        {
            string lim = _grDb > 0.05 ? $"Limiter −{_grDb:0.0} dB" : "Limiter idle";
            if (!_autoLevel || _bypass || !_running) return lim;
            return (Math.Abs(_autoDb) < 0.05 ? "Auto level 0.0 dB" : $"Auto level {(_autoDb > 0 ? "+" : "−")}{Math.Abs(_autoDb):0.0} dB") + "  ·  " + lim;
        }
    }

    public const int SpectrumColumns = 160;
    const int FftBits = 13, FftSize = 1 << FftBits;
    readonly float[] _fftIn = new float[FftSize];
    readonly Complex[] _fft = new Complex[FftSize];
    readonly float[] _window = Enumerable.Range(0, FftSize).Select(i => (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI * i / FftSize))).ToArray();

    /// <summary>Smoothed output spectrum in dB per display column (log-spaced 20 Hz – 20 kHz), or null when idle.</summary>
    public float[]? Spectrum { get; private set; }

    public void Tick(bool visible)
    {
        var chain = _engine.Chain;
        bool running = _engine.IsRunning && chain != null;

        if (!running)
        {
            InDb = Math.Max(-90, _inDb - 3); OutDb = Math.Max(-90, _outDb - 3); GrDb = Math.Max(0, _grDb - 0.5);
            Raise(nameof(LevelText));
            DeharshDb = 0; GlueDb = 0;
            _sweepHz = _demoToneHz;
            Spectrum = null;
            return;
        }

        double inPk = 20 * Math.Log10(Math.Max(chain!.TakeInputPeak(), 1e-5));
        double outPk = 20 * Math.Log10(Math.Max(chain.TakeOutputPeak(), 1e-5));
        double gr = -20 * Math.Log10(Math.Max(chain.TakeLimiterGain(), 1e-3));
        InDb = Math.Max(inPk, _inDb - 1.5);
        OutDb = Math.Max(outPk, _outDb - 1.5);
        GrDb = Math.Max(gr, _grDb - 0.25);
        _autoDb = chain.AutoGainDb;
        DeharshDb = Math.Max(chain.TakeDeharshDb(), _deharshDb - 0.4);
        GlueDb = Math.Max(chain.TakeGlueDb(), _glueDb - 0.3);
        _sweepHz = chain.SweepHz;
        Raise(nameof(LevelText));

        if (++_statusTick % 8 == 0)
        {
            var s = _engine.Stats();
            string rate = s.CaptureRate == s.OutputRate ? $"{s.OutputRate / 1000.0:0.#} kHz" : $"{s.CaptureRate / 1000.0:0.#}→{s.OutputRate / 1000.0:0.#} kHz";
            string vol = s.Muted ? " · muted" : $" · volume {s.VolumePercent:0}%";
            string drops = s.Underruns + s.Overflows > 0 ? $" · dropouts {s.Underruns + s.Overflows}" : "";
            Status = $"Running · {rate} · delay ≈ {s.TotalLatencyMs:0} ms{vol}{drops}" + (_engine.Notes != null ? "  —  " + _engine.Notes : "");
        }

        if (visible) UpdateSpectrum(chain);
    }

    void UpdateSpectrum(DspChain chain)
    {
        chain.CopyRecent(_fftIn);
        for (int i = 0; i < FftSize; i++) { _fft[i].X = _fftIn[i] * _window[i]; _fft[i].Y = 0; }
        FastFourierTransform.FFT(true, FftBits, _fft);

        var spec = Spectrum ??= Enumerable.Repeat(-120f, SpectrumColumns).ToArray();
        double fs = chain.SampleRate, binHz = fs / FftSize;
        double ratio = Math.Pow(1000, 1.0 / SpectrumColumns);
        double f0 = 20;
        for (int c = 0; c < SpectrumColumns; c++)
        {
            double f1 = f0 * ratio;
            double b0 = f0 / binHz, b1 = f1 / binHz;
            double power;
            if (b1 - b0 < 1)
            {
                // fewer than one FFT bin per column (bass): interpolate between neighbours
                double bc = 0.5 * (b0 + b1);
                int i = Math.Clamp((int)bc, 1, FftSize / 2 - 2);
                double t = bc - i;
                power = (1 - t) * Pow(i) + t * Pow(i + 1);
            }
            else
            {
                power = 0;
                int end = Math.Min((int)b1, FftSize / 2 - 1);
                for (int i = Math.Max(1, (int)b0); i <= end; i++) power = Math.Max(power, Pow(i));
            }
            double centre = Math.Sqrt(f0 * f1);
            // +3 dB/octave tilt so music (which falls off towards the treble) reads roughly level
            float db = (float)(10 * Math.Log10(power * 16 + 1e-14) + 3.0 * Math.Log2(centre / 1000));
            spec[c] = db > spec[c] ? spec[c] + (db - spec[c]) * 0.6f : Math.Max(db, spec[c] - 1.2f);
            f0 = f1;
        }
        return;

        double Pow(int i) => _fft[i].X * _fft[i].X + _fft[i].Y * _fft[i].Y;
    }
}
