using System.ComponentModel;
using ClassSoftwareHub.Desktop.Services;

namespace ClassSoftwareHub.Desktop.Pages.Tools;





public sealed class LanPeerItem : INotifyPropertyChanged
{
    private bool _isSelected;

    public LanPeerItem(LanPeer peer) => Peer = peer;

    public LanPeer Peer { get; }

    
    public string Display => Peer.IsSelf ? $"{Peer.Display}（本机）" : Peer.Display;

    
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
