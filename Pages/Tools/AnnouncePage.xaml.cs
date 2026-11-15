using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ClassSoftwareHub.Desktop.Core;
using ClassSoftwareHub.Desktop.Services;
using ClassSoftwareHub.Desktop.Views;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Pickers;

namespace ClassSoftwareHub.Desktop.Pages.Tools;













public sealed partial class AnnouncePage : Page
{
    
    private readonly DispatcherQueueTimer _statusTimer;

    
    private bool _loading = true;

    private bool _sending;

    public AnnouncePage()
    {
        InitializeComponent();

        var s = App.Settings.Current;

        EnableToggle.IsOn = s.AnnounceEnabled;
        QuietToggle.IsOn = s.AnnounceQuietMode;
        SpeakToggle.IsOn = s.AnnounceSpeakText;
        IntervalCombo.SelectedIndex = s.AnnounceIntervalSec switch { 15 => 0, 60 => 2, _ => 1 };
        DurationCombo.SelectedIndex = 1;   
        DefaultViewCombo.SelectedIndex = IsSendDefault(s.AnnounceDefaultView) ? 1 : 0;
        PairCodeText.Text = AnnounceService.MyPairCode;
        LabelBox.Text = s.AnnounceLabel;
        LanToggle.IsOn = s.AnnounceLanEnabled;
        AnimationCombo.SelectedIndex = (s.AnnounceAnimation ?? "typewriter").ToLowerInvariant() switch
        {
            "slide" => 1,
            "pop" => 2,
            "none" => 3,
            _ => 0,
        };

        _loading = false;

        SelectTab(IsSendDefault(s.AnnounceDefaultView) ? "send" : "receive");

        
        
        
        Loaded += (_, _) => SelectTab(IsSendDefault(App.Settings.Current.AnnounceDefaultView) ? "send" : "receive");

        RenderSaved();
        RenderBinders();
        SyncSoundName();
        _ = RefreshServerBindersAsync();   
        UpdateStatus();
        RefreshFirewall();                 
        RefreshPeerList();

        
        AnnounceLanService.PeersChanged += OnPeersChanged;
        AnnounceLanService.BindersChanged += OnBindersChanged;
        AnnounceLanService.SavedPeersChanged += OnSavedPeersChanged;

        _statusTimer = DispatcherQueue.CreateTimer();
        _statusTimer.Interval = TimeSpan.FromSeconds(1);
        _statusTimer.IsRepeating = true;
        _statusTimer.Tick += (_, _) => UpdateStatus();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _statusTimer.Start();
        UpdateStatus();
        RefreshPeerList();
        RefreshFirewall(force: true);      
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        _statusTimer.Stop();
        
        AnnounceLanService.PeersChanged -= OnPeersChanged;
        AnnounceLanService.BindersChanged -= OnBindersChanged;
        AnnounceLanService.SavedPeersChanged -= OnSavedPeersChanged;
    }

    private void OnPeersChanged() => DispatcherQueue.TryEnqueue(RefreshPeerList);

    private void OnBindersChanged() => DispatcherQueue.TryEnqueue(RenderBinders);

    
    private void OnSavedPeersChanged() => DispatcherQueue.TryEnqueue(() =>
    {
        RenderSaved();
        ShowResult(true, "对方已删除本机的登记，对应的「已保存的电脑」已移除。");
    });

    

    private static bool IsSendDefault(string? value) =>
        string.Equals(value, "send", StringComparison.OrdinalIgnoreCase);

