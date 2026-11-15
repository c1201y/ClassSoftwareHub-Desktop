using System;
using System.ComponentModel;
using ClassSoftwareHub.Desktop.Services;

namespace ClassSoftwareHub.Desktop.Pages.Tools;






public sealed class SavedPeerItem : INotifyPropertyChanged
{
    private bool _isSelected;

    public SavedPeerItem(AnnounceSavedPeer peer)
    {
        Code = peer.Code;
        Name = peer.Name;
    }

    public string Code { get; }

    
    public string Name { get; }

    public string Display => string.IsNullOrWhiteSpace(Name) ? Code : Name;

    public string Detail => string.IsNullOrWhiteSpace(Name) ? "仅配对码" : Code;

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


public sealed class BinderItem
{
    public BinderItem(AnnounceBinder binder)
    {
        Source = binder;
        Display = string.IsNullOrWhiteSpace(binder.Label) ? binder.Name : binder.Label;
        Detail = string.IsNullOrWhiteSpace(binder.Name) ? binder.Ip : $"{binder.Name} · {binder.Ip}";
        When = FormatTime(binder.SeenTicks);
    }

    public AnnounceBinder Source { get; }

    public string Display { get; }

    public string Detail { get; }

    public string When { get; }

    private static string FormatTime(long unixSeconds)
    {
        if (unixSeconds <= 0) return "";
        try
        {
            var t = DateTimeOffset.FromUnixTimeSeconds(unixSeconds).ToLocalTime();
            var span = DateTimeOffset.Now - t;
            if (span.TotalMinutes < 1) return "刚刚";
            if (span.TotalHours < 1) return $"{(int)span.TotalMinutes} 分钟前";
            if (span.TotalDays < 1) return $"{(int)span.TotalHours} 小时前";
            if (span.TotalDays < 30) return $"{(int)span.TotalDays} 天前";
            return t.ToString("yyyy-MM-dd");
        }
        catch
        {
            return "";
        }
    }
}
