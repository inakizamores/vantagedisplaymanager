using Vantage.Interop.Ccd;

namespace Vantage.Core.Models;

/// <summary>
/// Stable, cross-session identity for a monitor (BLUEPRINT P3). Never contains
/// adapter LUIDs, source ids, or target ids — those are session-scoped.
/// </summary>
public sealed record MonitorIdentity
{
    /// <summary>Primary key, e.g. "SAM7454_HNTX500005"; falls back to "NOEDID_&lt;instanceId&gt;".</summary>
    public required string StableId { get; init; }

    /// <summary>PnP device instance ID, e.g. DISPLAY\SAM7454\5&amp;35454913&amp;0&amp;UID4355.</summary>
    public required string DeviceInstanceId { get; init; }

    public string? FriendlyName { get; init; }
    public string? EdidManufacturer { get; init; }
    public ushort EdidProductCode { get; init; }
    public string? EdidSerial { get; init; }
}

public enum DisplayRotation
{
    Landscape = 1,
    Portrait = 2,
    LandscapeFlipped = 3,
    PortraitFlipped = 4,
}

public sealed record HdrInfo
{
    public bool Supported { get; init; }
    public bool Enabled { get; init; }
    /// <summary>Only meaningful on 24H2+ (SDR / WCG / HDR). Null on older builds.</summary>
    public string? ActiveColorMode { get; init; }
    public uint BitsPerColorChannel { get; init; }
    public string? ColorEncoding { get; init; }
    public double? SdrWhiteLevelNits { get; init; }
}

public sealed record DpiInfo
{
    public int CurrentPercent { get; init; }
    public int RecommendedPercent { get; init; }
    public int MinPercent { get; init; }
    public int MaxPercent { get; init; }
}

/// <summary>Session-scoped CCD addressing for a live display. Never persisted into profiles.</summary>
public sealed record CcdAddress
{
    public required ulong AdapterLuid { get; init; }
    public required uint SourceId { get; init; }
    public required uint TargetId { get; init; }
}

/// <summary>A live, connected, active display as captured right now.</summary>
public sealed record DisplayState
{
    public required MonitorIdentity Identity { get; init; }
    public required CcdAddress Address { get; init; }
    public string? GdiDeviceName { get; init; }          // \\.\DISPLAY1
    public string? AdapterDevicePath { get; init; }
    public string? AdapterName { get; init; }
    public required string OutputTechnology { get; init; }
    public bool IsPrimary { get; init; }
    public int PositionX { get; init; }
    public int PositionY { get; init; }
    public uint Width { get; init; }
    public uint Height { get; init; }
    /// <summary>Precise vertical refresh in millihertz (e.g. 239760 for 239.76 Hz).</summary>
    public uint RefreshMillihertz { get; init; }
    public DisplayRotation Rotation { get; init; }
    public string? Scaling { get; init; }
    public HdrInfo Hdr { get; init; } = new();
    public DpiInfo? Dpi { get; init; }
    public int PhysicalWidthMm { get; init; }
    public int PhysicalHeightMm { get; init; }
    /// <summary>NVIDIA output color depth in bits per channel; null on other GPUs or when driver-default.</summary>
    public int? OutputBpc { get; init; }

    public double RefreshHz => RefreshMillihertz / 1000.0;
}

/// <summary>Raw CCD path/mode arrays in a JSON-friendly shape, replayed on apply. Never used for identity (BLUEPRINT P1).</summary>
public sealed record ReplayPayload
{
    public required List<ReplayPath> Paths { get; init; }
    public required List<ReplayMode> Modes { get; init; }
    /// <summary>Adapter LUID (as captured) → adapter device path, for re-mapping LUIDs at apply time.</summary>
    public required Dictionary<string, string> AdapterPaths { get; init; }
}

public sealed record ReplayPath
{
    public required ulong SourceAdapter { get; init; }
    public required uint SourceId { get; init; }
    public required uint SourceModeInfoIdx { get; init; }
    public required uint SourceStatusFlags { get; init; }
    public required ulong TargetAdapter { get; init; }
    public required uint TargetId { get; init; }
    public required uint TargetModeInfoIdx { get; init; }
    public required uint OutputTechnology { get; init; }
    public required uint Rotation { get; init; }
    public required uint Scaling { get; init; }
    public required uint RefreshNumerator { get; init; }
    public required uint RefreshDenominator { get; init; }
    public required uint ScanLineOrdering { get; init; }
    public required int TargetAvailable { get; init; }
    public required uint TargetStatusFlags { get; init; }
    public required uint Flags { get; init; }
    /// <summary>Stable identity of the monitor this path drove, for diagnostics and re-matching.</summary>
    public string? MonitorStableId { get; init; }
}

public sealed record ReplayMode
{
    public required uint InfoType { get; init; }
    public required uint Id { get; init; }
    public required ulong Adapter { get; init; }
    /// <summary>The 48-byte mode union, base64-encoded verbatim.</summary>
    public required string UnionBytes { get; init; }
}

/// <summary>Complete capture of the current display configuration.</summary>
public sealed record SystemSnapshot
{
    public required DateTimeOffset CapturedAt { get; init; }
    public required IReadOnlyList<DisplayState> Displays { get; init; }
    public required ReplayPayload Replay { get; init; }
}
