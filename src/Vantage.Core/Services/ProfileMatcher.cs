using Vantage.Core.Models;

namespace Vantage.Core.Services;

/// <summary>
/// Semantic, tolerance-aware profile matching (BLUEPRINT P1). Compares the normalized
/// model only — never the raw replay payload.
/// </summary>
public static class ProfileMatcher
{
    /// <summary>Refresh rates within 0.5% are the same user-intended mode (59.94 ≈ 60, 239.76 ≈ 240).</summary>
    private const double RefreshRelativeTolerance = 0.005;

    public static ProfileMatchResult Match(VantageProfile profile, SystemSnapshot snapshot)
    {
        var live = snapshot.Displays.ToDictionary(d => d.Identity.StableId, d => d, StringComparer.OrdinalIgnoreCase);
        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var results = new List<DisplayMatchResult>();

        foreach (var wanted in profile.Displays)
        {
            if (!live.TryGetValue(wanted.Identity.StableId, out var actual) &&
                !TryMatchByInstanceId(live, wanted.Identity.DeviceInstanceId, out actual))
            {
                results.Add(new DisplayMatchResult
                {
                    ProfileIdentity = wanted.Identity,
                    Kind = wanted.Enabled ? DisplayMatchKind.DisplayMissing : DisplayMatchKind.Match,
                });
                continue;
            }

            claimed.Add(actual.Identity.StableId);
            var diffs = new List<FieldDiff>();
            var toleranceOnly = false;

            if (!wanted.Enabled)
            {
                // Profile wants this display off, but it is active.
                results.Add(new DisplayMatchResult
                {
                    ProfileIdentity = wanted.Identity,
                    Kind = DisplayMatchKind.Mismatch,
                    Diffs = [new FieldDiff("enabled", "false", "true")],
                });
                continue;
            }

            Check(diffs, "position", $"{wanted.PositionX},{wanted.PositionY}", $"{actual.PositionX},{actual.PositionY}",
                wanted.PositionX == actual.PositionX && wanted.PositionY == actual.PositionY);
            Check(diffs, "resolution", $"{wanted.Width}x{wanted.Height}", $"{actual.Width}x{actual.Height}",
                wanted.Width == actual.Width && wanted.Height == actual.Height);
            Check(diffs, "rotation", wanted.Rotation.ToString(), actual.Rotation.ToString(),
                wanted.Rotation == actual.Rotation);
            Check(diffs, "primary", wanted.Primary.ToString(), actual.IsPrimary.ToString(),
                wanted.Primary == actual.IsPrimary);

            // Refresh: exact match preferred, tolerance accepted.
            if (wanted.RefreshMillihertz != actual.RefreshMillihertz)
            {
                var rel = Math.Abs((double)wanted.RefreshMillihertz - actual.RefreshMillihertz)
                          / Math.Max(wanted.RefreshMillihertz, 1);
                if (rel <= RefreshRelativeTolerance)
                    toleranceOnly = true;
                else
                    diffs.Add(new FieldDiff("refresh", $"{wanted.RefreshMillihertz}mHz", $"{actual.RefreshMillihertz}mHz"));
            }

            if (wanted.HdrEnabled is { } hdrWanted && actual.Hdr.Supported)
                Check(diffs, "hdr", hdrWanted.ToString(), actual.Hdr.Enabled.ToString(), hdrWanted == actual.Hdr.Enabled);

            if (wanted.DpiScalePercent is { } dpiWanted && actual.Dpi is { } actualDpi)
                Check(diffs, "dpiScale", $"{dpiWanted}%", $"{actualDpi.CurrentPercent}%", dpiWanted == actualDpi.CurrentPercent);

            if (wanted.ColorDepthBpc is { } bpcWanted && actual.OutputBpc is { } bpcActual)
                Check(diffs, "colorDepth", $"{bpcWanted} bpc", $"{bpcActual} bpc", bpcWanted == bpcActual);

            results.Add(new DisplayMatchResult
            {
                ProfileIdentity = wanted.Identity,
                Kind = diffs.Count > 0 ? DisplayMatchKind.Mismatch
                     : toleranceOnly ? DisplayMatchKind.MatchWithTolerance
                     : DisplayMatchKind.Match,
                Diffs = diffs,
            });
        }

        var unexpected = snapshot.Displays
            .Where(d => !claimed.Contains(d.Identity.StableId))
            .Select(d => d.Identity.StableId)
            .ToList();

        return new ProfileMatchResult
        {
            ProfileId = profile.Id,
            Displays = results,
            UnexpectedActiveDisplays = unexpected,
        };
    }

    private static bool TryMatchByInstanceId(
        Dictionary<string, DisplayState> live, string instanceId, out DisplayState actual)
    {
        // Fallback for monitors whose EDID serial is blank/duplicated: match on the PnP instance path.
        foreach (var d in live.Values)
        {
            if (string.Equals(d.Identity.DeviceInstanceId, instanceId, StringComparison.OrdinalIgnoreCase))
            {
                actual = d;
                return true;
            }
        }
        actual = null!;
        return false;
    }

    private static void Check(List<FieldDiff> diffs, string field, string expected, string actualValue, bool ok)
    {
        if (!ok)
            diffs.Add(new FieldDiff(field, expected, actualValue));
    }
}
