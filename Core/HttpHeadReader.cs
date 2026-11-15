using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace ClassSoftwareHub.Desktop.Core;
















public static class HttpHeadReader
{
    
    public const int MaxHeadBytes = 32 * 1024;

    private const int BufferSize = 4096;

    
    
    
    
    
    
    
    public static async Task<(byte[]? Head, byte[] Leftover)> ReadAsync(Stream stream, CancellationToken token)
    {
        var buffer = new byte[BufferSize];
        var raw = new List<byte>();

        while (raw.Count < MaxHeadBytes)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), token).ConfigureAwait(false);
            if (n <= 0) return (null, Array.Empty<byte>());

            for (var i = 0; i < n; i++) raw.Add(buffer[i]);

            
            for (var i = 0; i + 3 < raw.Count; i++)
            {
                if (raw[i] != 13 || raw[i + 1] != 10 || raw[i + 2] != 13 || raw[i + 3] != 10) continue;

                var headEnd = i + 4;
                var head = raw.GetRange(0, headEnd).ToArray();
                var leftover = raw.Count > headEnd
                    ? raw.GetRange(headEnd, raw.Count - headEnd).ToArray()
                    : Array.Empty<byte>();
                return (head, leftover);
            }
        }

        return (null, Array.Empty<byte>());
    }
}
