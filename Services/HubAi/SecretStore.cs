using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using ClassSoftwareHub.Desktop.Core;

namespace ClassSoftwareHub.Desktop.Services.HubAi;


















public static class SecretStore
{
    
    private const string FileName = "hubai-key.bin";

    
    private const int CryptProtectUiForbidden = 0x1;

    
    
    
    
    private static string PathOf(string keyId)
        => Path.Combine(AppPaths.DataDir,
            string.IsNullOrEmpty(keyId) || keyId == "default" ? FileName : $"hubai-key-{keyId}.bin");

    
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
            
            
            AppLog.Warn("hubai", "密钥无法解密（可能换了账户或从别的机器拷来），需要重新填写");
            AppLog.Exception("hubai", ex, "解密模型密钥");
            return "";
        }
    }

    
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

    
    
    
    
    
    
    
    
    
    public static bool SelfTest()
    {
        try
        {
            var plain = Encoding.UTF8.GetBytes("hubai-selftest-" + Guid.NewGuid().ToString("N"));
            var cipher = Protect(plain);

            
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
