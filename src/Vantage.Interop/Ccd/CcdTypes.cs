// Windows Connecting and Configuring Displays (CCD) API type definitions.
// Layouts mirror wingdi.h/winuser.h from Windows SDK 10.0.26100 exactly; the
// 24H2-only and undocumented members are marked and must stay behind runtime gates.
using System.Runtime.InteropServices;

namespace Vantage.Interop.Ccd;

[StructLayout(LayoutKind.Sequential)]
public struct LUID : IEquatable<LUID>
{
    public uint LowPart;
    public int HighPart;

    public readonly ulong ToUInt64() => ((ulong)(uint)HighPart << 32) | LowPart;
    public readonly bool Equals(LUID other) => LowPart == other.LowPart && HighPart == other.HighPart;
    public override readonly bool Equals(object? obj) => obj is LUID other && Equals(other);
    public override readonly int GetHashCode() => HashCode.Combine(LowPart, HighPart);
    public override readonly string ToString() => ToUInt64().ToString("X");
}

public enum DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY : uint
{
    Other = 0xFFFFFFFF,
    Hd15 = 0,
    SVideo = 1,
    CompositeVideo = 2,
    ComponentVideo = 3,
    Dvi = 4,
    Hdmi = 5,
    Lvds = 6,
    DJpn = 8,
    Sdi = 9,
    DisplayPortExternal = 10,
    DisplayPortEmbedded = 11,
    UdiExternal = 12,
    UdiEmbedded = 13,
    SdtvDongle = 14,
    Miracast = 15,
    IndirectWired = 16,
    IndirectVirtual = 17,
    DisplayPortUsbTunnel = 18,
    Internal = 0x80000000,
}

public enum DISPLAYCONFIG_ROTATION : uint
{
    Identity = 1,
    Rotate90 = 2,
    Rotate180 = 3,
    Rotate270 = 4,
}

public enum DISPLAYCONFIG_SCALING : uint
{
    Identity = 1,
    Centered = 2,
    Stretched = 3,
    AspectRatioCenteredMax = 4,
    Custom = 5,
    Preferred = 128,
}

public enum DISPLAYCONFIG_SCANLINE_ORDERING : uint
{
    Unspecified = 0,
    Progressive = 1,
    Interlaced = 2,
    InterlacedUpperFieldFirst = 2,
    InterlacedLowerFieldFirst = 3,
}

public enum DISPLAYCONFIG_PIXELFORMAT : uint
{
    Bpp8 = 1,
    Bpp16 = 2,
    Bpp24 = 3,
    Bpp32 = 4,
    NonGdi = 5,
}

public enum DISPLAYCONFIG_MODE_INFO_TYPE : uint
{
    Source = 1,
    Target = 2,
    DesktopImage = 3,
}

public enum DISPLAYCONFIG_TOPOLOGY_ID : uint
{
    Internal = 1,
    Clone = 2,
    Extend = 4,
    External = 8,
}

public enum DISPLAYCONFIG_COLOR_ENCODING : uint
{
    Rgb = 0,
    YCbCr444 = 1,
    YCbCr422 = 2,
    YCbCr420 = 3,
    Intensity = 4,
}

/// <summary>Windows 11 24H2+ (DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO_2.activeColorMode).</summary>
public enum DISPLAYCONFIG_ADVANCED_COLOR_MODE : uint
{
    Sdr = 0,
    Wcg = 1,
    Hdr = 2,
}

public enum DISPLAYCONFIG_DEVICE_INFO_TYPE : uint
{
    GetSourceName = 1,
    GetTargetName = 2,
    GetTargetPreferredMode = 3,
    GetAdapterName = 4,
    SetTargetPersistence = 5,
    GetTargetBaseType = 6,
    GetSupportVirtualResolution = 7,
    SetSupportVirtualResolution = 8,
    GetAdvancedColorInfo = 9,
    SetAdvancedColorState = 10,
    GetSdrWhiteLevel = 11,
    GetMonitorSpecialization = 12,
    SetMonitorSpecialization = 13,
    // Windows 11 24H2+
    GetAdvancedColorInfo2 = 15,
    SetHdrState = 16,
    SetWcgState = 17,
    // Undocumented. Used by the Settings app; verified by SetDPI/lihas sample and DisplayMagician.
    GetSourceDpiScale = unchecked((uint)-3),
    SetSourceDpiScale = unchecked((uint)-4),
    // Undocumented. Sets SDR content brightness on HDR displays (ledoge/set_maxtml, twinkle-tray).
    SetSdrWhiteLevel = 0xFFFFFFEE,
}

