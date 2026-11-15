using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClassSoftwareHub.Desktop.Core;



























public sealed class PageContextDoc
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    
    
    public PageContextDoc(string page, string? role = null)
    {
        Page = page;
        Role = role;
    }

    public string Page { get; }

    public string? Role { get; }

    
    public string? Note { get; private set; }

    
    public Dictionary<string, object?> Data { get; } = new();

    
    public List<object> Actions { get; } = new();

    public PageContextDoc Add(string key, object? value)
    {
        Data[key] = value;
        return this;
    }

    public PageContextDoc Say(string? note)
    {
        Note = note;
        return this;
    }

    public PageContextDoc Action(string name, string? detail = null, bool enabled = true)
    {
        Actions.Add(new { name, detail, enabled });
        return this;
    }

    public string ToJson()
    {
        var payload = new
        {
            generatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            page = Page,
            role = Role,
            note = Note,
            data = Data,
            actions = Actions.Count == 0 ? null : Actions,
        };

        try
        {
            return JsonSerializer.Serialize(payload, JsonOpts);
        }
        catch (Exception ex)
        {
            
            AppLog.Warn("hubai", "页面现场数据序列化失败：" + ex.Message);
            return $"{{\"page\":\"{Page}\",\"note\":\"现场数据序列化失败\"}}";
        }
    }
}
