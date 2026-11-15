using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using ClassSoftwareHub.Desktop.Core;

namespace ClassSoftwareHub.Desktop.Services.HubAi;

/// <summary>
/// 模型服务密钥的本地保管（2026-10-18）。
///
/// <para>
/// 用 Windows 自带的 <b>DPAPI</b>（<c>CryptProtectData</c>，CurrentUser 作用域）加密后落盘。
/// 效果：换个用户、换台机器都解不开，而且用户登录后不需要再输密码。
/// </para>
/// <para>
/// ⛔⛔ 铁律，改之前先看：
/// <list type="number">
///   <item><b>绝不进 settings.json</b>。那是明文 JSON，任何人打开就能看到。</item>
///   <item><b>绝不进日志</b>。任何 <c>AppLog</c> 调用都不许带密钥，报错只记状态码与错误类型。</item>
///   <item><b>不引 NuGet 包</b>。<c>System.Security.Cryptography.ProtectedData</c> 是个额外依赖，
///         这里直接 P/Invoke <c>crypt32.dll</c>，零依赖、行为一致。</item>
/// </list>
/// </para>
/// </summary>
public static class SecretStore
{
    /// <summary>密钥文件名前缀。默认条目用老的 <c>hubai-key.bin</c>，其余带 id 后缀。</summary>
    private const string FileName = "hubai-key.bin";

    /// <summary>禁止弹出任何 UI（学校电脑无人值守，弹窗会卡住请求）。</summary>
    private const int CryptProtectUiForbidden = 0x1;

    /// <summary>
    /// 按条目取文件路径（2026-10-18 多模型）。
    /// ⚠️ "default" 刻意沿用老文件名 <c>hubai-key.bin</c> —— 迁移过来的老用户不用重填密钥。
    /// </summary>
    private static string PathOf(string keyId)
        => Path.Combine(AppPaths.DataDir,
            string.IsNullOrEmpty(keyId) || keyId == "default" ? FileName : $"hubai-key-{keyId}.bin");

    /// <summary>当前有没有存过密钥。界面用它决定显示「已保存」还是「未配置」。</summary>
    public static bool HasSecret => Has();

    public static bool Has(string keyId = "default")
    {
        try
        {
            var path = PathOf(keyId);
            return File.Exists(path) && new FileInfo(path).Length > 0;
        }
        catch { return false; }
    }

    /// <summary>保存密钥。传空字符串等于清除。</summary>
    public static bool Save(string? secret, string keyId = "default")
    {
        if (string.IsNullOrWhiteSpace(secret))
        {
            Clear(keyId);
            return true;
        }

        try
        {
            var plain = Encoding.UTF8.GetBytes(secret.Trim());
            var cipher = Protect(plain);

            Directory.CreateDirectory(AppPaths.DataDir);
            File.WriteAllBytes(PathOf(keyId), cipher);

            AppLog.Step("hubai", "已加密保存模型密钥");
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Exception("hubai", ex, "保存模型密钥");
            return false;
        }
    }

    /// <summary>读出密钥。没有或解不开都返回空串（调用方按"未配置"处理）。</summary>
    public static string Load(string keyId = "default")
    {
        try
        {
            if (!Has(keyId)) return "";

            var cipher = File.ReadAllBytes(PathOf(keyId));
            var plain = Unprotect(cipher);
            return Encoding.UTF8.GetString(plain);
        }
        catch (Exception ex)
        {
            // 常见原因：换了 Windows 账户、把数据目录从别的机器拷过来。
            // 这不算程序错误，记一条警告就够，界面会当成"未配置"让用户重填。
            AppLog.Warn("hubai", "密钥无法解密（可能换了账户或从别的机器拷来），需要重新填写");
            AppLog.Exception("hubai", ex, "解密模型密钥");
            return "";
        }
    }

