namespace Vantage.Interop;

/// <summary>Runtime gates for OS-version-dependent API paths (BLUEPRINT P4).</summary>
public static class WindowsVersion
{
    /// <summary>Windows 11 24H2 — DISPLAYCONFIG_SET_HDR_STATE / GET_ADVANCED_COLOR_INFO_2.</summary>
    public static bool IsWindows11_24H2OrGreater { get; } = Environment.OSVersion.Version.Build >= 26100;

    public static bool IsWindows11OrGreater { get; } = Environment.OSVersion.Version.Build >= 22000;
}
