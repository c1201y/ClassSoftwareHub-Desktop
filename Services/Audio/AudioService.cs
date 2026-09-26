using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;

namespace ClassSoftwareHub.Desktop.Services.Audio;

/// <summary>音量合成器里的一行 = 一个正在使用音频的应用。</summary>
public sealed class AudioSessionItem
{
    /// <summary>进程 id（同一应用的多个会话会聚合到它身上）。</summary>
    public uint ProcessId { get; init; }

    /// <summary>显示名（优先取 exe 的「文件说明」，跟系统音量合成器一个口径）。</summary>
    public string Name { get; init; } = "";

    /// <summary>进程 exe 的完整路径（提取软件图标用；拿不到就是空串）。</summary>
    public string ExePath { get; init; } = "";

    /// <summary>0~100。</summary>
    public int VolumePercent { get; set; }

    public bool Muted { get; set; }

    /// <summary>内部用：这个应用名下的所有会话（一个程序可能开好几条）。</summary>
    internal List<ISimpleAudioVolume> Volumes { get; } = new();
}

/// <summary>
/// 系统音量：主音量（= 任务栏那个喇叭）+ 音量合成器（每个应用一条）。
/// 全走 Windows Core Audio，不引第三方包。
///
/// 设计原则：**读不到就老实说读不到**。没声卡、音频服务被停、设备被拔掉时，
/// 所有方法返回 false / 空列表，界面上显示「读不到音量设备」就行 —— 绝不抛异常把应用带崩。
/// </summary>
public static class AudioService
{
    // ── 主音量 ────────────────────────────────────────────────────────────

    /// <summary>读主音量。返回 false 表示拿不到设备（界面据此显示"读不到"）。</summary>
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

    /// <summary>设主音量（0~100）。</summary>
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

    /// <summary>主音量静音开关。</summary>
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

    // ── 音量合成器 ────────────────────────────────────────────────────────

    /// <summary>
    /// 列出**正在发声**的应用（跟系统音量合成器的口径一致：安静的会话不占位）。
    /// 同一个进程的多个会话会合并成一条，调的时候一起调。
    /// </summary>
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

                    // 状态：只留 Active（正在放声音的）
                    if (control.GetState(out var state) < 0) continue;
                    if (state != AudioSessionState.Active) continue;

                    // 进程 id 要 Control2 才问得到
                    if (control is not IAudioSessionControl2 control2) continue;
                    if (control2.GetProcessId(out var pid) < 0 || pid == 0) continue;

                    // ⚠️ 静音的会话状态仍是 Active，这里不过滤 —— 「静音」本身要显示成静音状态而不是消失。

                    // 音量控制接口：同一个 COM 对象再 QI 一次就能拿到
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
                    // 单条会话读失败不该拖垮整个列表（有些系统会话会拒绝访问）
                }
                // ⚠️ 这里**绝对不要** ReleaseCom(control)：control / control2 / simple 是
                //    **同一个 RCW** 的不同接口视图，ReleaseComObject 会把整个 RCW 作废 ——
                //    于是上面刚存进 Volumes 的 ISimpleAudioVolume 变成死引用，之后调音量一律失败
                //    （表现为「读得到、写不进去」，我在这儿栽过一次）。
                //    这些会话对象由 GC 的终结器负责释放，别手动干预。
            }
        }
        catch
        {
            // 枚举整体失败：返回已经拿到的部分
        }
        finally
        {
            ReleaseCom(enumerator);
            ReleaseCom(mgr);
        }

        // 名字一样的合并过之后，按名字排一下，界面上顺序才稳定（不然每次刷新都在跳）
        return result.Values.OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>设某个应用的音量（0~100），它名下所有会话一起改。</summary>
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

    /// <summary>某个应用单独静音。</summary>
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

    // ── 内部 ──────────────────────────────────────────────────────────────

    private static int ToPercent(float scalar) => (int)Math.Round(Math.Clamp(scalar, 0f, 1f) * 100);

    private static float ToScalar(int percent) => Math.Clamp(percent, 0, 100) / 100f;

    /// <summary>
    /// 拿默认播放设备的主音量接口。
    /// ⚠️ 先试 eMultimedia；有些机器上默认设备只在 eConsole 下能拿到，所以两个都兜一遍。
    /// </summary>
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

            // 退回 Console 角色再试一次（少数机器的默认设备只在这一档可见）
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

    /// <summary>
    /// 应用名：优先 exe 的「文件说明」（= 系统音量合成器显示的那个，比如"微信"），
    /// 拿不到就退回进程名，再不行就"应用 N"。系统声音那条单独给个名字。
    /// </summary>
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
                // 跨位数 / 权限不够时读不到主模块，正常现象
            }

            return p.ProcessName;
        }
        catch
        {
            return $"应用 {pid}";
        }
    }

    /// <summary>进程 exe 的完整路径（提取图标用）。跨位数/权限不够读不到就返回空串。</summary>
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

    /// <summary>ComImport 接口一律别让 GC 自己收 —— 手动 Release，免得音频服务那边攒一堆引用。</summary>
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
