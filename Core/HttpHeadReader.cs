using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace ClassSoftwareHub.Desktop.Core;

/// <summary>
/// 读 HTTP 请求头，并把它后面**已经读进来的字节**原样交回（body 的前缀）。
///
/// <para>
/// 抽成独立文件是为了能单测 —— 它曾经把局域网喊话整整卡了一个下午（2026-10-25）：
/// <c>HttpClient</c> 发小请求时，请求头与 body 往往在**同一个 TCP 包**里到达，
/// 一次 <c>Read</c> 就把两者一起读了回来。老实现把整段内容当成「请求头」解析完就丢掉，
/// 之后按 <c>Content-Length</c> 再去等 body 就永远等不到 ——
/// 现象是「连接建立成功、监听正常、接受循环也在跑，但服务端永不应答」，客户端只能超时。
/// 当时先怀疑防火墙、再怀疑多网卡、再怀疑端口预留，全是被这个假象带偏的。
/// </para>
/// <para>
/// ⛔ 所以调用方必须把 <c>Leftover</c> 当作 body 的开头，不要丢。
/// </para>
/// </summary>
public static class HttpHeadReader
{
    /// <summary>请求头上限。超过这个长度还没见到空行，就认为对方不是在说 HTTP。</summary>
    public const int MaxHeadBytes = 32 * 1024;

    private const int BufferSize = 4096;

    /// <summary>
    /// 读到空行（<c>0D 0A 0D 0A</c>）为止。
    /// </summary>
    /// <returns>
    /// <c>Head</c> 含空行本身（<c>null</c> = 没读到，对方断开或超限）；
    /// <c>Leftover</c> 是空行之后已经读到的字节，可能为空。
    /// </returns>
    public static async Task<(byte[]? Head, byte[] Leftover)> ReadAsync(Stream stream, CancellationToken token)
    {
        var buffer = new byte[BufferSize];
        var raw = new List<byte>();

        while (raw.Count < MaxHeadBytes)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), token).ConfigureAwait(false);
            if (n <= 0) return (null, Array.Empty<byte>());

            for (var i = 0; i < n; i++) raw.Add(buffer[i]);

            // 按字节找 0D 0A 0D 0A（即空行）：头部虽是 ASCII，用字节定位才能准确切出 body。
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
