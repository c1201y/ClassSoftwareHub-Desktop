using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClassSoftwareHub.Desktop.Core;

/// <summary>
/// 一页的「现场数据」，作为结构化 JSON 交给 Hub AI（2026-10-30，Nick 要求）。
///
/// <para>
/// 取代原来那一句话摘要（"在「AppData 管理」页，可清理 245 项…"）。Nick 的原话是
/// 「相关信息太少了，就像喂给 AI 的提示词那样……生成一份详细的 JSON 文件给 AI 读，
/// 让 AI 知道更详细的信息，这点信息够谁看」。
/// </para>
///
/// <para>
/// 几条约定：
/// <list type="bullet">
///   <item>数字用**原始值 + 可读文本**成对给（<c>bytes: 734003200</c> 和 <c>sizeText: "700 MB"</c>）——
///         模型算比例要原始值，说人话要用文本，只给一种它就会自己算错或写成 0.7 GB；</item>
///   <item>清单**有上限**（各页自己截到 25～40 条）并附「还有 N 项合计 X」——
///         全量几百条会把上下文顶爆，也会让每轮回复变慢；</item>
///   <item>只在发消息**那一刻**生成：界面上的筛选、勾选、扫描进度都会变，缓存下来的就是错的；</item>
///   <item>不写进用户消息：浮层只显示用户自己打的字，这份 JSON 走 system 通道（见
///         <c>AiChatService.ApplyPageContext</c>），历史里不会出现一坨机器文本。</item>
/// </list>
/// </para>
///
/// <para>
/// ⛔ 每个字段只放**事实**，不放下结论（"该清这个"属于模型该说的话）。与 <c>MachineSnapshot</c> 同一原则。
/// </para>
/// </summary>
public sealed class PageContextDoc
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <param name="page">页面名（界面上那个标题）</param>
    /// <param name="role">这一页的 AI 该扮演什么角色（一句话，写清楚"能拿什么当依据"）</param>
    public PageContextDoc(string page, string? role = null)
    {
        Page = page;
        Role = role;
    }

    public string Page { get; }

    public string? Role { get; }

    /// <summary>页面自己补的一句状态说明（"体积还在统计中"这类）。</summary>
    public string? Note { get; private set; }

    /// <summary>页面数据。键写中文，模型读起来更省事。</summary>
    public Dictionary<string, object?> Data { get; } = new();

    /// <summary>这一页现在能做哪些操作（让模型说"点全选再按清理"，而不是给个抽象建议）。</summary>
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
            // 序列化失败不该让提问发不出去：退回一句最简说明
            AppLog.Warn("hubai", "页面现场数据序列化失败：" + ex.Message);
            return $"{{\"page\":\"{Page}\",\"note\":\"现场数据序列化失败\"}}";
        }
    }
}
