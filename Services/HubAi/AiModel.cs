using System;
using System.Collections.Generic;
using System.Linq;
using ClassSoftwareHub.Desktop.Core;

namespace ClassSoftwareHub.Desktop.Services.HubAi;













public sealed class AiModelEntry
{
    
    public string Id { get; set; } = "";

    
    public string Name { get; set; } = "";

    public string ProviderId { get; set; } = "deepseek";

    public string BaseUrl { get; set; } = "";

    public string Model { get; set; } = "";

    public bool Thinking { get; set; } = true;

    
    public string Effort { get; set; } = "high";

    
    
    
    
    
    public int ContextLimit { get; set; }

    
    public string SecretId { get; set; } = "";

    public string EffectiveSecretId => string.IsNullOrEmpty(SecretId) ? Id : SecretId;

    
    public string Subtitle => $"{AiProviders.ById(ProviderId).Name} · {AiProviders.ModelLabel(ProviderId, Model)}";

    public AiConfig ToConfig(string apiKey) => new(
        ProviderId, BaseUrl, Model, apiKey,
        Thinking: Thinking,
        Effort: Effort,
        ContextLimit: ContextLimit);
}










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

    
    
    
    
    public static void EnsureMigrated()
    {
        var settings = App.Settings.Current;

        if (settings.HubAiModels.Count > 0)
        {
            
            if (string.IsNullOrEmpty(settings.HubAiActiveModelId)
                || settings.HubAiModels.All(m => m.Id != settings.HubAiActiveModelId))
            {
                settings.HubAiActiveModelId = settings.HubAiModels[0].Id;
                App.Settings.Save();
            }

            FixLegacyIds(settings);
            return;
        }

        
        if (string.IsNullOrWhiteSpace(settings.HubAiBaseUrl)) return;

        
        
        
        
        
        if (RequiresApiKey(settings.HubAiProvider) && !SecretStore.Has("default")) return;

        var provider = AiProviders.ById(settings.HubAiProvider);

        
        
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
            SecretId = "default",   
        };

        settings.HubAiModels.Add(entry);
        settings.HubAiActiveModelId = entry.Id;
        App.Settings.Save();

        AppLog.Step("hubai", $"已把原有模型配置迁移为列表第一项：{entry.Name}");
    }

    
    
    
    
    
    
    
    private static bool RequiresApiKey(string? providerId)
        => providerId is not ("local" or AiProviders.CustomId);

    
    
    
    
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

    
    public static void Remove(string id)
    {
        var entry = List.FirstOrDefault(m => m.Id == id);
        if (entry is null) return;

        SecretStore.Clear(entry.EffectiveSecretId);
        App.Settings.Current.HubAiModels.Remove(entry);

        
        if (App.Settings.Current.HubAiActiveModelId == id)
        {
            var next = App.Settings.Current.HubAiModels.FirstOrDefault();
            App.Settings.Current.HubAiActiveModelId = next?.Id;
            if (next is not null) ApplyToLegacyFields(next);
        }

        
        
        
        
        
        if (App.Settings.Current.HubAiModels.Count == 0)
        {
            App.Settings.Current.HubAiActiveModelId = null;
            App.Settings.Current.HubAiBaseUrl = "";
            App.Settings.Current.HubAiModel = "";
        }

        App.Settings.Save();
        AppLog.Step("hubai", $"删除模型：{entry.Name}");
    }

    
    public static void Activate(string id)
    {
        var entry = List.FirstOrDefault(m => m.Id == id);
        if (entry is null) return;

        App.Settings.Current.HubAiActiveModelId = id;
        ApplyToLegacyFields(entry);
        App.Settings.Save();
    }

    
    public static void ApplyToLegacyFields(AiModelEntry entry)
    {
        var settings = App.Settings.Current;
        settings.HubAiProvider = entry.ProviderId;
        settings.HubAiBaseUrl = entry.BaseUrl;
        settings.HubAiModel = entry.Model;
        settings.HubAiThinking = entry.Thinking;
        settings.HubAiEffort = entry.Effort;
    }

    
    public static void SaveEntry(AiModelEntry entry)
    {
        if (entry.Id.Length == 0) entry.Id = Guid.NewGuid().ToString("N");

        if (List.All(m => m.Id != entry.Id)) List.Add(entry);

        if (App.Settings.Current.HubAiActiveModelId == entry.Id)
            ApplyToLegacyFields(entry);

        App.Settings.Save();
    }
}
