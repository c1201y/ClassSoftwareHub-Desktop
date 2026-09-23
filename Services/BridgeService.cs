using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.UI.Xaml.Controls;

namespace ClassSoftwareHub.Desktop.Services;

/// <summary>
/// 网页 &lt;-&gt; 原生 桥接。
/// 网页用 window.chrome.webview.postMessage(JSON) 发指令；原生用 PostWebMessageAsJson 回事件。
/// 所有消息都是 {"type": "...", ...} 形状。
/// </summary>
public sealed class BridgeService
{
    private readonly MainWindow _window;
    private readonly SettingsStore _settings;
    private WebView2? _web;

    public BridgeService(MainWindow window, SettingsStore settings)
    {
        _window = window;
        _settings = settings;
    }

    public void Attach(WebView2 web) => _web = web;

    // ---------- 原生 -> 网页 ----------

    public void Send(string type, object? payload = null)
    {
        var core = _web?.CoreWebView2;
        if (core is null) return;

        JsonObject node;
        try
        {
            node = JsonSerializer.SerializeToNode(payload) as JsonObject ?? new JsonObject();
            node["type"] = type;
        }
        catch
        {
            node = new JsonObject { ["type"] = type };
        }

        try { core.PostWebMessageAsJson(node.ToJsonString()); } catch { /* 网页还没就绪 */ }
    }

    // ---------- 网页 -> 原生 ----------

    public void HandleWebMessage(string json)
    {
        JsonNode? node;
        try { node = JsonNode.Parse(json); } catch { return; }
        if (node is not JsonObject obj) return;

        var type = Str(obj["type"]);
        switch (type)
        {
            case "shell.ready":
            case "shell.getInfo":
                Send("shell.info", _window.BuildInfoPayload());
                break;

            case "shell.getState":
                Send("shell.state", _window.BuildStatePayload());
                break;

            // ---- 标题栏 / 拖动 ----
            case "shell.titlebar":
                _window.ApplyTitleBarRegions(obj);
                break;

            case "shell.titlebarUpdate":
                // 网页要求「重新计算」——它自己会再发一条 shell.titlebar 回来
                break;

            case "shell.beginDrag":
            case "start-window-drag":
                _window.BeginWindowDrag();
                break;

            case "shell.toggleMaximize":
            case "toggle-window-maximize":
                _window.ToggleWindowMaximize();
                break;

            // ---- 外观 ----
            case "shell.setBackdrop":
                _window.SetBackdrop(Str(obj["value"], "acrylic"));
                break;

            case "shell.setTheme":
                _window.SetTheme(Str(obj["value"], "system"));
                break;

            case "shell.webTheme":
                _window.OnWebThemeReported(Str(obj["value"]));
                break;

            case "shell.setAlwaysOnTop":
                _window.SetAlwaysOnTop(Bool(obj["value"]));
                break;

            case "shell.setWebTransparent":
                _window.SetWebTransparent(Bool(obj["value"], true));
                break;

            case "shell.setTitleBarColor":
                _window.SetTitleBarColor(Str(obj["value"]));
                break;

            // ---- 窗口 / 系统 ----
            case "shell.window":
                _window.HandleWindowCommand(Str(obj["action"]));
                break;

            case "shell.openExternal":
                _window.OpenExternal(Str(obj["url"]));
                break;

            case "shell.setTelemetry":
                _window.SetTelemetryEnabled(Bool(obj["value"]));
                break;

            case "shell.setAutoStart":
                _window.SetAutoStart(Bool(obj["value"]));
                break;

            case "shell.setZoom":
                _window.SetWebZoom(Double(obj["value"], 1.0));
                break;

            case "shell.log":
                System.Diagnostics.Debug.WriteLine("[web] " + Str(obj["message"]));
                break;

            default:
                System.Diagnostics.Debug.WriteLine("[bridge] unknown message: " + type);
                break;
        }
    }

    // ---------- 小工具 ----------

    private static string Str(JsonNode? n, string dflt = "")
    {
        try { return n is null ? dflt : (n.GetValue<string>() ?? dflt); }
        catch { return dflt; }
    }

    private static bool Bool(JsonNode? n, bool dflt = false)
    {
        try { return n is null ? dflt : n.GetValue<bool>(); }
        catch { return dflt; }
    }

    private static double Double(JsonNode? n, double dflt)
    {
        try { return n is null ? dflt : n.GetValue<double>(); }
        catch { return dflt; }
    }
}
