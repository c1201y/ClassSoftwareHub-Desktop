using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;

namespace ClassSoftwareHub.Desktop.Services.Audio;


public sealed class AudioSessionItem
{
    
    public uint ProcessId { get; init; }

    
    public string Name { get; init; } = "";

    
    public string ExePath { get; init; } = "";

    
    public int VolumePercent { get; set; }

    public bool Muted { get; set; }

    
    internal List<ISimpleAudioVolume> Volumes { get; } = new();
}








public static class AudioService
{
    

    
    public static bool TryGetMaster(out int percent, out bool muted)
    {
        percent = 0;
        muted = false;

        var vol = TryGetEndpointVolume();
        if (vol is null) return false;

        try
        {
            if (vol.GetMasterVolumeLevelScalar(out var scalar) < 0) return false;
            if (vol.GetMute(out var m) < 0) return false;

            percent = ToPercent(scalar);
            muted = m;
            return true;
        }
        catch
        {
            return false;
        }
    }

    
    public static bool SetMasterPercent(int percent)
    {
        var vol = TryGetEndpointVolume();
        if (vol is null) return false;

        try
        {
            var ctx = Guid.Empty;
            return vol.SetMasterVolumeLevelScalar(ToScalar(percent), ref ctx) >= 0;
        }
        catch
        {
            return false;
        }
    }

    
    public static bool SetMasterMute(bool muted)
    {
        var vol = TryGetEndpointVolume();
        if (vol is null) return false;

        try
        {
            var ctx = Guid.Empty;
            return vol.SetMute(muted, ref ctx) >= 0;
        }
        catch
        {
            return false;
        }
    }

    

    
    
    
    
    public static List<AudioSessionItem> GetActiveSessions()
    {
        var result = new Dictionary<uint, AudioSessionItem>();

        var mgr = TryGetSessionManager();
        if (mgr is null) return new List<AudioSessionItem>();

        IAudioSessionEnumerator? enumerator = null;
        try
        {
            if (mgr.GetSessionEnumerator(out enumerator) < 0 || enumerator is null) return new List<AudioSessionItem>();

            if (enumerator.GetCount(out var count) < 0) return new List<AudioSessionItem>();

            for (var i = 0; i < count; i++)
            {
                IAudioSessionControl? control = null;
                try
                {
                    if (enumerator.GetSession(i, out control) < 0 || control is null) continue;

                    
                    if (control.GetState(out var state) < 0) continue;
                    if (state != AudioSessionState.Active) continue;

                    
                    if (control is not IAudioSessionControl2 control2) continue;
                    if (control2.GetProcessId(out var pid) < 0 || pid == 0) continue;

                    

                    
                    if (control is not ISimpleAudioVolume simple) continue;

                    simple.GetMasterVolume(out var v);
                    simple.GetMute(out var m);

                    if (!result.TryGetValue(pid, out var item))
                    {
                        item = new AudioSessionItem
                        {
                            ProcessId = pid,
                            Name = DisplayNameOf(pid),
                            ExePath = ExePathOf(pid),
                            VolumePercent = ToPercent(v),
                            Muted = m,
                        };
                        result[pid] = item;
                    }

                    item.Volumes.Add(simple);
                }
                catch
                {
                    
                }
                
                
                
                
                
            }
        }
        catch
        {
            
        }
        finally
        {
            ReleaseCom(enumerator);
            ReleaseCom(mgr);
        }

        
        return result.Values.OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    
    public static bool SetSessionPercent(AudioSessionItem item, int percent)
    {
        var ok = false;
        foreach (var v in item.Volumes)
        {
            try
            {
                var ctx = Guid.Empty;
                if (v.SetMasterVolume(ToScalar(percent), ref ctx) >= 0) ok = true;
            }
            catch { }
        }
        return ok;
    }

    
    public static bool SetSessionMute(AudioSessionItem item, bool muted)
    {
        var ok = false;
        foreach (var v in item.Volumes)
        {
            try
            {
                var ctx = Guid.Empty;
                if (v.SetMute(muted, ref ctx) >= 0) ok = true;
            }
            catch { }
        }
        return ok;
    }

    

    private static int ToPercent(float scalar) => (int)Math.Round(Math.Clamp(scalar, 0f, 1f) * 100);

    private static float ToScalar(int percent) => Math.Clamp(percent, 0, 100) / 100f;

    
    
    
    
    private static IAudioEndpointVolume? TryGetEndpointVolume()
    {
        var device = TryGetDefaultRenderDevice();
        if (device is null) return null;

        try
        {
            var iid = AudioIids.EndpointVolume;
            if (device.Activate(ref iid, ClsCtx.All, IntPtr.Zero, out var obj) < 0) return null;
            return obj as IAudioEndpointVolume;
        }
        catch
        {
            return null;
        }
        finally
        {
            ReleaseCom(device);
        }
    }

    private static IAudioSessionManager2? TryGetSessionManager()
    {
        var device = TryGetDefaultRenderDevice();
        if (device is null) return null;

        try
        {
            var iid = AudioIids.SessionManager2;
            if (device.Activate(ref iid, ClsCtx.All, IntPtr.Zero, out var obj) < 0) return null;
            return obj as IAudioSessionManager2;
        }
        catch
        {
            return null;
        }
        finally
        {
            ReleaseCom(device);
        }
    }

    private static IMMDevice? TryGetDefaultRenderDevice()
    {
        IMMDeviceEnumerator? enumerator = null;
        try
        {
            enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();

            if (enumerator.GetDefaultAudioEndpoint(EDataFlow.Render, ERole.Multimedia, out var dev) >= 0 && dev is not null)
                return dev;

            
            if (enumerator.GetDefaultAudioEndpoint(EDataFlow.Render, ERole.Console, out var dev2) >= 0 && dev2 is not null)
                return dev2;

            return null;
        }
        catch
        {
            return null;
        }
        finally
        {
            ReleaseCom(enumerator);
        }
    }

    
    
    
    
    private static string DisplayNameOf(uint pid)
    {
        try
        {
            using var p = Process.GetProcessById((int)pid);
            if (p.ProcessName.Equals("SystemSounds", StringComparison.OrdinalIgnoreCase)) return "系统声音";

            try
            {
                var desc = p.MainModule?.FileVersionInfo?.FileDescription;
                if (!string.IsNullOrWhiteSpace(desc)) return desc!.Trim();
            }
            catch
            {
                
            }

            return p.ProcessName;
        }
        catch
        {
            return $"应用 {pid}";
        }
    }

    
    private static string ExePathOf(uint pid)
    {
        try
        {
            using var p = Process.GetProcessById((int)pid);
            return p.MainModule?.FileName ?? "";
        }
        catch
        {
            return "";
        }
    }

    
    private static void ReleaseCom(object? o)
    {
        if (o is null) return;
        try
        {
            if (Marshal.IsComObject(o)) Marshal.ReleaseComObject(o);
        }
        catch { }
    }
}
