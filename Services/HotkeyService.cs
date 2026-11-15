using System.Linq;
using System.Runtime.InteropServices;
using ClassSoftwareHub.Desktop.Core;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using WinRT.Interop;

namespace ClassSoftwareHub.Desktop.Services;








public enum HotkeyAction
{
    ShowMain = 0,
    Announce = 1,
    Timer = 2,
    PptTouch = 3,
}








public readonly record struct HotkeyCombo(uint Modifiers, uint VirtualKey)
{
    public const uint ModAlt = 0x0001;
    public const uint ModControl = 0x0002;
    public const uint ModShift = 0x0004;
    public const uint ModWin = 0x0008;

    
    internal const uint ModNoRepeat = 0x4000;

    
    private static readonly (string Name, uint Vk)[] NamedKeys =
    {
        ("Space", 0x20), ("Enter", 0x0D), ("Tab", 0x09), ("Backspace", 0x08),
        ("Delete", 0x2E), ("Insert", 0x2D), ("Home", 0x24), ("End", 0x23),
        ("PageUp", 0x21), ("PageDown", 0x22),
        ("Left", 0x25), ("Up", 0x26), ("Right", 0x27), ("Down", 0x28),
        ("Esc", 0x1B), ("Escape", 0x1B),
    };

    
    public static bool TryParse(string? text, out HotkeyCombo combo, out string? error)
    {
        combo = default;
        error = null;

        if (string.IsNullOrWhiteSpace(text))
        {
            error = "还没有设置组合键";
            return false;
        }

        var parts = text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 2)
        {
            error = "至少要有一个修饰键（Ctrl / Alt / Shift / Win）";
            return false;
        }

        uint mods = 0;
        for (var i = 0; i < parts.Length - 1; i++)
        {
            switch (parts[i].ToLowerInvariant())
            {
                case "ctrl":
                case "control":
                    mods |= ModControl;
                    break;
                case "alt":
                    mods |= ModAlt;
                    break;
                case "shift":
                    mods |= ModShift;
                    break;
                case "win":
                case "windows":
                    mods |= ModWin;
                    break;
                default:
                    error = $"无法识别修饰键「{parts[i]}」";
                    return false;
            }
        }

        if (mods == 0)
        {
            error = "至少要有一个修饰键（Ctrl / Alt / Shift / Win）";
            return false;
        }

        if (!TryParseKey(parts[^1], out var vk))
        {
            error = $"无法识别按键「{parts[^1]}」";
            return false;
        }

        combo = new HotkeyCombo(mods, vk);
        return true;
    }

    
    
    
    
    public static bool TryBuild(uint modifiers, uint virtualKey, out string text, out string? error)
    {
        text = string.Empty;
        error = null;

        if (modifiers == 0)
        {
            error = "要按住 Ctrl / Alt / Shift / Win 中的至少一个";
            return false;
        }

        if (!IsSupportedKey(virtualKey))
        {
            error = "该键不受支持（可用字母、数字、F1~F24 与方向 / 翻页等常用键）";
            return false;
        }

        text = ToText(modifiers, virtualKey);
        return true;
    }

    
    public static bool IsModifierKey(uint virtualKey)
        => virtualKey is 0x10 or 0x11 or 0x12 or 0x5B or 0x5C;

    public static bool IsSupportedKey(uint virtualKey)
        => (virtualKey >= 0x30 && virtualKey <= 0x39)      
           || (virtualKey >= 0x41 && virtualKey <= 0x5A)   
           || (virtualKey >= 0x70 && virtualKey <= 0x87)   
           || NamedKeys.Any(k => k.Vk == virtualKey);

    
    public static string ToText(uint modifiers, uint virtualKey)
        => string.Join('+',
            new[]
            {
                (modifiers & ModControl) != 0 ? "Ctrl" : null,
                (modifiers & ModAlt) != 0 ? "Alt" : null,
                (modifiers & ModShift) != 0 ? "Shift" : null,
                (modifiers & ModWin) != 0 ? "Win" : null,
                KeyName(virtualKey),
            }.OfType<string>());

    private static bool TryParseKey(string name, out uint vk)
    {
        vk = 0;

        if (name.Length == 1)
        {
            var c = char.ToUpperInvariant(name[0]);
            if (c >= 'A' && c <= 'Z')
            {
                vk = (uint)c;
                return true;
            }

            if (c >= '0' && c <= '9')
            {
                vk = (uint)c;
                return true;
            }
        }

        if (name.Length is 2 or 3 && (name[0] == 'F' || name[0] == 'f')
            && int.TryParse(name[1..], out var fn) && fn is >= 1 and <= 24)
        {
            vk = (uint)(0x70 + fn - 1);
            return true;
        }

        foreach (var (keyName, keyVk) in NamedKeys)
        {
            if (string.Equals(keyName, name, StringComparison.OrdinalIgnoreCase))
            {
                vk = keyVk;
                return true;
            }
        }

        return false;
    }

    private static string KeyName(uint vk)
    {
        if (vk >= 0x30 && vk <= 0x39) return ((char)vk).ToString();
        if (vk >= 0x41 && vk <= 0x5A) return ((char)vk).ToString();
        if (vk >= 0x70 && vk <= 0x87) return $"F{vk - 0x70 + 1}";

        foreach (var (name, keyVk) in NamedKeys)
            if (keyVk == vk)
                return name;

        return $"VK{vk:X2}";
    }
}
