[Flags]
public enum QueryDisplayFlags : uint
{
    AllPaths = 0x1,
    OnlyActivePaths = 0x2,
    DatabaseCurrent = 0x4,
    VirtualModeAware = 0x10,
    IncludeHmd = 0x20,
    VirtualRefreshRateAware = 0x40,
}

[Flags]
public enum SetDisplayConfigFlags : uint
{
    TopologyInternal = 0x1,
    TopologyClone = 0x2,
    TopologyExtend = 0x4,
    TopologyExternal = 0x8,
    TopologySupplied = 0x10,
    UseSuppliedDisplayConfig = 0x20,
    Validate = 0x40,
    Apply = 0x80,
    NoOptimization = 0x100,
    SaveToDatabase = 0x200,
    AllowChanges = 0x400,
    PathPersistIfRequired = 0x800,
    ForceModeEnumeration = 0x1000,
    AllowPathOrderChanges = 0x2000,
    VirtualModeAware = 0x8000,
    VirtualRefreshRateAware = 0x20000,
}

[StructLayout(LayoutKind.Sequential)]
public struct DISPLAYCONFIG_RATIONAL
{
    public uint Numerator;
    public uint Denominator;

    public readonly double ToDouble() => Denominator == 0 ? 0 : (double)Numerator / Denominator;
    public override readonly string ToString() => $"{Numerator}/{Denominator}";
}

[StructLayout(LayoutKind.Sequential)]
public struct DISPLAYCONFIG_2DREGION
{
    public uint Cx;
    public uint Cy;
}

[StructLayout(LayoutKind.Sequential)]
public struct POINTL
{
    public int X;
    public int Y;
}

[StructLayout(LayoutKind.Sequential)]
public struct RECTL
{
    public int Left;
    public int Top;
    public int Right;
    public int Bottom;
}

[StructLayout(LayoutKind.Sequential)]
public struct DISPLAYCONFIG_PATH_SOURCE_INFO
{
    public LUID AdapterId;
    public uint Id;
    /// <summary>Union: modeInfoIdx, or (cloneGroupId:16 | sourceModeInfoIdx:16) when QDC_VIRTUAL_MODE_AWARE.</summary>
    public uint ModeInfoIdx;
    public uint StatusFlags;

    public const uint PATH_MODE_IDX_INVALID = 0xFFFFFFFF;
    public const uint PATH_SOURCE_MODE_IDX_INVALID = 0xFFFF;
    public const uint PATH_CLONE_GROUP_INVALID = 0xFFFF;
    public const uint STATUS_IN_USE = 0x1;

    public ushort CloneGroupId
    {
        readonly get => (ushort)(ModeInfoIdx & 0xFFFF);
        set => ModeInfoIdx = (ModeInfoIdx & 0xFFFF0000) | value;
    }

    public ushort SourceModeInfoIdx
    {
        readonly get => (ushort)(ModeInfoIdx >> 16);
        set => ModeInfoIdx = (ModeInfoIdx & 0x0000FFFF) | ((uint)value << 16);
    }
}

[StructLayout(LayoutKind.Sequential)]
public struct DISPLAYCONFIG_PATH_TARGET_INFO
{
    public LUID AdapterId;
    public uint Id;
    /// <summary>Union: modeInfoIdx, or (desktopModeInfoIdx:16 | targetModeInfoIdx:16) when QDC_VIRTUAL_MODE_AWARE.</summary>
    public uint ModeInfoIdx;
    public DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY OutputTechnology;
    public DISPLAYCONFIG_ROTATION Rotation;
    public DISPLAYCONFIG_SCALING Scaling;
    public DISPLAYCONFIG_RATIONAL RefreshRate;
    public DISPLAYCONFIG_SCANLINE_ORDERING ScanLineOrdering;
    /// <summary>BOOL.</summary>
    public int TargetAvailable;
    public uint StatusFlags;

    public const uint STATUS_IN_USE = 0x1;
    public const uint DESKTOP_MODE_IDX_INVALID = 0xFFFF;
    public const uint TARGET_MODE_IDX_INVALID = 0xFFFF;

    public ushort DesktopModeInfoIdx
    {
        readonly get => (ushort)(ModeInfoIdx & 0xFFFF);
        set => ModeInfoIdx = (ModeInfoIdx & 0xFFFF0000) | value;
    }

