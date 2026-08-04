namespace Vantage.Core.Models;

/// <summary>Per-display desired state inside a profile — the normalized schema (BLUEPRINT §4).</summary>
public sealed record ProfileDisplay
{
    public required MonitorIdentity Identity { get; init; }
    public bool Enabled { get; init; } = true;
    public bool Primary { get; init; }
    public int PositionX { get; init; }
    public int PositionY { get; init; }
    public uint Width { get; init; }
    public uint Height { get; init; }
    public uint RefreshMillihertz { get; init; }
    public DisplayRotation Rotation { get; init; } = DisplayRotation.Landscape;
    public int? DpiScalePercent { get; init; }
    public bool? HdrEnabled { get; init; }
    public double? SdrWhiteLevelNits { get; init; }
    /// <summary>Output color depth (bpc) via the GPU vendor API. HDR presets pin 10, SDR presets pin 8 — leaving it floating causes washed-out colors when the driver keeps the wrong depth across HDR toggles.</summary>
    public int? ColorDepthBpc { get; init; }
    /// <summary>Reserved for vendor blobs (Surround/Eyefinity/DRS) — BLUEPRINT P5.</summary>
    public Dictionary<string, string>? VendorExtras { get; init; }
}

public sealed record VantageProfile
{
    public required Guid Id { get; init; }
    public required string Name { get; set; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;
    public string? Hotkey { get; set; }
    public required List<ProfileDisplay> Displays { get; init; }
    public required ReplayPayload Replay { get; init; }
}

/// <summary>Versioned on-disk envelope (BLUEPRINT P8).</summary>
public sealed record ProfileFileEnvelope
{
    public int SchemaVersion { get; init; } = 1;
    public DateTimeOffset LastUpdated { get; set; } = DateTimeOffset.Now;
    public List<VantageProfile> Profiles { get; init; } = [];
}

public enum DisplayMatchKind
{
    Match,
    MatchWithTolerance,
    Mismatch,
    DisplayMissing,
}

public sealed record FieldDiff(string Field, string Expected, string Actual);

public sealed record DisplayMatchResult
{
    public required MonitorIdentity ProfileIdentity { get; init; }
    public required DisplayMatchKind Kind { get; init; }
    public List<FieldDiff> Diffs { get; init; } = [];
}

/// <summary>Scored, per-field result of comparing a profile against live state — never a bare bool (BLUEPRINT P1).</summary>
public sealed record ProfileMatchResult
{
    public required Guid ProfileId { get; init; }
    public required List<DisplayMatchResult> Displays { get; init; }
    /// <summary>Displays currently active that the profile doesn't mention.</summary>
    public List<string> UnexpectedActiveDisplays { get; init; } = [];

    /// <summary>All profile displays matched (possibly within tolerance) and no extras are active.</summary>
    public bool IsActive =>
        UnexpectedActiveDisplays.Count == 0 &&
        Displays.All(d => d.Kind is DisplayMatchKind.Match or DisplayMatchKind.MatchWithTolerance);

    /// <summary>Every display the profile needs is currently connected (though maybe not in the right mode).</summary>
    public bool IsPossible => Displays.All(d => d.Kind != DisplayMatchKind.DisplayMissing);
}
