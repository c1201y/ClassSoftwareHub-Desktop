namespace ClassSoftwareHub.Desktop.Services;

/// <summary>
/// 发送端「已保存的电脑」（2026-10-25 Nick 提）：手动填过的配对码可以存下来，下次直接勾选，不用再背。
/// <para>
/// <see cref="Name"/> 是保存时顺带记下的显示名（当时在局域网里发现过就带上标识/机器名），
/// 没发现过就留空，列表退回只显示配对码。
/// </para>
/// </summary>
public sealed class AnnounceSavedPeer
{
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";

    /// <summary>保存时刻（Unix 秒）。列表按它倒序，最近存的在前。</summary>
    public long SavedTicks { get; set; }
}

/// <summary>
/// 接收端「谁保存了本机配对码」（2026-10-25 Nick 提：防滥用用）。
/// <para>
/// 由对方**在保存时经局域网**发过来的一条登记（<c>POST /bind</c>）：对方机器名 + 标识 + 来源 IP。
/// ⚠️ 只有「对方保存的那一刻」与本机在同一局域网、且通道开着，才登记得到；
///    不在同一网段保存的不会出现在这里（这是当前方案的边界，界面上如实说明）。
/// </para>
/// </summary>
public sealed class AnnounceBinder
{
    public string Code { get; set; } = "";      // 本机配对码（登记时留存，便于日后换码后区分）
    public string Name { get; set; } = "";      // 对方机器名
    public string Label { get; set; } = "";     // 对方本机标识
    public string Ip { get; set; } = "";        // 来源 IP（局域网登记才有）
    public long SeenTicks { get; set; }         // 最近一次登记时间（Unix 秒）

    /// <summary>
    /// 这条记录从哪来：<c>lan</c> = 对方在同一局域网直连登记（有 IP，删除时直连通知）；
    /// <c>server</c> = 服务端名单（跨网，删除要走服务端 /unbind）。
    /// 空 = 老存档，按 lan 处理。
    /// </summary>
    public string Via { get; set; } = "lan";

    /// <summary>对方的设备指纹（服务端登记才有，删除时按它定位）。</summary>
    public string SenderId { get; set; } = "";
}

/// <summary>
/// 「删了但还没通知到对方」的移除请求（2026-10-25）。
/// <para>
/// 接收端删除某条登记时，要通知对方把「已保存的电脑」里那条也删掉；
/// 对方不在线就先把请求存下来，等它下次广播心跳（同 IP）时补发。
/// ⚠️ 只在对方与删除时同一 IP 才补发得到；IP 变了就发不出去，请求会自然过期作废。
/// </para>
/// </summary>
public sealed class PendingUnbind
{
    public string Ip { get; set; } = "";
    public string Code { get; set; } = "";      // 本机配对码（对方据此删掉对应的已保存项）
    public long CreatedTicks { get; set; }
}
