using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;

namespace ClassSoftwareHub.Desktop.Services;

















internal static class IndirectString
{
    
    private static readonly ConcurrentDictionary<string, string> _cache = new(StringComparer.Ordinal);

    
    public static string Resolve(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";

        return _cache.GetOrAdd(value, static v =>
        {
            
            var buf = new StringBuilder(1024);
            try
            {
                var hr = SHLoadIndirectString(v, buf, buf.Capacity, IntPtr.Zero);
                if (hr != 0) return "";          
            }
            catch
            {
                
                return "";
            }

            var s = buf.ToString().Trim();

            
            
            if (s.Length == 0 || s[0] == '@' || s.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase))
                return "";

            return s;
        });
    }

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHLoadIndirectString(
        string pszSource, StringBuilder pszOutBuf, int cchOutBuf, IntPtr ppvReserved);
}