public sealed class HotkeyService : IDisposable
{
    private const uint WmHotkey = 0x0312;
    private const int IdBase = 0x4C00;
    private const int TargetIdBase = 0x5100;
    private const int ErrorHotkeyAlreadyRegistered = 1409;
    private const uint SubclassId = 1;

    private readonly IntPtr _hwnd;
    private readonly Action<HotkeyAction> _handler;
    private readonly Action<string>? _targetHandler;
    private readonly DispatcherQueue? _queue;
    private readonly Dictionary<int, HotkeyAction> _bound = new();
    private readonly Dictionary<int, string> _targetBound = new();
    private readonly List<string> _conflicts = new();

    private IntPtr _refData;
    private bool _subclassed;
    private bool _disposed;

    public HotkeyService(Window window, Action<HotkeyAction> handler, Action<string>? targetHandler = null)
    {
        _targetHandler = targetHandler;
        _hwnd = WindowNative.GetWindowHandle(window);
        _handler = handler;
        _queue = window.DispatcherQueue;

        
        _refData = GCHandle.ToIntPtr(GCHandle.Alloc(this));

        _subclassed = SetWindowSubclass(_hwnd, SubclassProc, SubclassId, _refData);
        if (!_subclassed)
        {
            GCHandle.FromIntPtr(_refData).Free();
            _refData = IntPtr.Zero;
            AppLog.Warn("hotkey", "主窗口子类化失败，全局快捷键不可用");
        }
    }

    
    public IReadOnlyList<string> Conflicts => _conflicts;

    
    public void Apply(AppSettings settings)
    {
        if (_disposed) return;

        UnregisterAll();
        _conflicts.Clear();

        if (!settings.HotkeysEnabled)
        {
            AppLog.Info("hotkey", "总开关关闭，未注册任何组合键");
            return;
        }

        Bind(HotkeyAction.ShowMain, settings.HotkeyShow);
        Bind(HotkeyAction.Announce, settings.HotkeyAnnounce);
        Bind(HotkeyAction.Timer, settings.HotkeyTimer);
        Bind(HotkeyAction.PptTouch, settings.HotkeyPptTouch);

        var next = 0;
        foreach (var pair in settings.SidebarAppHotkeys ?? new Dictionary<string, string>())
        {
            if (string.IsNullOrWhiteSpace(pair.Key) || string.IsNullOrWhiteSpace(pair.Value)) continue;
            BindTarget(pair.Key, pair.Value, TargetIdBase + next);
            next++;
        }
    }

    private void Bind(HotkeyAction action, string? text)
    {
        if (!HotkeyCombo.TryParse(text, out var combo, out var error))
        {
            _conflicts.Add($"{ActionName(action)}：{error}");
            return;
        }

        var id = IdBase + (int)action;
        if (!RegisterHotKey(_hwnd, id, combo.Modifiers | HotkeyCombo.ModNoRepeat, combo.VirtualKey))
        {
            var code = Marshal.GetLastWin32Error();
            var reason = code == ErrorHotkeyAlreadyRegistered ? "已被其他程序占用" : $"系统拒绝注册（错误码 {code}）";
            _conflicts.Add($"{HotkeyCombo.ToText(combo.Modifiers, combo.VirtualKey)}（{ActionName(action)}）{reason}");
            AppLog.Warn("hotkey", $"{ActionName(action)} 注册失败：{reason}");
            return;
        }

        _bound[id] = action;
        AppLog.Info("hotkey", $"{ActionName(action)} → {HotkeyCombo.ToText(combo.Modifiers, combo.VirtualKey)} 已注册");
    }

