using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ClassSoftwareHub.Desktop.Services;












public static class SubmitEndpoint
{
    
    
    
    
    
    
    
    
    public static string[] All => Core.ServiceEndpoints.All;

    
    
    
    
    public const int TimeoutMs = 10000;

    private static string FilePath => Path.Combine(Core.AppPaths.DataDir, "submit-endpoint.txt");

    
    public static List<string> Ordered()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var remembered = File.ReadAllText(FilePath).Trim();
                if (All.Contains(remembered))
                    return new List<string> { remembered }
                        .Concat(All.Where(item => item != remembered)).ToList();
            }
        }
        catch {  }
        return All.ToList();
    }

    
    public static void Remember(string baseUrl)
    {
        try
        {
            Directory.CreateDirectory(Core.AppPaths.DataDir);
            File.WriteAllText(FilePath, baseUrl);
        }
        catch {  }
    }
}
