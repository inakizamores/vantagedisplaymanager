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
        var assignment = Assign(profile, snapshot);
        var results = new List<DisplayMatchResult>();

        for (var i = 0; i < profile.Displays.Count; i++)
        {
            var wanted = profile.Displays[i];
            if (assignment[i] is not { } actual)
            {
                results.Add(new DisplayMatchResult
                {
                    ProfileIdentity = wanted.Identity,
                    Kind = wanted.Enabled ? DisplayMatchKind.DisplayMissing : DisplayMatchKind.Match,
                });
                continue;
            }

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

        var claimed = assignment.Where(a => a is not null).Select(a => a!.Identity.StableId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
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

    /// <summary>
    /// Pairs each profile display with at most one live display, and each live display with at
    /// most one profile display. Three passes, strongest signal first, so a weak signal can
    /// never steal a monitor from an exact match:
    /// <list type="number">
    /// <item>the resolved stable id — the normal path;</item>
    /// <item>the PnP instance path — for a panel whose EDID id changed under it (firmware update);</item>
    /// <item>the EDID id alone, best geometric fit — for identical panels whose ids only differ
    /// by connector, covering a moved cable and profiles written before ids were disambiguated.</item>
    /// </list>
    /// </summary>
    private static DisplayState?[] Assign(VantageProfile profile, SystemSnapshot snapshot)
    {
        var assignment = new DisplayState?[profile.Displays.Count];
        var claimed = new bool[snapshot.Displays.Count];

        // Enabled entries pick first: a display the profile wants switched off must never take
        // a monitor away from one it wants switched on.
        var order = Enumerable.Range(0, profile.Displays.Count)
            .OrderByDescending(i => profile.Displays[i].Enabled)
            .ThenBy(i => i)
            .ToArray();

        void Pass(Func<ProfileDisplay, DisplayState, bool> isCandidate, bool byBestFit = false)
        {
            foreach (var i in order)
            {
                if (assignment[i] is not null)
                    continue;

                var wanted = profile.Displays[i];
                var bestScore = int.MinValue;
                var best = -1;

                for (var j = 0; j < snapshot.Displays.Count; j++)
                {
                    if (claimed[j] || !isCandidate(wanted, snapshot.Displays[j]))
                        continue;

                    var score = byBestFit ? FitScore(wanted, snapshot.Displays[j]) : 0;
                    if (score > bestScore)
                    {
                        bestScore = score;
                        best = j;
                    }
                }

                if (best < 0)
                    continue;

                claimed[best] = true;
                assignment[i] = snapshot.Displays[best];
            }
        }

        Pass(static (w, d) => string.Equals(w.Identity.StableId, d.Identity.StableId, StringComparison.OrdinalIgnoreCase));
        Pass(static (w, d) => w.Identity.DeviceInstanceId.Length > 0
            && string.Equals(w.Identity.DeviceInstanceId, d.Identity.DeviceInstanceId, StringComparison.OrdinalIgnoreCase));
        Pass(static (w, d) => string.Equals(
            MonitorIdentityResolver.BaseId(w.Identity.StableId),
            MonitorIdentityResolver.BaseId(d.Identity.StableId),
            StringComparison.OrdinalIgnoreCase), byBestFit: true);

        return assignment;
    }

    /// <summary>
    /// How well a live display fits a profile entry, used only to pick among interchangeable
    /// panels of the same model. Position dominates: it is what tells the left monitor from
    /// the right one, and choosing by it means the fallback lands on the assignment that needs
    /// the fewest mode changes instead of shuffling three identical screens at random.
    /// </summary>
    private static int FitScore(ProfileDisplay wanted, DisplayState live)
    {
        var score = 0;
        if (wanted.PositionX == live.PositionX && wanted.PositionY == live.PositionY)
            score += 8;
        if (wanted.Width == live.Width && wanted.Height == live.Height)
            score += 4;
        if (wanted.Rotation == live.Rotation)
            score += 2;
        if (wanted.Primary == live.IsPrimary)
            score += 1;
        return score;
    }

    private static void Check(List<FieldDiff> diffs, string field, string expected, string actualValue, bool ok)
    {
        if (!ok)
            diffs.Add(new FieldDiff(field, expected, actualValue));
    }
}