    private void Tabs_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (_loading) return;
        ApplyTab();
    }

    private void ApplyTab() => ApplyTabByTag(Tabs.SelectedItem?.Tag as string ?? "receive");

    private void ApplyTabByTag(string tag)
    {
        var send = tag == "send";
        ReceiveView.Visibility = send ? Visibility.Collapsed : Visibility.Visible;
        SendView.Visibility = send ? Visibility.Visible : Visibility.Collapsed;
    }

    
    
    
    
    
    
    
    
    private void SelectTab(string tag)
    {
        foreach (var item in Tabs.Items)
        {
            if (item is SelectorBarItem bar && (bar.Tag as string) == tag)
            {
                Tabs.SelectedItem = bar;
                break;
            }
        }

        ApplyTabByTag(tag);
    }

    private void DefaultView_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        if (DefaultViewCombo.SelectedItem is not ComboBoxItem item || item.Tag is not string tag) return;

        App.Settings.Current.AnnounceDefaultView = tag;
        App.Settings.Save();
        SelectTab(tag);   
    }

    

    private void EnableToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        
        if (EnableToggle.IsOn == App.Settings.Current.AnnounceEnabled) return;

        App.Settings.Current.AnnounceEnabled = EnableToggle.IsOn;
        App.Settings.Save();

        if (EnableToggle.IsOn) AnnounceService.Start();
        else AnnounceService.Stop();

        UpdateStatus();
    }

    private void Label_Changed(object sender, TextChangedEventArgs e)
    {
        if (_loading) return;

        var value = (LabelBox.Text ?? "").Trim();
        if (value == App.Settings.Current.AnnounceLabel) return;

        App.Settings.Current.AnnounceLabel = value;
        App.Settings.Save();
        
    }

    private void Lan_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;

        
        
        
        
        if (LanToggle.IsOn == App.Settings.Current.AnnounceLanEnabled) return;

        App.Settings.Current.AnnounceLanEnabled = LanToggle.IsOn;
        App.Settings.Save();

        if (LanToggle.IsOn)
        {
            AnnounceLanService.Start();

            if (!AnnounceLanService.IsRunning && AnnounceLanService.LastError is { Length: > 0 } error)
                ShowResult(false, $"局域网通道启动失败：{error}");
        }
        else
        {
            AnnounceLanService.Stop();
        }

        
        
        RefreshFirewall(force: true);
        RefreshPeerList();
    }

    private void Interval_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        if (IntervalCombo.SelectedItem is not ComboBoxItem item || item.Tag is not string tag) return;
        if (!int.TryParse(tag, out var sec)) return;

        App.Settings.Current.AnnounceIntervalSec = sec;
        App.Settings.Save();
        AnnounceService.RefreshSettings();
    }

    private void Quiet_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        App.Settings.Current.AnnounceQuietMode = QuietToggle.IsOn;
        App.Settings.Save();
    }

    private void Speak_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        App.Settings.Current.AnnounceSpeakText = SpeakToggle.IsOn;
        App.Settings.Save();
    }

    private void CopyCode_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var package = new DataPackage();
            package.SetText(PairCodeText.Text);
            Clipboard.SetContent(package);
        }
        catch (Exception ex)
        {
            
            AppLog.Warn("announce", "复制配对码失败：" + ex.Message);
        }
    }

    
    private void UpdateStatus()
    {
        if (!EnableToggle.IsOn)
        {
            StatusText.Text = App.Settings.Current.AnnounceLanEnabled
                ? "服务器通道未开启；同网段的电脑仍可通过局域网直接发来。"
                : "未开启，打开后开始接收消息。";
            return;
        }

        if (!AnnounceService.IsRunning)
        {
            StatusText.Text = "正在启动……";
            return;
        }

        var back = AnnounceService.BackoffSec;
        if (back > 0)
        {
            StatusText.Text = $"连不上服务器，{back} 秒后自动重试。"
                + (App.Settings.Current.AnnounceLanEnabled ? "局域网直达不受影响。" : "");
            return;
        }

        StatusText.Text = AnnounceService.LastPollAt is null ? "已开启，正在连接服务器……" : "已连接，正在等待消息。";
    }

    

    
    private FirewallState _firewall = FirewallState.Unknown;

    
    
    
    
    
    
    
    
    private void RefreshFirewall(bool force = false)
    {
        if (!LanToggle.IsOn)
        {
            _firewall = FirewallState.Unknown;
            FirewallCard.Visibility = Visibility.Collapsed;
            return;
        }

        _firewall = FirewallGateway.Check(force);

        var need = _firewall is FirewallState.PortRulesMissing or FirewallState.ProgramBlocked;
        FirewallDesc.Text = FirewallGateway.Describe(_firewall);
        FirewallCard.Visibility = need ? Visibility.Visible : Visibility.Collapsed;
    }

    
    
    
    
    
    
    
    private async Task ApplyFirewallRepairAsync()
    {
        FirewallButton.IsEnabled = false;
        try
        {
            var state = await Task.Run(FirewallGateway.Repair);
            _firewall = state;

            switch (state)
            {
                case FirewallState.Ok:
                    ShowResult(true, "防火墙已修复：同网段的电脑现在可以发进来。");
                    break;
                case FirewallState.Unknown:
                    ShowResult(false, "没有拿到防火墙状态。若刚才取消了管理员确认，重新点「修复」即可。");
                    break;
                default:
                    ShowResult(false, "仍然被拦截：" + FirewallGateway.Describe(state));
                    break;
            }
        }
        finally
        {
            FirewallButton.IsEnabled = true;
            FirewallDesc.Text = FirewallGateway.Describe(_firewall);
            FirewallCard.Visibility = LanToggle.IsOn
                                      && _firewall is (FirewallState.PortRulesMissing or FirewallState.ProgramBlocked)
                ? Visibility.Visible
                : Visibility.Collapsed;
            RefreshPeerList();
        }
    }

    private async void Firewall_Click(object sender, RoutedEventArgs e) => await ApplyFirewallRepairAsync();

    private void RefreshPeers_Click(object sender, RoutedEventArgs e) => RefreshPeerList();

    
    
    
    private void RefreshPeerList()
    {
        if (!App.Settings.Current.AnnounceLanEnabled)
        {
            
            
            PeerHint.Text = "局域网通道未开启。请到「接收」页打开「局域网通道」，同一网段的电脑才会出现在这里。";
            PeerList.Visibility = Visibility.Collapsed;
            PeerList.ItemsSource = null;
            return;
        }

        var selected = (PeerList.ItemsSource as List<LanPeerItem>)
            ?.Where(p => p.IsSelected)
            .Select(p => p.PairCode)
            .ToHashSet() ?? new HashSet<string>();

        var items = AnnounceLanService.Peers
            .Select(p => new LanPeerItem(p) { IsSelected = selected.Contains(p.PairCode) })
            .ToList();

        PeerList.ItemsSource = items;

        if (items.Count > 0)
        {
            PeerHint.Text = $"已发现 {items.Count} 台。勾选要发送的电脑，也可以直接填配对码。";
            PeerList.Visibility = Visibility.Visible;
            return;
        }

        PeerList.Visibility = Visibility.Collapsed;
        PeerHint.Text = AnnounceLanService.IsRunning
            ? "当前网段内没有找到开启局域网通道的电脑；可以直接填配对码走服务器。"
            : "局域网通道未能启动"
              + (string.IsNullOrEmpty(AnnounceLanService.LastError) ? "" : $"（{AnnounceLanService.LastError}）")
              + "；可以改用配对码走服务器。";
    }

    

    private void RenderBinders()
    {
        var list = App.Settings.Current.AnnounceBinders
            .OrderByDescending(b => b.SeenTicks)
            .Select(b => new BinderItem(b))
            .ToList();

        BinderList.ItemsSource = list;
        BinderList.Visibility = list.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        BinderEmpty.Visibility = list.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ClearBindersButton.IsEnabled = list.Count > 0;
    }

    
    
    
    
    
    
    
    private async Task RefreshServerBindersAsync()
    {
        var server = await AnnounceService.ListBindersAsync();
        if (server is null) return;   

        var list = App.Settings.Current.AnnounceBinders;
        list.RemoveAll(b => string.Equals(b.Via, "server", StringComparison.Ordinal));

        foreach (var b in server)
        {
            
            list.RemoveAll(x => string.Equals(x.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            list.Add(new AnnounceBinder
            {
                Code = AnnounceService.MyPairCode,
                Name = b.Name,
                Label = b.Label,
                SeenTicks = b.Ts,
                Via = "server",
                SenderId = b.Id,
            });
        }

        while (list.Count > 50) list.RemoveAt(0);
        App.Settings.Save();
        RenderBinders();
    }

    private async void DeleteBinder_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string name }) return;

        var list = App.Settings.Current.AnnounceBinders;
        var target = list.FirstOrDefault(b => string.Equals(b.Name, name, StringComparison.OrdinalIgnoreCase));
        if (target is null) return;

        list.Remove(target);
        App.Settings.Save();
        RenderBinders();

        if (string.Equals(target.Via, "server", StringComparison.Ordinal))
        {
            var ok = await AnnounceService.UnbindAsync(target.SenderId);
            ShowResult(ok,
                ok ? "已从服务端移除该登记。" : "已从本机移除；服务端暂时不可用，对方仍会保留这个码。");
            return;
        }

        _ = NotifyUnbindAsync(new[] { target.Ip });
    }

    private async void ClearBinders_Click(object sender, RoutedEventArgs e)
    {
        var list = App.Settings.Current.AnnounceBinders;
        if (list.Count == 0) return;

        var serverCount = list.Count(b => string.Equals(b.Via, "server", StringComparison.Ordinal));
        var ips = list.Where(b => !string.Equals(b.Via, "server", StringComparison.Ordinal))
            .Select(b => b.Ip)
            .Where(ip => !string.IsNullOrWhiteSpace(ip))
            .Distinct()
            .ToList();

        list.Clear();
        App.Settings.Save();
        RenderBinders();

        if (serverCount > 0)
        {
            var ok = await AnnounceService.UnbindAsync(null);   
            if (!ok) ShowResult(false, "服务端暂时不可用：跨网络的登记没有清掉。");
        }

        if (ips.Count > 0) _ = NotifyUnbindAsync(ips);
    }

    
    
    
    
    private async Task NotifyUnbindAsync(IEnumerable<string> ips)
    {
        var code = AnnounceService.MyPairCode;
        var failed = 0;
        var total = 0;

        foreach (var ip in ips.Distinct())
        {
            total++;
            if (await AnnounceLanService.TryUnbindAsync(ip, code)) continue;

            QueuePendingUnbind(ip, code);
            failed++;
        }

        if (total == 0) return;

        ShowResult(true, failed == 0
            ? "已移除，并已通知对方删除对应记录。"
            : $"已从本机移除；{failed} 台当前不在线，等它们再上线时自动通知。");
    }

    private void QueuePendingUnbind(string ip, string code)
    {
        if (string.IsNullOrWhiteSpace(ip)) return;

        var pending = App.Settings.Current.AnnouncePendingUnbinds;
        if (pending.Any(p => p.Ip == ip && p.Code == code)) return;

        pending.Add(new PendingUnbind
        {
            Ip = ip,
            Code = code,
            CreatedTicks = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        });
        while (pending.Count > 50) pending.RemoveAt(0);
        App.Settings.Save();
    }

    

    private void RenderSaved()
    {
        var saved = App.Settings.Current.AnnounceSavedPeers;

        var selected = (SavedList.ItemsSource as List<SavedPeerItem>)
            ?.Where(p => p.IsSelected)
            .Select(p => p.Code)
            .ToHashSet() ?? new HashSet<string>();

        var items = saved
            .OrderByDescending(p => p.SavedTicks)
            .Select(p => new SavedPeerItem(p) { IsSelected = selected.Contains(p.Code) })
            .ToList();

        SavedList.ItemsSource = items;
        SavedList.Visibility = items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        SavedHint.Text = items.Count > 0 ? "勾选要发送的电脑" : "填好配对码后点「保存」，下次直接勾选";
    }

    private void SavePeer_Click(object sender, RoutedEventArgs e) => SavePeer(TargetCodeBox.Text, notify: true);

    
    
    
    
    private async void SavePeer(string? rawCode, bool notify)
    {
        var code = (rawCode ?? "").Trim();
        if (!AnnounceService.IsValidPairCode(code))
        {
            ShowResult(false, "配对码应为 6 位数字。");
            return;
        }

        var list = App.Settings.Current.AnnounceSavedPeers;
        if (list.Any(p => p.Code == code))
        {
            ShowResult(true, "这个配对码已经在「已保存的电脑」里。");
            return;
        }

        var peer = AnnounceLanService.Peers.FirstOrDefault(p => p.PairCode == code);
        list.Insert(0, new AnnounceSavedPeer
        {
            Code = code,
            Name = peer?.Display ?? "",
            SavedTicks = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        });
        App.Settings.Save();
        RenderSaved();

        if (notify)
        {
            var label = App.Settings.Current.AnnounceLabel ?? "";

            if (peer is not null)
            {
                await AnnounceLanService.TryBindAsync(peer, Environment.MachineName, label);
                ShowResult(true, $"已保存 {peer.Display}，并已通知对方登记。");
            }
            else
            {
                
                var ok = await AnnounceService.BindAsync(code, Environment.MachineName, label);
                ShowResult(true, ok
                    ? "已保存，并已在服务端登记 —— 对方跨网络也能看到。"
                    : "已保存。对方不在同一局域网，且服务端登记暂时不可用，对方看不到这条登记。");
            }
        }
        else
        {
            ShowResult(true, "已保存。");
        }
    }

    private void DeleteSaved_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string code }) return;

        App.Settings.Current.AnnounceSavedPeers.RemoveAll(p => p.Code == code);
        App.Settings.Save();
        RenderSaved();
    }

    

    private void Animation_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        if (AnimationCombo.SelectedItem is not ComboBoxItem item || item.Tag is not string tag) return;

        App.Settings.Current.AnnounceAnimation = tag;
        App.Settings.Save();
    }

    private void SyncSoundName() => SoundNameText.Text = AnnounceSound.DisplayName;

    private async void PickSound_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileOpenPicker
            {
                ViewMode = PickerViewMode.List,
                SuggestedStartLocation = PickerLocationId.MusicLibrary,
            };
            foreach (var ext in AnnounceSound.Extensions) picker.FileTypeFilter.Add(ext);

            
            WinRT.Interop.InitializeWithWindow.Initialize(
                picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow!));

            var file = await picker.PickSingleFileAsync();
            if (file is null) return;

            AnnounceSound.CustomPath = file.Path;
            SyncSoundName();
            AnnounceSound.Play();   
        }
        catch (Exception ex)
        {
            AppLog.Info("announce", "选择提示音失败：" + ex.Message);
        }
    }

    private void TestSound_Click(object sender, RoutedEventArgs e) => AnnounceSound.Play();

    private void ResetSound_Click(object sender, RoutedEventArgs e)
    {
        AnnounceSound.CustomPath = "";
        SyncSoundName();
        AnnounceSound.Play();
    }

    

    private void PreviewShort_Click(object sender, RoutedEventArgs e)
        => Preview("请李明同学到办公室拿东西", "教务处", 15);

    private void PreviewLong_Click(object sender, RoutedEventArgs e)
        => Preview("通知：明天上午第二节课后，请各班班长到教务处领取本学期期中考试的答题卡与草稿纸，"
                   + "并按班级人数核对清楚后签字确认；如有缺页或印刷不清请当场说明，逾期不再补发。",
                   "教务处", 20);

    private void PreviewNoFrom_Click(object sender, RoutedEventArgs e)
        => Preview("请把教室多媒体柜的钥匙交到门卫室", "", 20);

    private void Preview(string text, string from, int durationSec)
    {
        AppLog.Info("announce", "本机预览喊话窗（不联网）");
        AnnounceWindow.Preview(new AnnounceMessage
        {
            Id = "preview-" + Guid.NewGuid().ToString("N")[..6],
            Text = text,
            From = from,
            Duration = durationSec,
            Ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        }, quiet: QuietToggle.IsOn, speak: SpeakToggle.IsOn);
    }

    

    private void Content_Changed(object sender, TextChangedEventArgs e)
    {
        var len = ContentBox.Text?.Length ?? 0;
        CountText.Text = $"{len} / 100";
        SendButton.IsEnabled = len > 0 && len <= 100 && !_sending;
    }

    private async void Send_Click(object sender, RoutedEventArgs e)
    {
        if (_sending) return;

        var text = ContentBox.Text?.Trim() ?? "";
        if (text.Length == 0)
        {
            ShowResult(false, "请先输入要展示的内容。");
            return;
        }

        var manualCode = (TargetCodeBox.Text ?? "").Trim();
        if (manualCode.Length > 0 && !AnnounceService.IsValidPairCode(manualCode))
        {
            ShowResult(false, "配对码应为 6 位数字。");
            return;
        }

        
        var targets = new List<string>();
        targets.AddRange((PeerList.ItemsSource as List<LanPeerItem>)?.Where(p => p.IsSelected)
            .Select(p => p.PairCode) ?? Enumerable.Empty<string>());
        targets.AddRange((SavedList.ItemsSource as List<SavedPeerItem>)?.Where(p => p.IsSelected)
            .Select(p => p.Code) ?? Enumerable.Empty<string>());
        if (manualCode.Length > 0) targets.Add(manualCode);
        targets = targets.Distinct().ToList();

        if (targets.Count == 0)
        {
            ShowResult(false, "请勾选要发送的电脑，或填一个配对码。");
            return;
        }

        var duration = 30;
        if (DurationCombo.SelectedItem is ComboBoxItem item && item.Tag is string tag
            && int.TryParse(tag, out var d))
            duration = d;

        _sending = true;
        SendButton.IsEnabled = false;
        SendResult.IsOpen = false;

        var from = FromBox.Text ?? "";
        var msg = new AnnounceMessage
        {
            Id = Guid.NewGuid().ToString("N")[..12],   
            Text = text,
            From = from,
            Duration = duration,
            Ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };

        var lanOk = 0;
        var serverOk = 0;
        var failed = 0;
        var revoked = new List<string>();   

        foreach (var code in targets)
        {
            
            var peer = AnnounceLanService.Peers.FirstOrDefault(p => p.PairCode == code);
            if (peer is not null && await AnnounceLanService.TrySendAsync(peer, msg))
            {
                lanOk++;
                continue;
            }

            var r = await AnnounceService.SendAsync(text, from, duration, new[] { code });
            if (r.Ok) serverOk++;
            else if (string.Equals(r.Error, "revoked", StringComparison.OrdinalIgnoreCase)) revoked.Add(code);
            else failed++;
        }

        
        if (revoked.Count > 0)
        {
            App.Settings.Current.AnnounceSavedPeers.RemoveAll(p => revoked.Contains(p.Code));
            App.Settings.Save();
            RenderSaved();
        }

        _sending = false;
        SendButton.IsEnabled = (ContentBox.Text?.Length ?? 0) > 0;

        
        if (manualCode.Length > 0 && lanOk + serverOk > 0) SavePeer(manualCode, notify: false);

        var parts = new List<string>();
        if (lanOk > 0) parts.Add($"局域网直达 {lanOk} 台");
        if (serverOk > 0) parts.Add($"服务器中转 {serverOk} 台");
        if (failed > 0) parts.Add($"失败 {failed} 台");
        if (revoked.Count > 0) parts.Add($"{revoked.Count} 台已删除本机的登记（已从「已保存的电脑」移除）");

        if (failed == 0)
        {
            ContentBox.Text = "";   
            ShowResult(true, "已发送：" + string.Join("，", parts) + "。");
        }
        else
        {
            ShowResult(lanOk + serverOk > 0, "已发送：" + string.Join("，", parts) + "。");
        }
    }

    private void ShowResult(bool ok, string message)
    {
        SendResult.Severity = ok ? InfoBarSeverity.Success : InfoBarSeverity.Error;
        SendResult.Title = ok ? "已完成" : "未完成";
        SendResult.Message = message;
        SendResult.IsOpen = true;
    }
}
