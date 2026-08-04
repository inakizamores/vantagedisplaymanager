using System.Runtime.InteropServices;

namespace Vantage.Interop.Gdi;

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
public struct DEVMODE
{
    private const int CCHDEVICENAME = 32;
    private const int CCHFORMNAME = 32;

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CCHDEVICENAME)]
    public string dmDeviceName;
    public ushort dmSpecVersion;
    public ushort dmDriverVersion;
    public ushort dmSize;
    public ushort dmDriverExtra;
    public uint dmFields;
    public int dmPositionX;
    public int dmPositionY;
    public uint dmDisplayOrientation;
    public uint dmDisplayFixedOutput;
    public short dmColor;
    public short dmDuplex;
    public short dmYResolution;
    public short dmTTOption;
    public short dmCollate;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CCHFORMNAME)]
    public string dmFormName;
    public ushort dmLogPixels;
    public uint dmBitsPerPel;
    public uint dmPelsWidth;
    public uint dmPelsHeight;
    public uint dmDisplayFlags;
    public uint dmDisplayFrequency;
    public uint dmICMMethod;
    public uint dmICMIntent;
    public uint dmMediaType;
    public uint dmDitherType;
    public uint dmReserved1;
    public uint dmReserved2;
    public uint dmPanningWidth;
    public uint dmPanningHeight;

    public static DEVMODE Create() => new() { dmSize = (ushort)Marshal.SizeOf<DEVMODE>() };
}

[Flags]
public enum DmFields : uint
{
    Position = 0x20,
    DisplayOrientation = 0x80,
    BitsPerPel = 0x40000,
    PelsWidth = 0x80000,
    PelsHeight = 0x100000,
    DisplayFlags = 0x200000,
    DisplayFrequency = 0x400000,
    DisplayFixedOutput = 0x20000000,
}

[Flags]
public enum ChangeDisplaySettingsFlags : uint
{
    None = 0,
    UpdateRegistry = 0x1,
    Global = 0x8,
    SetPrimary = 0x10,
    NoReset = 0x10000000,
    Reset = 0x40000000,
}

public enum DispChangeResult
{
    Successful = 0,
    Restart = 1,
    Failed = -1,
    BadMode = -2,
    NotUpdated = -3,
    BadFlags = -4,
    BadParam = -5,
    BadDualView = -6,
}

/// <summary>
/// Legacy GDI display-settings API. Used only for mode enumeration and for
/// reconciling resolution/refresh/position when the topology already matches —
/// the CCD path stays authoritative for topology (BLUEPRINT P4).
/// </summary>
public static class GdiApi
{
    private const int ENUM_CURRENT_SETTINGS = -1;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern bool EnumDisplaySettingsExW(string? lpszDeviceName, int iModeNum, ref DEVMODE lpDevMode, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern DispChangeResult ChangeDisplaySettingsExW(
        string? lpszDeviceName, ref DEVMODE lpDevMode, IntPtr hwnd, ChangeDisplaySettingsFlags dwflags, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern DispChangeResult ChangeDisplaySettingsExW(
        string? lpszDeviceName, IntPtr lpDevMode, IntPtr hwnd, ChangeDisplaySettingsFlags dwflags, IntPtr lParam);

    public sealed record GdiMode(uint Width, uint Height, uint RefreshHz, uint BitsPerPel);

    public static DEVMODE? GetCurrentMode(string gdiDeviceName)
    {
        var dm = DEVMODE.Create();
        return EnumDisplaySettingsExW(gdiDeviceName, ENUM_CURRENT_SETTINGS, ref dm, 0) ? dm : null;
    }

    /// <summary>All modes the display+driver report for a GDI device (\\.\DISPLAY1), 32-bpp only, deduplicated.</summary>
    public static List<GdiMode> EnumerateModes(string gdiDeviceName)
    {
        var results = new HashSet<GdiMode>();
        var dm = DEVMODE.Create();
        for (var i = 0; EnumDisplaySettingsExW(gdiDeviceName, i, ref dm, 0); i++)
        {
            if (dm.dmBitsPerPel == 32)
                results.Add(new GdiMode(dm.dmPelsWidth, dm.dmPelsHeight, dm.dmDisplayFrequency, dm.dmBitsPerPel));
        }
        return results
            .OrderByDescending(m => m.Width)
            .ThenByDescending(m => m.Height)
            .ThenByDescending(m => m.RefreshHz)
            .ToList();
    }

    /// <summary>Stages a mode/position change for one display (CDS_NORESET) — call <see cref="CommitStaged"/> after.</summary>
    public static DispChangeResult StageModeChange(
        string gdiDeviceName, uint width, uint height, uint refreshHz, int posX, int posY, bool setPrimary)
    {
        var dm = DEVMODE.Create();
        if (!EnumDisplaySettingsExW(gdiDeviceName, ENUM_CURRENT_SETTINGS, ref dm, 0))
            return DispChangeResult.BadParam;

        dm.dmPelsWidth = width;
        dm.dmPelsHeight = height;
        dm.dmDisplayFrequency = refreshHz;
        dm.dmPositionX = posX;
        dm.dmPositionY = posY;
        dm.dmFields = (uint)(DmFields.PelsWidth | DmFields.PelsHeight | DmFields.DisplayFrequency | DmFields.Position);

        var flags = ChangeDisplaySettingsFlags.UpdateRegistry | ChangeDisplaySettingsFlags.NoReset;
        if (setPrimary)
            flags |= ChangeDisplaySettingsFlags.SetPrimary;

        return ChangeDisplaySettingsExW(gdiDeviceName, ref dm, IntPtr.Zero, flags, IntPtr.Zero);
    }

    /// <summary>Commits all staged changes in one desktop transition.</summary>
    public static DispChangeResult CommitStaged()
        => ChangeDisplaySettingsExW(null, IntPtr.Zero, IntPtr.Zero, ChangeDisplaySettingsFlags.None, IntPtr.Zero);
}
