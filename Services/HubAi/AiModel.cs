using System;
using System.Collections.Generic;
using System.Linq;
using ClassSoftwareHub.Desktop.Core;

namespace ClassSoftwareHub.Desktop.Services.HubAi;

/// <summary>
/// 一个**已配置的模型**（2026-10-18）。
///
/// <para>
/// 和 <see cref="AiProvider"/> 的区别：Provider 是"服务商预设"（代码里写死的那几家），
/// 这里记的是"用户真的配过一个"的实例 —— 同一个服务商可以配多条（比如两条 DeepSeek，一条快一条深思考）。
/// </para>
/// <para>
/// ⛔ 密钥**不在这个对象里**。它经 DPAPI 加密后单独存文件，用 <see cref="SecretId"/> 定位，
/// 这样删掉一条模型不会连带把别的模型的密钥弄坏。
/// </para>
/// </summary>
public sealed class AiModelEntry
{
    /// <summary>稳定 id（GUID）。⛔ 不要用模型名当 id —— 改名会丢密钥关联。</summary>
    public string Id { get; set; } = "";

    /// <summary>列表上显示的名字。默认「服务商 · 模型」，用户可改。</summary>
    public string Name { get; set; } = "";

    public string ProviderId { get; set; } = "deepseek";

    public string BaseUrl { get; set; } = "";

    public string Model { get; set; } = "";

    public bool Thinking { get; set; } = true;

    /// <summary>low / high / max。</summary>
    public string Effort { get; set; } = "high";

    /// <summary>
    /// 上下文上限（**近似字符数**，0 = 不限制）。
    /// ⚠️ 各家按 token 算，这里没有分词器，按 1 token ≈ 1.2 汉字粗估后取一个偏保守的值。
    /// 超了就丢最早的历史，保住 system prompt 与最近几轮。
    /// </summary>
    public int ContextLimit { get; set; }

    /// <summary>密钥文件后缀。默认等于 <see cref="Id"/>；迁移来的老配置用 "default"。</summary>
    public string SecretId { get; set; } = "";

    public string EffectiveSecretId => string.IsNullOrEmpty(SecretId) ? Id : SecretId;

    /// <summary>副标题：服务商名 · 模型的显示名。</summary>
    public string Subtitle => $"{AiProviders.ById(ProviderId).Name} · {AiProviders.ModelLabel(ProviderId, Model)}";

    public AiConfig ToConfig(string apiKey) => new(
        ProviderId, BaseUrl, Model, apiKey,
        Thinking: Thinking,
        Effort: Effort,
        ContextLimit: ContextLimit);
}

/// <summary>
/// 已配置模型的增删改与"当前使用哪一条"（2026-10-18）。
///
/// <para>
/// ⚠️ 旧的单模型字段（<c>HubAiProvider/HubAiBaseUrl/HubAiModel/...</c>）仍然保留，作为
/// **当前生效值的镜像**：切换模型时同步写回。这样那些直接读旧字段的代码（用量估算等）不用全改，
/// 也不会有"两个真相"。⛔ 以后新增功能请读这里的 <see cref="Active"/>，别再读旧字段。
/// </para>
/// </summary>
public static class AiModelStore
{
    private static List<AiModelEntry> List => App.Settings.Current.HubAiModels;

    public static IReadOnlyList<AiModelEntry> All => List;

    public static AiModelEntry? Active
    {
        get
        {
            EnsureMigrated();
            var id = App.Settings.Current.HubAiActiveModelId;
            return List.FirstOrDefault(m => m.Id == id) ?? List.FirstOrDefault();
        }
    }

    /// <summary>
    /// 把旧版"单一模型配置"转成列表里的第一条。只在从没迁移过时跑一次。
    /// ⚠️ 迁移**不复制密钥文件**，而是让它指向老的 "default" 密钥 —— 用户不用重填。
    /// </summary>
    public static void EnsureMigrated()
    {
        var settings = App.Settings.Current;

        if (settings.HubAiModels.Count > 0)
        {
            // 有列表但没选当前：补一个
            if (string.IsNullOrEmpty(settings.HubAiActiveModelId)
                || settings.HubAiModels.All(m => m.Id != settings.HubAiActiveModelId))
            {
                settings.HubAiActiveModelId = settings.HubAiModels[0].Id;
                App.Settings.Save();
            }

            FixLegacyIds(settings);
            return;
        }

        // 从来没配过（连地址都是空的）就不造条目
        if (string.IsNullOrWhiteSpace(settings.HubAiBaseUrl)) return;

        var provider = AiProviders.ById(settings.HubAiProvider);

        // ⚠️ 顺手修掉早期的错误模型 ID：`deepseek-v4-flash` 只是兼容别名，
        //    /v1/models 返回的正式 ID 是 `deepseek-flash`（对应 V4.1-Flash）。
        if (settings.HubAiProvider == "deepseek" && settings.HubAiModel == "deepseek-v4-flash")
        {
            settings.HubAiModel = "deepseek-flash";
            AppLog.Step("hubai", "已把模型 ID 从 deepseek-v4-flash 修正为 deepseek-flash");
        }

        var entry = new AiModelEntry
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = $"{provider.Name} · {AiProviders.ModelLabel(provider.Id, settings.HubAiModel)}",
            ProviderId = settings.HubAiProvider,
            BaseUrl = settings.HubAiBaseUrl,
            Model = settings.HubAiModel,
            Thinking = settings.HubAiThinking,
            Effort = settings.HubAiEffort,
            ContextLimit = 0,
            SecretId = "default",   // 老密钥文件就是 hubai-key.bin
        };

