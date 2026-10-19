using System.Text.Json;
using NRSVaultMini.Core;

namespace NRSVaultMini;

public sealed class AppSettings
{
    public string RootFolder { get; set; } = "";
    public string XmlPath { get; set; } = "";
    public bool TrialRun { get; set; } = true;
    public bool Rebuild { get; set; } = true;
    public bool MoveUnknown { get; set; } = true;
    public bool MoveDuplicates { get; set; } = true;
    public bool RemoveEmptyFolders { get; set; } = true;
    public bool BackupEnabled { get; set; }
    public string BackupFolder { get; set; } = "";
    public bool DatIncludeUnverified { get; set; }
    public List<string> SourceFolders { get; set; } = new();
    public bool UseSources { get; set; } = true;
    public bool SourcesOnlyNeeded { get; set; } = true;
    public string DatName { get; set; } = new DatOptions().Name;
    public string DatAuthor { get; set; } = new DatOptions().Author;
    public string LastDatFolder { get; set; } = "";
    public double WindowWidth { get; set; } = 1500;
    public double WindowHeight { get; set; } = 900;

    private static string FilePath => Path.Combine(Scanner.CacheDir, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch { /* fall back to defaults */ }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Scanner.CacheDir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* settings are a convenience */ }
    }
}
