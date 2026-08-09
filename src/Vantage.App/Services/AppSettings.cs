using System.IO;
using System.Text.Json;
using Vantage.Core.Services;

namespace Vantage.App.Services;

/// <summary>
/// Small app-level settings, stored beside the profile store in
/// Documents\Vantage Display Manager. Versioned like everything else (BLUEPRINT P8).
/// </summary>
public sealed class AppSettings
{
    private static string FilePath => VantageDataPaths.SettingsFile;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public int SchemaVersion { get; set; } = 1;

    /// <summary>Quiet update check against GitHub at every launch. On by default; the off
    /// switch exists for people who'd rather no app phones anywhere unprompted.</summary>
    public bool CheckForUpdatesAtStartup { get; set; } = true;

    /// <summary>Closing the window hides to the tray (default) instead of exiting.</summary>
    public bool CloseToTray { get; set; } = true;

    // Future app-level settings land here; unknown fields in older files are ignored on load.

    public static AppSettings Load()
    {
        VantageDataPaths.EnsureCreatedAndMigrated();
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), JsonOptions) ?? new AppSettings();
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            // Corrupt/locked settings are not fatal — fall back to defaults.
        }
        return new AppSettings();
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, JsonOptions));
        if (File.Exists(FilePath))
            File.Replace(tmp, FilePath, null);
        else
            File.Move(tmp, FilePath);
    }
}
