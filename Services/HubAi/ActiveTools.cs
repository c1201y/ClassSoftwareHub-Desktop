using System.Collections.Generic;
using ClassSoftwareHub.Desktop.Data;

namespace ClassSoftwareHub.Desktop.Services.HubAi;















public static class ActiveTools
{
    
    public static IReadOnlyList<ToolDefinition> Load()
    {
        var tools = new List<ToolDefinition>();

        if (App.Settings.Current.HubAiToolsEnabled)
        {
            var pack = ToolPackStore.Load();
            if (pack?.Tools is { Count: > 0 } packTools) tools.AddRange(packTools);

            
            tools.AddRange(BuiltinTools.Docs);

            
            
            
            
            tools.Add(AiActionCatalog.ToolDefinition);
        }

        if (App.Settings.Current.HubAiWebEnabled) tools.AddRange(BuiltinTools.Web);

        return tools;
    }
}
