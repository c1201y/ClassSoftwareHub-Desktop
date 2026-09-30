using System;
using System.Runtime.InteropServices;
using ClassSoftwareHub.Desktop.Data;

namespace ClassSoftwareHub.Desktop.Services.VirtualKeyboard;

/// <summary>
/// 往当前前台窗口送键。
///
/// **一律送虚拟键码**（不是 Unicode 字符）：中文输入法只认虚拟键码，送 Unicode 会绕过输入法 ——
/// 打拼音出来的是字母，不是汉字。标点也走 OEM 键码（<c>0xBC</c> 之类），全角半角交给输入法决定。
///
/// ⚠️ <c>INPUT</c> 结构在 **x64 上是 40 字节**（联合体按最长的 <c>MOUSEINPUT</c> 32 字节算，
///    前面 <c>type</c> 4 字节 + 4 字节对齐填充）。少 8 字节的话 <c>SendInput</c> 会**整批拒收**
///    （返回 0、<c>GetLastError</c> = 87）。构造完先 AssertSize 一次，别等上线才发现。
/// </summary>
public static class KeyInjector
{
    private const uint InputKeyboard = 1;
    private const uint KeyEventExtended = 0x0001;
    private const uint KeyEventKeyUp = 0x0002;
    private const uint KeyEventUnicode = 0x0004;

    private static bool _sizeChecked;

    /// <summary>按一下某个虚拟键（自带抬起）。</summary>
    /// <param name="shift">是否同时按住 Shift（送大写 / 上档符号用）。</param>
    public static void Tap(ushort vk, bool shift = false, bool extended = false)
    {
        EnsureSize();

        var ext = extended || KeyCode.IsExtended(vk);
        var flags = ext ? KeyEventExtended : 0u;

        if (shift)
        {
            Send(
                Keyboard(KeyCode.LShift, 0, KeyEventExtended),
                Keyboard(KeyCode.LShift, 0, KeyEventExtended | KeyEventKeyUp));
        }

        Send(
            Keyboard(vk, 0, flags),
            Keyboard(vk, 0, flags | KeyEventKeyUp));
    }

    /// <summary>修饰键组合（粘滞 Ctrl / ⊞ / Alt 用）：按下修饰键 → 按一下目标键 → 松开修饰键。</summary>
    public static void Chord(ushort modifier, ushort vk, bool shift = false, bool extended = false)
    {
        EnsureSize();

        var modFlags = KeyCode.IsExtended(modifier) ? KeyEventExtended : 0u;
        var keyFlags = (extended || KeyCode.IsExtended(vk)) ? KeyEventExtended : 0u;

        var n = 0;
        var buf = new INPUT[8];

        buf[n++] = Keyboard(modifier, 0, modFlags);
        if (shift) buf[n++] = Keyboard(KeyCode.LShift, 0, KeyEventExtended);

        buf[n++] = Keyboard(vk, 0, keyFlags);
        buf[n++] = Keyboard(vk, 0, keyFlags | KeyEventKeyUp);

        if (shift) buf[n++] = Keyboard(KeyCode.LShift, 0, KeyEventExtended | KeyEventKeyUp);
        buf[n++] = Keyboard(modifier, 0, modFlags | KeyEventKeyUp);

        Send(buf, n);
    }

    /// <summary>
    /// 直接送一段文本（走 <c>KEYEVENTF_UNICODE</c>，绕开键盘布局与输入法）。
    /// 当前布局没覆盖到的字符才用它 —— 平时不要走这条路，会让输入法失效。
    /// </summary>
    public static void Text(string text)
    {
        EnsureSize();
        if (text.Length == 0) return;

        var buf = new INPUT[text.Length * 2];
        var n = 0;
        foreach (var ch in text)
        {
            buf[n++] = Keyboard(0, ch, KeyEventUnicode);
            buf[n++] = Keyboard(0, ch, KeyEventUnicode | KeyEventKeyUp);
        }
        Send(buf, n);
    }

    // ── 内部 ────────────────────────────────────────────────

    private static void EnsureSize()
    {
        if (_sizeChecked) return;
        _sizeChecked = true;

        var size = Marshal.SizeOf<INPUT>();
        if (size != 40)
        {
            // 结构对不上就别送了 —— 送出去也是整批被拒，反而更难查。
            VkbdLog.Write($"⚠️ INPUT 结构大小异常：{size} 字节（x64 应为 40），键注入已停用");
        }
    }

    private static INPUT Keyboard(ushort vk, ushort scan, uint flags) => new()
    {
        Type = InputKeyboard,
        Data = new InputUnion
        {
            Keyboard = new KEYBDINPUT
            {
                Vk = vk,
                Scan = scan,
                Flags = flags,
                Time = 0,
                ExtraInfo = IntPtr.Zero,
            },
        },
    };

    private static void Send(params INPUT[] inputs) => Send(inputs, inputs.Length);

    private static void Send(INPUT[] inputs, int count)
    {
        if (count <= 0) return;

        var sent = SendInput((uint)count, inputs, Marshal.SizeOf<INPUT>());
        if (sent != count)
        {
            // 常见原因：前台窗口提权了，普通权限的注入会被 UIPI 拦掉。
            VkbdLog.Write($"⚠️ 送键不完整：想送 {count} 个事件，实际送出 {sent}（前台窗口可能提权了）");
        }
    }

    // ── Win32 ───────────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint Type;
        public InputUnion Data;
    }

    // 联合体必须显式布局；三个成员都从偏移 0 开始。
    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT Mouse;
        [FieldOffset(0)] public KEYBDINPUT Keyboard;
        [FieldOffset(0)] public HARDWAREINPUT Hardware;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort Vk;
        public ushort Scan;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int Dx;
        public int Dy;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HARDWAREINPUT
    {
        public uint Msg;
        public ushort ParamL;
        public ushort ParamH;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, INPUT[] inputs, int size);
}
