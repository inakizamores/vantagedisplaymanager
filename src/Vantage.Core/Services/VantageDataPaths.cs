namespace Vantage.Core.Services;

/// <summary>
/// Central location for user data. Lives in Documents\Vantage Display Manager — like game
/// saves — so it survives uninstall/reinstall, is trivial to back up or copy to a new PC,
/// and rides along automatically when Documents is OneDrive-synced.
/// </summary>
public static class VantageDataPaths
{
    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        "Vantage Display Manager");

    public static string ProfilesFile => Path.Combine(Root, "profiles.json");
    public static string SettingsFile => Path.Combine(Root, "settings.json");

    /// <summary>Pre-0.1.3 location (%LOCALAPPDATA%\Vantage) — read-only migration source.</summary>
    private static string LegacyRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Vantage");

    private static readonly string[] MigratableFiles = ["profiles.json", "profiles.json.bak", "settings.json"];

    /// <summary>
    /// Creates the data folder and, on first run of 0.1.3+, copies any data found in the
    /// legacy location. Existing files in Documents are never overwritten.
    /// </summary>
    public static void EnsureCreatedAndMigrated()
    {
        Directory.CreateDirectory(Root);

        if (!Directory.Exists(LegacyRoot))
            return;

        foreach (var name in MigratableFiles)
        {
            var source = Path.Combine(LegacyRoot, name);
            var dest = Path.Combine(Root, name);
            try
            {
                if (File.Exists(source) && !File.Exists(dest))
                    File.Copy(source, dest);
            }
            catch (IOException)
            {
                // Best effort — a locked/unreadable legacy file must not block startup.
            }
        }
    }
}
