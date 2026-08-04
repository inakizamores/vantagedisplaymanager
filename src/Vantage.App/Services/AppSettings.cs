using System.IO;
using System.Text.Json;

namespace Vantage.App.Services;

/// <summary>
/// Small app-level settings, stored beside the profile store
/// (%LOCALAPPDATA%\Vantage\settings.json). Versioned like everything else (BLUEPRINT P8).
/// </summary>
public sealed class AppSettings
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Vantage", "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public int SchemaVersion { get; set; } = 1;

    /// <summary>Seconds before an unconfirmed display change is automatically reverted.</summary>
    public int RevertCountdownSeconds { get; set; } = 15;

    public static AppSettings Load()
    {
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
