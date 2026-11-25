using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;

namespace Radiqualiser.Core.Audio;

public sealed record DeviceInfo(string Id, string Name, int SampleRate, int Channels, int Bits)
{
    public override string ToString() => Name;
}

public static class Devices
{
    public static List<DeviceInfo> List(DataFlow flow)
    {
        var list = new List<DeviceInfo>();
        using var en = new MMDeviceEnumerator();
        using var col = en.EnumerateAudioEndPoints(flow, DeviceState.Active);
        foreach (var d in col)
        {
            try
            {
                using var client = d.CreateAudioClient();
                var f = client.MixFormat;
                list.Add(new DeviceInfo(d.ID, d.FriendlyName, f.SampleRate, f.Channels, f.BitsPerSample));
            }
            catch
            {
                // a device that cannot report a format is not usable; skip it
            }
            finally
            {
                d.Dispose();
            }
        }
        return list;
    }

    public static string? DefaultRenderId()
    {
        try
        {
            using var en = new MMDeviceEnumerator();
            if (!en.HasDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)) return null;
            using var d = en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            return d.ID;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>The playback side of VB-CABLE: the device Windows should play into.</summary>
    public static DeviceInfo? FindCableRender(IEnumerable<DeviceInfo> renders) =>
        renders.FirstOrDefault(d => d.Name.StartsWith("CABLE Input", StringComparison.OrdinalIgnoreCase))
        ?? renders.FirstOrDefault(d => d.Name.Contains("VB-Audio Virtual Cable", StringComparison.OrdinalIgnoreCase)
                                       && !d.Name.Contains("16ch", StringComparison.OrdinalIgnoreCase));

    /// <summary>The recording side of VB-CABLE: where this app picks the audio up.</summary>
    public static DeviceInfo? FindCableCapture(IEnumerable<DeviceInfo> captures) =>
        captures.FirstOrDefault(d => d.Name.StartsWith("CABLE Output", StringComparison.OrdinalIgnoreCase))
        ?? captures.FirstOrDefault(d => d.Name.Contains("VB-Audio Virtual Cable", StringComparison.OrdinalIgnoreCase));

    public static bool IsCable(DeviceInfo d) =>
        d.Name.Contains("VB-Audio", StringComparison.OrdinalIgnoreCase) || d.Name.StartsWith("CABLE ", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Changes the Windows default playback device. Windows has no documented API for this; this is the same
/// long-standing (Windows 7 → 11) interface the Sound control panel itself uses.
/// </summary>
public static class DefaultDeviceSwitcher
{
    [ComImport, Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")]
    class PolicyConfigClient { }

    [ComImport, Guid("f8679f50-850a-41cf-9c72-430f290290c8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPolicyConfig
    {
        // Only SetDefaultEndpoint is used; the others just hold their place in the method table.
        [PreserveSig] int GetMixFormat(IntPtr a, IntPtr b);
        [PreserveSig] int GetDeviceFormat(IntPtr a, int b, IntPtr c);
        [PreserveSig] int ResetDeviceFormat(IntPtr a);
        [PreserveSig] int SetDeviceFormat(IntPtr a, IntPtr b, IntPtr c);
        [PreserveSig] int GetProcessingPeriod(IntPtr a, int b, IntPtr c, IntPtr d);
        [PreserveSig] int SetProcessingPeriod(IntPtr a, IntPtr b);
        [PreserveSig] int GetShareMode(IntPtr a, IntPtr b);
        [PreserveSig] int SetShareMode(IntPtr a, IntPtr b);
        [PreserveSig] int GetPropertyValue(IntPtr a, int b, IntPtr c, IntPtr d);
        [PreserveSig] int SetPropertyValue(IntPtr a, int b, IntPtr c, IntPtr d);
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int role);
        [PreserveSig] int SetEndpointVisibility(IntPtr a, int b);
    }

    /// <summary>Makes the device the default for normal playback (console + multimedia roles). Returns false on failure.</summary>
    public static bool SetDefault(string deviceId)
    {
        try
        {
            var cfg = (IPolicyConfig)new PolicyConfigClient();
            try
            {
                int hr0 = cfg.SetDefaultEndpoint(deviceId, 0);   // eConsole
                int hr1 = cfg.SetDefaultEndpoint(deviceId, 1);   // eMultimedia
                return hr0 >= 0 && hr1 >= 0;
            }
            finally
            {
                Marshal.ReleaseComObject(cfg);
            }
        }
        catch
        {
            return false;
        }
    }
}
