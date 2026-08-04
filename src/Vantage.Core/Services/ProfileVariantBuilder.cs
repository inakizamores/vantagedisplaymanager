using Vantage.Core.Models;
using Vantage.Interop.Gdi;

namespace Vantage.Core.Services;

public sealed record DisplayOverride
{
    public required string StableId { get; init; }
    public uint? Width { get; init; }
    public uint? Height { get; init; }
    /// <summary>Desired refresh in Hz; when omitted with a resolution change, the highest supported rate is chosen.</summary>
    public uint? RefreshHz { get; init; }
    public bool? HdrEnabled { get; init; }
}

/// <summary>
/// Derives a new profile from a live snapshot with per-display mode/HDR overrides —
/// the "DisplayMagician preset gallery" workflow without round-tripping through
/// Windows Settings. Validates modes against the driver's mode list and keeps
/// neighboring displays glued to the resized display's edge.
/// </summary>
public static class ProfileVariantBuilder
{
    public static VantageProfile Build(SystemSnapshot snapshot, string name, IReadOnlyList<DisplayOverride> overrides)
    {
        var liveById = snapshot.Displays.ToDictionary(d => d.Identity.StableId, StringComparer.OrdinalIgnoreCase);
        var displays = new List<ProfileDisplay>();

        // Pass 1: resolve overrides and validate requested modes against the driver.
        var resolved = new Dictionary<string, (uint W, uint H, uint MilliHz, bool? Hdr)>(StringComparer.OrdinalIgnoreCase);
        foreach (var over in overrides)
        {
            if (!liveById.TryGetValue(over.StableId, out var live))
                throw new InvalidOperationException($"Display '{over.StableId}' is not connected.");

            var width = over.Width ?? live.Width;
            var height = over.Height ?? live.Height;
            uint milliHz;

            if (over.Width is not null || over.Height is not null || over.RefreshHz is not null)
            {
                if (live.GdiDeviceName is not { Length: > 0 } gdiName)
                    throw new InvalidOperationException($"Cannot enumerate modes for '{live.Identity.FriendlyName}'.");

                var modes = GdiApi.EnumerateModes(gdiName);
                var candidates = modes.Where(m => m.Width == width && m.Height == height).ToList();
                if (candidates.Count == 0)
                    throw new InvalidOperationException(
                        $"'{live.Identity.FriendlyName}' does not support {width}x{height}. " +
                        $"Supported: {string.Join(", ", modes.Select(m => $"{m.Width}x{m.Height}").Distinct().Take(12))}");

                var hz = over.RefreshHz ?? candidates.Max(m => m.RefreshHz);
                if (candidates.All(m => m.RefreshHz != hz))
                    throw new InvalidOperationException(
                        $"'{live.Identity.FriendlyName}' does not support {width}x{height} @ {hz} Hz. " +
                        $"Available: {string.Join(", ", candidates.Select(m => m.RefreshHz).OrderDescending())} Hz");

                milliHz = hz * 1000;
            }
            else
            {
                milliHz = live.RefreshMillihertz;
            }

            resolved[over.StableId] = (width, height, milliHz, over.HdrEnabled);
        }

        // Pass 2: build the display list, shifting neighbors to stay adjacent to resized displays.
        foreach (var live in snapshot.Displays)
        {
            var posX = live.PositionX;
            var posY = live.PositionY;

            foreach (var (id, (w, _, _, _)) in resolved)
            {
                var edited = liveById[id];
                var deltaX = (int)edited.Width - (int)w;
                if (deltaX != 0 && !string.Equals(live.Identity.StableId, id, StringComparison.OrdinalIgnoreCase)
                    && live.PositionX >= edited.PositionX + (int)edited.Width)
                {
                    posX -= deltaX;
                }
            }

            var hasOverride = resolved.TryGetValue(live.Identity.StableId, out var o);
            var hdrEnabled = hasOverride && o.Hdr is not null
                ? (live.Hdr.Supported ? o.Hdr : null)
                : (live.Hdr.Supported ? live.Hdr.Enabled : null);

            // Color depth follows the HDR intent strictly: HDR → 10 bpc, SDR → 8 bpc.
            // Leaving it floating lets the driver keep the wrong depth across HDR toggles
            // (washed-out colors). Displays without an HDR override keep their current bpc.
            var colorDepth = hasOverride && o.Hdr is not null && live.Hdr.Supported
                ? (o.Hdr == true ? 10 : 8)
                : live.OutputBpc;

            displays.Add(new ProfileDisplay
            {
                Identity = live.Identity,
                Enabled = true,
                Primary = live.IsPrimary,
                PositionX = posX,
                PositionY = posY,
                Width = hasOverride ? o.W : live.Width,
                Height = hasOverride ? o.H : live.Height,
                RefreshMillihertz = hasOverride ? o.MilliHz : live.RefreshMillihertz,
                Rotation = live.Rotation,
                DpiScalePercent = live.Dpi?.CurrentPercent,
                HdrEnabled = hdrEnabled,
                SdrWhiteLevelNits = live.Hdr is { Enabled: true, SdrWhiteLevelNits: not null } ? live.Hdr.SdrWhiteLevelNits : null,
                ColorDepthBpc = colorDepth,
            });
        }

        return new VantageProfile
        {
            Id = Guid.NewGuid(),
            Name = name,
            Displays = displays,
            Replay = snapshot.Replay,
        };
    }
}