    private void BindTarget(string appId, string? text, int id)
    {
        var app = App.Settings.Current.PinnedApps?.FirstOrDefault(a => a.Id == appId);
        var name = app is null ? "快捷应用" : PinnedAppLauncher.DisplayName(app);

        if (!HotkeyCombo.TryParse(text, out var combo, out var error))
        {
            _conflicts.Add($"{name}：{error}");
            return;
        }

        if (!RegisterHotKey(_hwnd, id, combo.Modifiers | HotkeyCombo.ModNoRepeat, combo.VirtualKey))
        {
            var code = Marshal.GetLastWin32Error();
            var reason = code == ErrorHotkeyAlreadyRegistered ? "已被其他程序占用" : $"系统拒绝注册（错误码 {code}）";
            _conflicts.Add($"{HotkeyCombo.ToText(combo.Modifiers, combo.VirtualKey)}（{name}）{reason}");
            AppLog.Warn("hotkey", $"{name} 注册失败：{reason}");
            return;
        }

        _targetBound[id] = appId;
        AppLog.Info("hotkey", $"{name} → {HotkeyCombo.ToText(combo.Modifiers, combo.VirtualKey)} 已注册");
    }

    
    
    
    
    
    
    
    public void Suspend()
    {
        if (_disposed) return;
        UnregisterAll();
        _conflicts.Clear();
        AppLog.Info("hotkey", "录制中：已临时注销全部组合键");
    }

    private void UnregisterAll()
    {
        foreach (var id in _bound.Keys)
        {
            if (!UnregisterHotKey(_hwnd, id))
                AppLog.Warn("hotkey", $"注销热键失败（id={id}，错误码 {Marshal.GetLastWin32Error()}）");
        }

        _bound.Clear();
        _targetBound.Clear();
    }

    internal static string ActionName(HotkeyAction action) => action switch
    {
        HotkeyAction.ShowMain => "显示主界面",
        HotkeyAction.Announce => "教室喊话",
        HotkeyAction.Timer => "课堂计时器",
        HotkeyAction.PptTouch => "PPT 触控",
        _ => action.ToString(),
    };

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        UnregisterAll();

        if (_subclassed)
        {
            try { RemoveWindowSubclass(_hwnd, SubclassProc, SubclassId); }
            catch (Exception ex) { AppLog.Warn("hotkey", "还原窗口过程失败：" + ex.Message); }
            _subclassed = false;
        }

        if (_refData != IntPtr.Zero)
        {
            GCHandle.FromIntPtr(_refData).Free();
            _refData = IntPtr.Zero;
        }
    }

    

    private delegate IntPtr SubclassProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, UIntPtr idSubclass, IntPtr refData);

    
    private static readonly SubclassProcDelegate SubclassProc = SubclassProcCore;

    private static IntPtr SubclassProcCore(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, UIntPtr idSubclass, IntPtr refData)
    {
        if (msg == WmHotkey && refData != IntPtr.Zero)
        {
            HotkeyService? service = null;
            try { service = GCHandle.FromIntPtr(refData).Target as HotkeyService; }
            catch (InvalidOperationException) { }

            if (service is not null && service._bound.TryGetValue(wParam.ToInt32(), out var action))
            {
                var handler = service._handler;
                if (service._queue is { } queue)
                    queue.TryEnqueue(() => handler(action));
                else
                    handler(action);

                return IntPtr.Zero;
            }

            if (service is not null && service._targetBound.TryGetValue(wParam.ToInt32(), out var target)
                && service._targetHandler is { } targetHandler)
            {
                var handler = targetHandler;
                if (service._queue is { } queue)
                    queue.TryEnqueue(() => handler(target));
                else
                    handler(target);

                return IntPtr.Zero;
            }
        }

        return DefSubclassProc(hWnd, msg, wParam, lParam);
    }

    [DllImport("comctl32.dll", SetLastError = true)]
    private static extern bool SetWindowSubclass(IntPtr hWnd, SubclassProcDelegate pfnSubclass, UIntPtr idSubclass, IntPtr refData);

    [DllImport("comctl32.dll")]
    private static extern bool RemoveWindowSubclass(IntPtr hWnd, SubclassProcDelegate pfnSubclass, UIntPtr idSubclass);

    [DllImport("comctl32.dll")]
    private static extern IntPtr DefSubclassProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
