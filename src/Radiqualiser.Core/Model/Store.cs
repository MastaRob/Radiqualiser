using System.Text.Json;
using System.Text.Json.Serialization;
using Radiqualiser.Core.Dsp;

namespace Radiqualiser.Core.Model;

/// <summary>One point of the EQ curve.</summary>
public sealed class BandData
{
    public int Id { get; set; }
    public double Freq { get; set; } = 1000;
    public double GainDb { get; set; }
    public bool Enabled { get; set; } = true;
}

/// <summary>Everything that shapes the sound; what a preset stores.</summary>
public sealed class Preset
{
    public string Name { get; set; } = "";
    public bool EqEnabled { get; set; } = true;
    public double PreampDb { get; set; }
    public double OutputDb { get; set; }
    public List<BandData> Bands { get; set; } = new();
    /// <summary>0 = straight lines between the points, 1 = smooth curve.</summary>
    public double EqRounding { get; set; } = 1.0;
    public bool TubeEnabled { get; set; }
    public double DriveDb { get; set; } = -14;
    public double Character { get; set; } = 0.6;
    public double Transformer { get; set; } = 0.3;
    /// <summary>No longer used (the bass-bloom control was removed); kept so older preset files still load.</summary>
    public double Bloom { get; set; }
    public double Mix { get; set; } = 1.0;
    public double Feedback { get; set; }

    public bool SoftenOn { get; set; }
    public double Transient { get; set; } = 0.3;
    public double Deharsh { get; set; } = 0.4;
    public double DeharshHz { get; set; } = 3500;

    public bool TapeOn { get; set; }
    public double TapeDrive { get; set; } = 0.35;
    public double TapeSquash { get; set; } = 0.5;
    public double Wow { get; set; }

    public bool BassOn { get; set; }
    public double BassAmount { get; set; } = 0.4;
    public double BassHz { get; set; } = 90;

    public bool GlueOn { get; set; }
    public double Glue { get; set; } = 0.3;

    public bool SpaceOn { get; set; }
    public double SpaceAmount { get; set; } = 0.3;
    public double SpaceDecay { get; set; } = 6;

    public Preset Clone() => JsonSerializer.Deserialize<Preset>(JsonSerializer.Serialize(this, Store.Json), Store.Json)!;
}

public sealed class AppSettings
{
    public Preset Current { get; set; } = new();
    public string? PresetName { get; set; } = "Flat";
    /// <summary>The settings have been changed since the named preset was loaded or saved.</summary>
    public bool PresetModified { get; set; }
    public bool Power { get; set; } = true;
    public string? SourceId { get; set; }
    public string? OutputId { get; set; }
    /// <summary>The playback device that was the Windows default before this app pointed it at the cable.</summary>
    public string? RestoreDefaultId { get; set; }
    public bool AutoSwitchDefault { get; set; } = true;
    public bool FollowWindowsVolume { get; set; } = true;
    public bool LowLatency { get; set; } = true;
    public int SafetyBufferMs { get; set; } = 20;
    public bool TrayHintShown { get; set; }
    /// <summary>The starter presets have been written out as ordinary preset files (done once).</summary>
    public bool PresetsSeeded { get; set; }
    public bool AutoLevel { get; set; } = true;
    public double SweepSpeed { get; set; } = 50;
}

public static class Store
{
    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string Dir { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Radiqualiser");
    static string SettingsPath => Path.Combine(Dir, "settings.json");
    static string PresetDir => Path.Combine(Dir, "presets");

    public static AppSettings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsPath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath), Json) ?? new AppSettings();
        }
        catch
        {
            // unreadable settings: start fresh rather than refuse to launch
        }
        return new AppSettings();
    }

    public static void SaveSettings(AppSettings s)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            string tmp = SettingsPath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(s, Json));
            File.Move(tmp, SettingsPath, true);
        }
        catch
        {
            // settings are a convenience; never take the audio down over a failed save
        }
    }

    /// <summary>The presets a fresh installation starts with.</summary>
    public static IReadOnlyList<Preset> Factory { get; } = new[]
    {
        new Preset { Name = "Flat" },
        new Preset { Name = "Gentle Triode", TubeEnabled = true, DriveDb = -16, Character = 0.9, Transformer = 0.2 },
        new Preset { Name = "Push-Pull Punch", TubeEnabled = true, DriveDb = -8, Character = 0.1, Transformer = 0.35 },
        new Preset { Name = "Vintage Console", TubeEnabled = true, DriveDb = -4, Character = 0.6, Transformer = 0.7 },
        new Preset { Name = "Cooked", TubeEnabled = true, DriveDb = 14, Character = 0.75, Transformer = 0.8 },
        new Preset { Name = "Soft Tape", TapeOn = true, TapeDrive = 0.4, TapeSquash = 0.65, SoftenOn = true, Transient = 0.25, Deharsh = 0.45 },
        new Preset { Name = "Worn Cassette", TapeOn = true, TapeDrive = 0.6, TapeSquash = 0.85, Wow = 0.45, GlueOn = true, Glue = 0.45 },
        new Preset { Name = "Cathedral", SpaceOn = true, SpaceAmount = 0.45, SpaceDecay = 8 },
        new Preset
        {
            Name = "Loudness Curve",
            PreampDb = -5,      // only used when Auto level is off
            Bands =
            {
                new BandData { Id = 1, Freq = 40, GainDb = 6 },
                new BandData { Id = 2, Freq = 100, GainDb = 4 },
                new BandData { Id = 3, Freq = 300, GainDb = 0 },
                new BandData { Id = 4, Freq = 1500, GainDb = 0 },
                new BandData { Id = 5, Freq = 3000, GainDb = -1.5 },
                new BandData { Id = 6, Freq = 6000, GainDb = 0 },
                new BandData { Id = 7, Freq = 12000, GainDb = 3 },
            },
        },
    };

    /// <summary>
    /// Writes the starter presets out as ordinary preset files, once. From then on they are no different from presets
    /// the user saved: they can be changed, overwritten or deleted, and a deleted one stays deleted.
    /// </summary>
    public static void SeedStarterPresets(AppSettings settings)
    {
        if (settings.PresetsSeeded) return;
        try
        {
            foreach (var p in Factory)
                if (!File.Exists(FileFor(p.Name))) SavePreset(p);
            settings.PresetsSeeded = true;
            SaveSettings(settings);
        }
        catch
        {
            // no write access: try again next launch
        }
    }

    static string FileFor(string name)
    {
        string safe = string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim();
        return Path.Combine(PresetDir, safe + ".json");
    }

    public static List<Preset> LoadUserPresets()
    {
        var list = new List<Preset>();
        try
        {
            if (!Directory.Exists(PresetDir)) return list;
            foreach (string f in Directory.GetFiles(PresetDir, "*.json"))
            {
                try
                {
                    var p = JsonSerializer.Deserialize<Preset>(File.ReadAllText(f), Json);
                    if (p != null && !string.IsNullOrWhiteSpace(p.Name)) list.Add(p);
                }
                catch
                {
                    // skip a corrupt preset file, keep the rest
                }
            }
        }
        catch
        {
            // no presets folder access: behave as if there are none
        }
        return list.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static void SavePreset(Preset p)
    {
        Directory.CreateDirectory(PresetDir);
        File.WriteAllText(FileFor(p.Name), JsonSerializer.Serialize(p, Json));
    }

    public static void DeletePreset(string name)
    {
        string f = FileFor(name);
        if (File.Exists(f)) File.Delete(f);
    }
}