    /// <summary>删除已保存的密钥。设置页的「清除密钥」走这里。</summary>
    public static void Clear(string keyId = "default")
    {
        try
        {
            var path = PathOf(keyId);
            if (File.Exists(path)) File.Delete(path);

            AppLog.Step("hubai", "已清除模型密钥");
        }
        catch (Exception ex)
        {
            AppLog.Exception("hubai", ex, "删除模型密钥");
        }
    }

    /// <summary>
    /// DPAPI 往返自检（纯内存，不落盘）。只在调试版调用。
    ///
    /// <para>
    /// 为什么要它：这套 <c>crypt32</c> 调用是手写 P/Invoke，<c>DATA_BLOB</c> 的字段顺序或
    /// <c>IntPtr</c>/<c>int</c> 宽度写错时，**编译期完全看不出来**，要到用户真的保存密钥才炸。
    /// 这里用一段随机串走一遍"加密→解密→比对"，跑不通会立刻在日志里留痕。
    /// </para>
    /// </summary>
    public static bool SelfTest()
    {
        try
        {
            var plain = Encoding.UTF8.GetBytes("hubai-selftest-" + Guid.NewGuid().ToString("N"));
            var cipher = Protect(plain);

            // 密文必须和明文不同，否则等于没加密
            if (cipher.Length == 0 || cipher.AsSpan().SequenceEqual(plain)) return false;

            var back = Unprotect(cipher);
            var ok = back.AsSpan().SequenceEqual(plain);

            if (!ok) AppLog.Warn("hubai", "DPAPI 自检：解密结果与原文不一致");
            return ok;
        }
        catch (Exception ex)
        {
            AppLog.Exception("hubai", ex, "DPAPI 自检");
            return false;
        }
    }

    // ══════════════════════════════════════════════════════
    //  DPAPI
    // ══════════════════════════════════════════════════════

    private static byte[] Protect(byte[] plain)
    {
        var inputPtr = IntPtr.Zero;
        var output = default(DATA_BLOB);

        try
        {
            inputPtr = Marshal.AllocHGlobal(plain.Length);
            Marshal.Copy(plain, 0, inputPtr, plain.Length);

            var input = new DATA_BLOB { cbData = plain.Length, pbData = inputPtr };

            if (!CryptProtectData(ref input, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                    CryptProtectUiForbidden, ref output))
            {
                throw new InvalidOperationException(
                    $"CryptProtectData 失败（错误码 {Marshal.GetLastWin32Error()}）");
            }

            var result = new byte[output.cbData];
            Marshal.Copy(output.pbData, result, 0, output.cbData);
            return result;
        }
        finally
        {
            if (inputPtr != IntPtr.Zero) Marshal.FreeHGlobal(inputPtr);
            if (output.pbData != IntPtr.Zero) LocalFree(output.pbData);
        }
    }

    private static byte[] Unprotect(byte[] cipher)
    {
        var inputPtr = IntPtr.Zero;
        var output = default(DATA_BLOB);

        try
        {
            inputPtr = Marshal.AllocHGlobal(cipher.Length);
            Marshal.Copy(cipher, 0, inputPtr, cipher.Length);

            var input = new DATA_BLOB { cbData = cipher.Length, pbData = inputPtr };

            if (!CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                    CryptProtectUiForbidden, ref output))
            {
                throw new InvalidOperationException(
                    $"CryptUnprotectData 失败（错误码 {Marshal.GetLastWin32Error()}）");
            }

            var result = new byte[output.cbData];
            Marshal.Copy(output.pbData, result, 0, output.cbData);
            return result;
        }
        finally
        {
            if (inputPtr != IntPtr.Zero) Marshal.FreeHGlobal(inputPtr);
            if (output.pbData != IntPtr.Zero) LocalFree(output.pbData);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DATA_BLOB
    {
        public int cbData;
        public IntPtr pbData;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref DATA_BLOB pDataIn, string? szDataDescr,
        IntPtr pOptionalEntropy, IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, ref DATA_BLOB pDataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref DATA_BLOB pDataIn, IntPtr ppszDataDescr,
        IntPtr pOptionalEntropy, IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, ref DATA_BLOB pDataOut);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LocalFree(IntPtr hMem);
}
