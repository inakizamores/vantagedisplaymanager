using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace Vantage.App.Services;

/// <summary>Where a profile shortcut can be placed.</summary>
public enum ShortcutLocation
{
    StartMenu,
    Desktop,
}

/// <summary>
/// Writes the <c>.lnk</c> files that turn a profile into something you can launch from the
/// Start menu — the DisplayMagician workflow, minus its desktop clutter. Each shortcut runs
/// <c>Vantage.exe --apply &lt;profile id&gt;</c>, which either hands the request to the
/// already-running tray instance over the IPC channel or applies it head-on
/// (see <see cref="InstanceChannel"/> and the cold path in <c>App.OnStartup</c>).
///
/// Everything is per-user (<c>%AppData%</c> / the user's Desktop) so no shortcut ever needs
/// administrator rights, matching the rest of the app.
/// </summary>
public static class ShortcutService
{
    /// <summary>Optional sub-folder for people who would rather keep their presets together.</summary>
    public const string StartMenuFolderName = "Vantage Presets";

    /// <summary>
    /// The Start menu's own root. Shortcuts written here land directly in the All apps list,
    /// alphabetically among everything else — no group to expand first. A sub-folder shows up
    /// as a collapsed heading instead, which is a click in the way every single time.
    /// </summary>
    public static string StartMenuFolder => Environment.GetFolderPath(Environment.SpecialFolder.Programs);

    public static string StartMenuGroupFolder => Path.Combine(StartMenuFolder, StartMenuFolderName);

    public static string DesktopFolder => Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);

    public static string FolderFor(ShortcutLocation location, bool grouped = false) => location switch
    {
        ShortcutLocation.Desktop => DesktopFolder,
        _ when grouped => StartMenuGroupFolder,
        _ => StartMenuFolder,
    };

    /// <summary>True when the path sits in either Start menu location — root or group folder.</summary>
    public static bool IsInStartMenu(string path)
    {
        var folder = Path.GetDirectoryName(path);
        return string.Equals(folder, StartMenuFolder, StringComparison.OrdinalIgnoreCase)
            || string.Equals(folder, StartMenuGroupFolder, StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsGrouped(string path) => string.Equals(
        Path.GetDirectoryName(path), StartMenuGroupFolder, StringComparison.OrdinalIgnoreCase);

    /// <summary>Full path of the .lnk a given name would produce in a given location.</summary>
    public static string PathFor(ShortcutLocation location, string name, bool grouped = false) =>
        Path.Combine(FolderFor(location, grouped), SanitizeFileName(name) + ".lnk");

    /// <summary>
    /// Creates (or replaces) the shortcut and returns its path. <paramref name="iconPath"/>
    /// may be a .ico, or an .exe/.dll to borrow the first icon from; null keeps the app's own.
    /// </summary>
    public static string Create(
        ShortcutLocation location,
        string name,
        string arguments,
        string description,
        string? iconPath,
        bool grouped = false)
    {
        var folder = FolderFor(location, grouped);
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, SanitizeFileName(name) + ".lnk");

        var exe = Environment.ProcessPath
            ?? throw new InvalidOperationException("Could not determine the path of the running executable.");

        var link = (IShellLinkW)new ShellLink();
        try
        {
            link.SetPath(exe);
            link.SetArguments(arguments);
            link.SetDescription(Truncate(description, 259));
            link.SetWorkingDirectory(Path.GetDirectoryName(exe) ?? string.Empty);
            link.SetIconLocation(iconPath is { Length: > 0 } ? iconPath : exe, 0);
            ((IPersistFile)link).Save(path, fRemember: true);
        }
        finally
        {
            Marshal.FinalReleaseComObject(link);
        }

        return path;
    }

    /// <summary>The executable an existing .lnk points at, or null if it cannot be read.</summary>
    public static string? TargetOf(string path)
    {
        var link = (IShellLinkW)new ShellLink();
        try
        {
            ((IPersistFile)link).Load(path, StgmRead);
            var buffer = new StringBuilder(1024);
            link.GetPath(buffer, buffer.Capacity, IntPtr.Zero, 0);
            return buffer.Length > 0 ? buffer.ToString() : null;
        }
        catch (Exception ex) when (ex is COMException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
        finally
        {
            Marshal.FinalReleaseComObject(link);
        }
    }

    /// <summary>
    /// Re-points an existing shortcut at the executable running now, in place. Everything else
    /// about the file — its name, its icon, and any Start pin the user made against it — is
    /// left alone, which is the whole reason this exists instead of delete-then-recreate.
    /// </summary>
    public static bool Repair(string path, string arguments, string description)
    {
        if (Environment.ProcessPath is not { } exe)
            return false;

        var link = (IShellLinkW)new ShellLink();
        try
        {
            var file = (IPersistFile)link;
            file.Load(path, StgmReadWrite);
            link.SetPath(exe);
            link.SetArguments(arguments);
            link.SetDescription(Truncate(description, 259));
            link.SetWorkingDirectory(Path.GetDirectoryName(exe) ?? string.Empty);
            file.Save(null!, fRemember: true);   // null = save back over the file it came from
            return true;
        }
        catch (Exception ex) when (ex is COMException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
        finally
        {
            Marshal.FinalReleaseComObject(link);
        }
    }

    private const int StgmRead = 0x00000000;
    private const int StgmReadWrite = 0x00000002;

    /// <summary>Deletes a shortcut, and the preset folder with it once the last one is gone.</summary>
    public static void Delete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);

            // Leaving an empty "Vantage Presets" heading behind in All apps looks broken.
            if (IsGrouped(path) && Directory.Exists(StartMenuGroupFolder) &&
                !Directory.EnumerateFileSystemEntries(StartMenuGroupFolder).Any())
            {
                Directory.Delete(StartMenuGroupFolder);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A shortcut the user already removed, or one Explorer has open, is not an error.
        }
    }

    /// <summary>Windows rejects these outright, and a couple more read badly in the Start menu.</summary>
    public static string SanitizeFileName(string name)
    {
        var cleaned = new StringBuilder(name.Length);
        foreach (var c in name.Trim())
            cleaned.Append(Path.GetInvalidFileNameChars().Contains(c) ? '-' : c);

        // Trailing dots and spaces are silently dropped by the filesystem — drop them here
        // instead, so the path we record matches the file that ends up on disk.
        var result = cleaned.ToString().TrimEnd('.', ' ');
        return result.Length == 0 ? "Preset" : result;
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max];

    [ComImport]
    [Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink
    {
    }

    /// <summary>
    /// Declared by hand rather than pulled in through the Windows Script Host type library,
    /// which would add a COM reference for one interface. Method order is the vtable order —
    /// do not reorder.
    /// </summary>
    [ComImport]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int cch, IntPtr findData, uint flags);
        void GetIDList(out IntPtr idList);
        void SetIDList(IntPtr idList);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder dir, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder args, int cch);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);
        void GetHotkey(out short hotkey);
        void SetHotkey(short hotkey);
        void GetShowCmd(out int showCmd);
        void SetShowCmd(int showCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder iconPath, int cch, out int iconIndex);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string iconPath, int iconIndex);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pathRel, uint reserved);
        void Resolve(IntPtr hwnd, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
    }
}