    public ushort TargetModeInfoIdx
    {
        readonly get => (ushort)(ModeInfoIdx >> 16);
        set => ModeInfoIdx = (ModeInfoIdx & 0x0000FFFF) | ((uint)value << 16);
    }
}

[StructLayout(LayoutKind.Sequential)]
public struct DISPLAYCONFIG_PATH_INFO
{
    public DISPLAYCONFIG_PATH_SOURCE_INFO SourceInfo;
    public DISPLAYCONFIG_PATH_TARGET_INFO TargetInfo;
    public uint Flags;

    public const uint PATH_ACTIVE = 0x1;
    public const uint PATH_SUPPORT_VIRTUAL_MODE = 0x8;
}

[StructLayout(LayoutKind.Sequential)]
public struct DISPLAYCONFIG_VIDEO_SIGNAL_INFO
{
    public ulong PixelRate;
    public DISPLAYCONFIG_RATIONAL HSyncFreq;
    public DISPLAYCONFIG_RATIONAL VSyncFreq;
    public DISPLAYCONFIG_2DREGION ActiveSize;
    public DISPLAYCONFIG_2DREGION TotalSize;
    /// <summary>Union: videoStandard, or (videoStandard:16 | vSyncFreqDivider:6 | reserved:10).</summary>
    public uint VideoStandard;
    public DISPLAYCONFIG_SCANLINE_ORDERING ScanLineOrdering;
}

[StructLayout(LayoutKind.Sequential)]
public struct DISPLAYCONFIG_TARGET_MODE
{
    public DISPLAYCONFIG_VIDEO_SIGNAL_INFO TargetVideoSignalInfo;
}

[StructLayout(LayoutKind.Sequential)]
public struct DISPLAYCONFIG_SOURCE_MODE
{
    public uint Width;
    public uint Height;
    public DISPLAYCONFIG_PIXELFORMAT PixelFormat;
    public POINTL Position;
}

[StructLayout(LayoutKind.Sequential)]
public struct DISPLAYCONFIG_DESKTOP_IMAGE_INFO
{
    public POINTL PathSourceSize;
    public RECTL DesktopImageRegion;
    public RECTL DesktopImageClip;
}

/// <summary>
/// 64 bytes. The union of target(48)/source(20)/desktopImage(40) modes starts at offset 16.
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 64)]
public struct DISPLAYCONFIG_MODE_INFO
{
    [FieldOffset(0)] public DISPLAYCONFIG_MODE_INFO_TYPE InfoType;
    [FieldOffset(4)] public uint Id;
    [FieldOffset(8)] public LUID AdapterId;
    [FieldOffset(16)] public DISPLAYCONFIG_TARGET_MODE TargetMode;
    [FieldOffset(16)] public DISPLAYCONFIG_SOURCE_MODE SourceMode;
    [FieldOffset(16)] public DISPLAYCONFIG_DESKTOP_IMAGE_INFO DesktopImageInfo;
}

[StructLayout(LayoutKind.Sequential)]
public struct DISPLAYCONFIG_DEVICE_INFO_HEADER
{
    public DISPLAYCONFIG_DEVICE_INFO_TYPE Type;
    public uint Size;
    public LUID AdapterId;
    public uint Id;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
public struct DISPLAYCONFIG_SOURCE_DEVICE_NAME
{
    public DISPLAYCONFIG_DEVICE_INFO_HEADER Header;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
    public string ViewGdiDeviceName;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
public struct DISPLAYCONFIG_TARGET_DEVICE_NAME
{
    public DISPLAYCONFIG_DEVICE_INFO_HEADER Header;
    public uint Flags;
    public DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY OutputTechnology;
    public ushort EdidManufactureId;
    public ushort EdidProductCodeId;
    public uint ConnectorInstance;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
    public string MonitorFriendlyDeviceName;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
    public string MonitorDevicePath;

    public const uint FLAG_FRIENDLY_NAME_FROM_EDID = 0x1;
    public const uint FLAG_FRIENDLY_NAME_FORCED = 0x2;
    public const uint FLAG_EDID_IDS_VALID = 0x4;

