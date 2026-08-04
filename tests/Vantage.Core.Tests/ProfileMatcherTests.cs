using System.Text.Json;
using System.Text.Json.Serialization;
using Vantage.Core.Models;
using Vantage.Core.Services;
using Xunit;

namespace Vantage.Core.Tests;

/// <summary>
/// Matcher tests over a recorded fixture from a real machine (Samsung Odyssey G9 5120x1440@240
/// + AV receiver 1920x1080@120, hybrid NVIDIA/AMD adapters). BLUEPRINT P1/P10: tolerant
/// semantic matching, verified against real captured data — the anti-DisplayMagician tests.
/// </summary>
public class ProfileMatcherTests
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private static SystemSnapshot LoadSnapshot()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "snapshot-g9-avr.json");
        return JsonSerializer.Deserialize<SystemSnapshot>(File.ReadAllText(path), Json)!;
    }

    [Fact]
    public void ProfileFromSnapshot_IsActive_AgainstThatSnapshot()
    {
        var snapshot = LoadSnapshot();
        var profile = ProfileStore.FromSnapshot(snapshot, "roundtrip");

        var match = ProfileMatcher.Match(profile, snapshot);

        Assert.True(match.IsActive);
        Assert.True(match.IsPossible);
        Assert.All(match.Displays, d => Assert.Equal(DisplayMatchKind.Match, d.Kind));
    }

    [Fact]
    public void RefreshRate_WithinHalfPercent_MatchesWithTolerance()
    {
        var snapshot = LoadSnapshot();
        var profile = ProfileStore.FromSnapshot(snapshot, "tolerance");

        // 239.76 Hz vs the captured 240 Hz — the classic 1000/1001 NTSC-style drift.
        var g9 = profile.Displays.First(d => d.Width == 5120);
        profile.Displays[profile.Displays.IndexOf(g9)] = g9 with { RefreshMillihertz = 239760 };

        var match = ProfileMatcher.Match(profile, snapshot);

        Assert.True(match.IsActive);
        Assert.Contains(match.Displays, d => d.Kind == DisplayMatchKind.MatchWithTolerance);
    }

    [Fact]
    public void RefreshRate_MeaningfullyDifferent_IsMismatch()
    {
        var snapshot = LoadSnapshot();
        var profile = ProfileStore.FromSnapshot(snapshot, "diff");

        var g9 = profile.Displays.First(d => d.Width == 5120);
        profile.Displays[profile.Displays.IndexOf(g9)] = g9 with { RefreshMillihertz = 120000 };

        var match = ProfileMatcher.Match(profile, snapshot);

        Assert.False(match.IsActive);
        Assert.Contains(match.Displays, d => d.Diffs.Any(x => x.Field == "refresh"));
    }

    [Fact]
    public void ResolutionChange_IsMismatch_WithFieldDiff()
    {
        var snapshot = LoadSnapshot();
        var profile = ProfileStore.FromSnapshot(snapshot, "res");

        var g9 = profile.Displays.First(d => d.Width == 5120);
        profile.Displays[profile.Displays.IndexOf(g9)] = g9 with { Width = 3430 };

        var match = ProfileMatcher.Match(profile, snapshot);

        Assert.False(match.IsActive);
        var diff = match.Displays.SelectMany(d => d.Diffs).Single(x => x.Field == "resolution");
        Assert.Contains("3430", diff.Expected);
    }

    [Fact]
    public void MissingDisplay_MakesProfileNotPossible()
    {
        var snapshot = LoadSnapshot();
        var profile = ProfileStore.FromSnapshot(snapshot, "missing");
        profile.Displays.Add(new ProfileDisplay
        {
            Identity = new MonitorIdentity
            {
                StableId = "DEL_9999_NOTCONNECTED",
                DeviceInstanceId = @"DISPLAY\DEL9999\1&0&UID999",
                FriendlyName = "Imaginary Monitor",
            },
            Width = 2560,
            Height = 1440,
            RefreshMillihertz = 60000,
        });

        var match = ProfileMatcher.Match(profile, snapshot);

        Assert.False(match.IsPossible);
        Assert.Contains(match.Displays, d => d.Kind == DisplayMatchKind.DisplayMissing);
    }

    [Fact]
    public void HdrDifference_IsReportedAsHdrField()
    {
        var snapshot = LoadSnapshot();
        var profile = ProfileStore.FromSnapshot(snapshot, "hdr");

        var hdrCapable = profile.Displays.FirstOrDefault(d => d.HdrEnabled is not null);
        Assert.NotNull(hdrCapable); // fixture machine has an HDR-capable G9
        profile.Displays[profile.Displays.IndexOf(hdrCapable!)] = hdrCapable! with { HdrEnabled = !hdrCapable!.HdrEnabled };

        var match = ProfileMatcher.Match(profile, snapshot);

        Assert.False(match.IsActive);
        Assert.Contains(match.Displays, d => d.Diffs.Any(x => x.Field == "hdr"));
    }

    [Fact]
    public void IdentityFallback_MatchesByDeviceInstanceId_WhenStableIdDiffers()
    {
        var snapshot = LoadSnapshot();
        var profile = ProfileStore.FromSnapshot(snapshot, "fallback");

        // Simulate an EDID read failing on a later session: StableId degrades but the
        // PnP instance path still identifies the monitor.
        var first = profile.Displays[0];
        profile.Displays[0] = first with
        {
            Identity = first.Identity with { StableId = "NOEDID_SOMETHING_ELSE" },
        };

        var match = ProfileMatcher.Match(profile, snapshot);

        Assert.True(match.IsPossible);
        Assert.NotEqual(DisplayMatchKind.DisplayMissing, match.Displays[0].Kind);
    }
}
