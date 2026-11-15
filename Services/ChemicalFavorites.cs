using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace ClassSoftwareHub.Desktop.Services;









public static class ChemicalFavorites
{
    public static readonly string[] DefaultSubstances =
    {
        "H2O", "CO2", "O2", "H2", "HCl", "H2SO4",
        "NaOH", "Ca(OH)2", "NaCl", "CaCO3", "CuSO4", "Fe2O3",
    };

    public static readonly string[] DefaultIons =
    {
        "H^+", "OH^-", "SO4^2-", "CO3^2-",
        "NO3^-", "NH4^+", "Cl^-", "Fe^3+",
    };

    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ClassSoftwareHub", "chemical-favorites.json");

    private sealed class Payload
    {
        public List<string> Substances { get; set; } = new();
        public List<string> Ions { get; set; } = new();
    }

    
    public static (List<string> Substances, List<string> Ions) Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                var payload = JsonSerializer.Deserialize<Payload>(json);
                if (payload is not null)
                {
                    var substances = Clean(payload.Substances);
                    var ions = Clean(payload.Ions);
                    if (substances.Count > 0 || ions.Count > 0)
                        return (substances, ions);
                }
            }
        }
        catch (Exception ex)
        {
            Core.AppLog.Exception("chem", ex, "读取常用物质清单");
        }

        return (DefaultSubstances.ToList(), DefaultIons.ToList());
    }

    public static void Save(IEnumerable<string> substances, IEnumerable<string> ions)
    {
        try
        {
            var payload = new Payload
            {
                Substances = Clean(substances),
                Ions = Clean(ions),
            };

            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(payload, new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            }));
        }
        catch (Exception ex)
        {
            Core.AppLog.Exception("chem", ex, "保存常用物质清单");
        }
    }

    
    private static List<string> Clean(IEnumerable<string>? items)
    {
        if (items is null) return new List<string>();

        return items
            .Select(i => (i ?? "").Trim())
            .Where(i => i.Length is > 0 and <= 40)
            .Distinct(StringComparer.Ordinal)
            .Take(40)
            .ToList();
    }
}
