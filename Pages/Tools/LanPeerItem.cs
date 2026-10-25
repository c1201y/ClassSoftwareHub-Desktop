using System.ComponentModel;
using ClassSoftwareHub.Desktop.Services;

namespace ClassSoftwareHub.Desktop.Pages.Tools;

/// <summary>
/// 发送页「局域网里的电脑」列表的一行（2026-10-25）。
/// 勾选状态只属于界面，不落盘 —— 每次进来重新发现、重新勾（名单会随网段变化，存下来反而误导）。
/// </summary>
public sealed class LanPeerItem : INotifyPropertyChanged
{
    private bool _isSelected;

    public LanPeerItem(LanPeer peer) => Peer = peer;

    public LanPeer Peer { get; }

    /// <summary>主行：本机标识（高28班），没设就显示机器名；自己那台标「（本机）」，免得误认。</summary>
    public string Display => Peer.IsSelf ? $"{Peer.Display}（本机）" : Peer.Display;

    /// <summary>副行：机器名 + 配对码。</summary>
    public string Detail => Peer.Detail;

    public string PairCode => Peer.PairCode;

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
