using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;

namespace ClassSoftwareHub.Desktop.Services;

/// <summary>
/// 解「间接字符串」（indirect string）—— 注册表里那种 <c>@{包全名?ms-resource://…}</c> 形式的名字。
///
/// 为什么需要它（2026-10-13 加）：
///   Microsoft Store / MSIX 包在注册表里登记的 DisplayName 常常不是人话，而是
///   <c>@{Microsoft.Windows.FileExplorer_10.0.26100.2_neutral_neutral_cw5n1h2txyewy?ms-resource://FileExplorer/…}</c>
///   这样的引用 —— 必须交给系统用包自己的资源去展开。
///   旧实现只认 <c>ms-resource:</c> 前缀，认不出这种 <c>@{…}</c> 形式，于是「应用卸载」页上
///   整屏都是那串东西（Nick 2026-10-13 截图里「无关的太多了」的一大半就是它）。
///
/// 走 shell 的 <c>SHLoadIndirectString</c>：它就是 Windows 自己解这类引用的那个函数，
/// 「程序和功能」能显示出中文名靠的也是它。不需要管理员，不需要包身份，任意线程可调。
///
/// ⚠️ 解不出来是常态（老式 <c>ms-resource:AppDisplayName</c> 缺包上下文就没有解），
///    所以**永不抛异常**，失败一律返回空串，由调用方决定退化成什么（见 InstalledApps）。
/// </summary>
internal static class IndirectString
{
    /// <summary>同一个串在这轮扫描里会被反复问到 → 缓存一下（值可能是空串，也要缓存）。</summary>
    private static readonly ConcurrentDictionary<string, string> _cache = new(StringComparer.Ordinal);

    /// <summary>试着把 <paramref name="value"/> 展开成人话。解不出来返回空串。</summary>
    public static string Resolve(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";

        return _cache.GetOrAdd(value, static v =>
        {
            // 最多 1024 字符：包显示名再长也长不过这个，够用且不用二次扩容重试
            var buf = new StringBuilder(1024);
            try
            {
                var hr = SHLoadIndirectString(v, buf, buf.Capacity, IntPtr.Zero);
                if (hr != 0) return "";          // S_OK = 0，其它一律当"解不出来"
            }
            catch
            {
                // shlwapi 加载失败之类的极端情形 —— 当"解不出来"处理，别把整轮扫描带崩
                return "";
            }

            var s = buf.ToString().Trim();

            // ⚠️ 系统有时会把**原串**照抄回输出缓冲（尤其是解不开的 @!w~ 形式）。
            //    那不算展开成功 —— 原样返回等于骗调用方，它就不会去走退化路了。
            if (s.Length == 0 || s[0] == '@' || s.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase))
                return "";

            return s;
        });
    }

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHLoadIndirectString(
        string pszSource, StringBuilder pszOutBuf, int cchOutBuf, IntPtr ppvReserved);
}
