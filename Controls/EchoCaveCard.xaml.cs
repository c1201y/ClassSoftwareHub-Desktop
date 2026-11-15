using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ClassSoftwareHub.Desktop.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.System;

namespace ClassSoftwareHub.Desktop.Controls;

























public sealed partial class EchoCaveCard : UserControl
{
    
    private const int ClearDelayMs = 150;

    
    private const int CharDelayMs = 40;

    
    private const int BlinkEvery = 10;

    
    
    
    
    private const string Placeholder = "点击此处可以查看 ClassSoftwareHub 群友逆天发言";

    private IReadOnlyList<EchoMessage> _messages = Array.Empty<EchoMessage>();

    
    private readonly List<EchoMessage> _queue = new();

    
    private int _typeGeneration;

    private bool _isTyping;
    private bool _busy;
    private bool _loaded;
    private CancellationTokenSource? _cts;

    public EchoCaveCard()
    {
        InitializeComponent();

        MessageText.Text = Placeholder;   

        
        
        
        
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _loaded = true;
        await ReloadAsync(force: false);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _loaded = false;

        
        _typeGeneration++;
        _isTyping = false;

        SetLoading(false);                 
        try { _cts?.Cancel(); } catch {  }
        _cts = null;
    }

    

    private async Task ReloadAsync(bool force)
    {
        if (_busy) return;
        _busy = true;
        SetLoading(true);

        try
        {
            try { _cts?.Cancel(); } catch {  }
            _cts = new CancellationTokenSource();
            var result = await EchoCaveService.LoadAsync(force, _cts.Token);
            if (!_loaded) return;

            _messages = result.Messages;
            _queue.Clear();                 

            if (_messages.Count > 0)
            {
                var first = TakeNext();
                if (first is not null)
                    MessageText.Text = first.Text;   
            }
            else
            {
                _typeGeneration++;                
                _isTyping = false;

                
                
                
                MessageText.Text = Placeholder;
            }
        }
        catch (OperationCanceledException)
        {
            
        }
        catch (Exception ex)
        {
            
            Core.AppLog.Info("echo-cave", "读取失败: " + ex.Message);
        }
        finally
        {
            _busy = false;
            SetLoading(false);
        }
    }

    
    
    
    
    private void SetLoading(bool loading)
    {
        LoadingRing.IsActive = loading;
        LoadingRing.Visibility = loading ? Visibility.Visible : Visibility.Collapsed;
    }

    

    
    private EchoMessage? TakeNext()
    {
        if (_messages.Count == 0) return null;

        if (_queue.Count == 0) Reshuffle();
        if (_queue.Count == 0) return null;

        var message = _queue[0];
        _queue.RemoveAt(0);
        return message;
    }

    
    
    
    
    
    private void Reshuffle()
    {
        _queue.Clear();
        _queue.AddRange(_messages);

        for (var i = _queue.Count - 1; i > 0; i--)
        {
            var j = Random.Shared.Next(i + 1);
            (_queue[i], _queue[j]) = (_queue[j], _queue[i]);
        }
    }

    

    
    
    
    
    
    public async void ShowNext()
    {
        
        if (_isTyping || _messages.Count == 0) return;

        var message = TakeNext();
        if (message is null) return;

        await TypeAsync(message.Text);
    }

    
    
    
    private async Task TypeAsync(string text)
    {
        var generation = ++_typeGeneration;
        _isTyping = true;

        try
        {
            MessageText.Text = "";
            await Task.Delay(ClearDelayMs);
            if (generation != _typeGeneration) return;

            for (var i = 0; i < text.Length; i++)
            {
                
                var caret = (i / BlinkEvery) % 2 == 0 ? "_" : "";
                MessageText.Text = text[..i] + caret;

                await Task.Delay(CharDelayMs);
                if (generation != _typeGeneration) return;
            }

            MessageText.Text = text;        
        }
        finally
        {
            if (generation == _typeGeneration) _isTyping = false;
        }
    }

    

    
    
    
    
    
    
    public async Task<bool> OpenSubmitPage()
    {
        try
        {
            return await Launcher.LaunchUriAsync(new Uri(EchoCaveService.SubmitUrl));
        }
        catch (Exception ex)
        {
            Core.AppLog.Info("echo-cave", "打开投稿页失败: " + ex.Message);
            return false;
        }
    }
}
