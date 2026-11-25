namespace Radiqualiser.Core.Dsp;

/// <summary>One point of the drawn EQ curve.</summary>
public sealed record BandParams(int Id, double Freq, double GainDb, bool Enabled = true);

/// <param name="DriveDb">How hard the signal is pushed into the valve curve (-30 … +24 dB; the bottom of the range is clean).</param>
/// <param name="Character">0 = push-pull (odd harmonics only) … 1 = single-ended triode (strong 2nd harmonic).</param>
/// <param name="Transformer">0…1: output-transformer bass saturation and treble roll-off.</param>
/// <param name="Bloom">0…1: bass lift from a high output impedance meeting a speaker's resonance.</param>
/// <param name="Mix">0…1 dry/wet blend of the valve curve. The app always uses 1; Drive alone sets the amount.</param>
/// <param name="Feedback">0…1: negative feedback around the valve curve (0 = none, 1 = 20 dB).</param>
public sealed record TubeParams(bool Enabled, double DriveDb, double Character, double Transformer, double Bloom, double Mix, double Feedback = 0)
{
    public static readonly TubeParams Off = new(false, -14, 0.6, 0.3, 0.3, 1.0);
}

/// <param name="Drive">0…1: how hard the tape is hit (0 = clean).</param>
/// <param name="Squash">0…1: how much sooner the treble saturates than the rest.</param>
/// <param name="Wow">0…1: wow and flutter (pitch unsteadiness).</param>
public sealed record TapeParams(bool Enabled, double Drive, double Squash, double Wow)
{
    public static readonly TapeParams Off = new(false, 0.35, 0.5, 0);
}

/// <summary>The remaining effects. Amounts are 0…1; a section that is switched off behaves as if its amounts were 0.</summary>
/// <param name="Transient">Transient softening.</param>
/// <param name="Deharsh">Dynamic de-harsh amount.</param>
/// <param name="DeharshHz">Centre of the region the de-harsh watches.</param>
/// <param name="BassAmount">Phantom-bass harmonics level.</param>
/// <param name="BassHz">Harmonics are made from everything below this.</param>
/// <param name="Glue">Glue compression amount.</param>
/// <param name="SpaceAmount">Reverb level.</param>
/// <param name="SpaceDecay">Reverb decay time in seconds.</param>
public sealed record FxParams(
    bool SoftenOn, double Transient, double Deharsh, double DeharshHz,
    bool BassOn, double BassAmount, double BassHz,
    bool GlueOn, double Glue,
    bool SpaceOn, double SpaceAmount, double SpaceDecay)
{
    public static readonly FxParams Off = new(false, 0.3, 0.4, 3500, false, 0.4, 90, false, 0.3, false, 0.3, 6);
}

/// <param name="Bands">The points the EQ curve passes through.</param>
/// <param name="EqRounding">0 = straight lines between the points … 1 = smooth curve through them.</param>
/// <param name="AutoLevel">Ignore the manual preamp/output and hold loudness and headroom automatically.</param>
public sealed record DspParams(
    bool Bypass,
    bool EqEnabled,
    double PreampDb,
    double OutputDb,
    IReadOnlyList<BandParams> Bands,
    TubeParams Tube,
    bool AutoLevel = false,
    TapeParams? Tape = null,
    FxParams? Fx = null,
    double EqRounding = 1)
{
    public static readonly DspParams Default = new(false, true, 0, 0, Array.Empty<BandParams>(), TubeParams.Off);
}