    public readonly bool FriendlyNameFromEdid => (Flags & FLAG_FRIENDLY_NAME_FROM_EDID) != 0;
    public readonly bool EdidIdsValid => (Flags & FLAG_EDID_IDS_VALID) != 0;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
public struct DISPLAYCONFIG_ADAPTER_NAME
{
    public DISPLAYCONFIG_DEVICE_INFO_HEADER Header;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
    public string AdapterDevicePath;
}

[StructLayout(LayoutKind.Sequential)]
public struct DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO
{
    public DISPLAYCONFIG_DEVICE_INFO_HEADER Header;
    /// <summary>Bitfield: advancedColorSupported:1, advancedColorEnabled:1, wideColorEnforced:1, advancedColorForceDisabled:1.</summary>
    public uint Value;
    public DISPLAYCONFIG_COLOR_ENCODING ColorEncoding;
    public uint BitsPerColorChannel;

    public readonly bool AdvancedColorSupported => (Value & 0x1) != 0;
    public readonly bool AdvancedColorEnabled => (Value & 0x2) != 0;
    public readonly bool WideColorEnforced => (Value & 0x4) != 0;
    public readonly bool AdvancedColorForceDisabled => (Value & 0x8) != 0;
}

[StructLayout(LayoutKind.Sequential)]
public struct DISPLAYCONFIG_SET_ADVANCED_COLOR_STATE
{
    public DISPLAYCONFIG_DEVICE_INFO_HEADER Header;
    /// <summary>Bitfield: enableAdvancedColor:1.</summary>
    public uint Value;
}

[StructLayout(LayoutKind.Sequential)]
public struct DISPLAYCONFIG_SDR_WHITE_LEVEL
{
    public DISPLAYCONFIG_DEVICE_INFO_HEADER Header;
    /// <summary>White level in units of 1/1000 of 80 nits: nits = SDRWhiteLevel / 1000 * 80.</summary>
    public uint SDRWhiteLevel;
}

/// <summary>Windows 11 24H2+. Distinguishes real HDR from ACM-on-SDR, which type 9 conflates.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO_2
{
    public DISPLAYCONFIG_DEVICE_INFO_HEADER Header;
    /// <summary>Bitfield — see property accessors.</summary>
    public uint Value;
    public DISPLAYCONFIG_COLOR_ENCODING ColorEncoding;
    public uint BitsPerColorChannel;
    public DISPLAYCONFIG_ADVANCED_COLOR_MODE ActiveColorMode;

    public readonly bool AdvancedColorSupported => (Value & 0x01) != 0;
    public readonly bool AdvancedColorActive => (Value & 0x02) != 0;
    public readonly bool AdvancedColorLimitedByPolicy => (Value & 0x08) != 0;
    public readonly bool HighDynamicRangeSupported => (Value & 0x10) != 0;
    public readonly bool HighDynamicRangeUserEnabled => (Value & 0x20) != 0;
    public readonly bool WideColorSupported => (Value & 0x40) != 0;
    public readonly bool WideColorUserEnabled => (Value & 0x80) != 0;
}

/// <summary>Windows 11 24H2+.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct DISPLAYCONFIG_SET_HDR_STATE
{
    public DISPLAYCONFIG_DEVICE_INFO_HEADER Header;
    /// <summary>Bitfield: enableHdr:1.</summary>
    public uint Value;

    public bool EnableHdr
    {
        readonly get => (Value & 0x1) != 0;
        set => Value = value ? Value | 0x1u : Value & ~0x1u;
    }
}

/// <summary>
/// Undocumented (type -3). Values are steps relative to the OS-recommended scale, indexing
/// the fixed 100..500 table. Struct size asserted at 0x20 by every known implementation.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct DISPLAYCONFIG_GET_SOURCE_DPI_SCALE
{
    public DISPLAYCONFIG_DEVICE_INFO_HEADER Header;
    public int MinScaleRel;
    public int CurScaleRel;
    public int MaxScaleRel;
}

/// <summary>Undocumented (type -4). Size asserted at 0x18.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct DISPLAYCONFIG_SET_SOURCE_DPI_SCALE
{
    public DISPLAYCONFIG_DEVICE_INFO_HEADER Header;
    public int ScaleRel;
}

/// <summary>Undocumented (type 0xFFFFFFEE). SDRWhiteLevel = nits * 1000 / 80; FinalValue = 1.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct DISPLAYCONFIG_SET_SDR_WHITE_LEVEL
{
    public DISPLAYCONFIG_DEVICE_INFO_HEADER Header;
    public uint SDRWhiteLevel;
    public byte FinalValue;
}