        settings.HubAiModels.Add(entry);
        settings.HubAiActiveModelId = entry.Id;
        App.Settings.Save();

        AppLog.Step("hubai", $"已把原有模型配置迁移为列表第一项：{entry.Name}");
    }

    /// <summary>
    /// 修正早期条目里的错误模型 ID 与名字。
    /// ⚠️ 只动"包含旧 ID"的条目 —— 用户自己改过的名字不覆盖。
    /// </summary>
    private static void FixLegacyIds(AppSettings settings)
    {
        var changed = false;

        foreach (var entry in settings.HubAiModels)
        {
            if (entry.ProviderId == "deepseek" && entry.Model == "deepseek-v4-flash")
            {
                entry.Model = "deepseek-flash";
                changed = true;
            }

            if (string.IsNullOrWhiteSpace(entry.Name) || entry.Name.Contains("deepseek-v4-flash"))
            {
                var provider = AiProviders.ById(entry.ProviderId);
                entry.Name = $"{provider.Name} · {AiProviders.ModelLabel(entry.ProviderId, entry.Model)}";
                changed = true;
            }
        }

        if (!changed) return;

        App.Settings.Save();
        AppLog.Step("hubai", "已修正历史模型条目的 ID 与显示名");
    }

    /// <summary>从服务商预设新增一条（同一模型重复添加会直接返回已有的那条）。</summary>
    public static AiModelEntry Add(AiProvider provider, string model)
    {
        EnsureMigrated();

        var existing = List.FirstOrDefault(m => m.ProviderId == provider.Id && m.Model == model);
        if (existing is not null) return existing;

        var entry = new AiModelEntry
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = $"{provider.Name} · {AiProviders.ModelLabel(provider.Id, model)}",
            ProviderId = provider.Id,
            BaseUrl = provider.BaseUrl,
            Model = model,
            Thinking = provider.ThinkingProfile.SupportsToggle,
            Effort = "high",
        };

        App.Settings.Current.HubAiModels.Add(entry);
        App.Settings.Save();

        AppLog.Step("hubai", $"新增模型：{entry.Name}");
        return entry;
    }

    /// <summary>删除一条，连带删掉它的密钥文件。</summary>
    public static void Remove(string id)
    {
        var entry = List.FirstOrDefault(m => m.Id == id);
        if (entry is null) return;

        SecretStore.Clear(entry.EffectiveSecretId);
        App.Settings.Current.HubAiModels.Remove(entry);

        // 删的正好是当前用的 → 自动切到第一条
        if (App.Settings.Current.HubAiActiveModelId == id)
        {
            var next = App.Settings.Current.HubAiModels.FirstOrDefault();
            App.Settings.Current.HubAiActiveModelId = next?.Id;
            if (next is not null) ApplyToLegacyFields(next);
        }

        App.Settings.Save();
        AppLog.Step("hubai", $"删除模型：{entry.Name}");
    }

    /// <summary>设为当前使用，并把值镜像到旧字段。</summary>
    public static void Activate(string id)
    {
        var entry = List.FirstOrDefault(m => m.Id == id);
        if (entry is null) return;

        App.Settings.Current.HubAiActiveModelId = id;
        ApplyToLegacyFields(entry);
        App.Settings.Save();
    }

    /// <summary>把一条模型的配置写回旧字段（兼容那些还在读 HubAiProvider/HubAiModel 的代码）。</summary>
    public static void ApplyToLegacyFields(AiModelEntry entry)
    {
        var settings = App.Settings.Current;
        settings.HubAiProvider = entry.ProviderId;
        settings.HubAiBaseUrl = entry.BaseUrl;
        settings.HubAiModel = entry.Model;
        settings.HubAiThinking = entry.Thinking;
        settings.HubAiEffort = entry.Effort;
    }

    /// <summary>保存对某一条的编辑（同时刷新旧字段与磁盘）。</summary>
    public static void SaveEntry(AiModelEntry entry)
    {
        if (entry.Id.Length == 0) entry.Id = Guid.NewGuid().ToString("N");

        if (List.All(m => m.Id != entry.Id)) List.Add(entry);

        if (App.Settings.Current.HubAiActiveModelId == entry.Id)
            ApplyToLegacyFields(entry);

        App.Settings.Save();
    }
}
