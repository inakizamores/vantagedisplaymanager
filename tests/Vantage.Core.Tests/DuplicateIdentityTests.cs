using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Vantage.Core.Models;
using Vantage.Core.Services;
using Xunit;

namespace Vantage.Core.Tests;

/// <summary>
/// End-to-end cover for issue #9, over a fixture recorded from the reporter's machine: three
/// ASUS ROG Strix XG438Q (one DisplayPort, two HDMI) that all report EDID serial 125727.
/// Saving a profile there crashed with "An item with the same key has already been added.
/// Key: AUS43E1_125727".
/// </summary>
public class DuplicateIdentityTests
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private static SystemSnapshot LoadSnapshot()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "snapshot-xg438q-triple.json");
        return JsonSerializer.Deserialize<SystemSnapshot>(File.ReadAllText(path), Json)!;
    }

    /// <summary>A profile as 1.0.1 would have written it: every display keyed on the shared EDID id.</summary>
    private static VantageProfile LegacyProfile(SystemSnapshot snapshot)
    {
        var profile = ProfileStore.FromSnapshot(snapshot, "legacy");
        for (var i = 0; i < profile.Displays.Count; i++)
        {
            var d = profile.Displays[i];
            profile.Displays[i] = d with
            {
                Identity = d.Identity with { StableId = MonitorIdentityResolver.BaseId(d.Identity.StableId) },
            };
        }
        return profile;
    }

    [Fact]
    public void Snapshot_HasDistinctIdentities()
    {
        var snapshot = LoadSnapshot();

        Assert.Equal(3, snapshot.Displays.Count);
        Assert.Equal(3, snapshot.Displays.Select(d => d.Identity.StableId)
            .Distinct(StringComparer.OrdinalIgnoreCase).Count());
        // All three still carry the shared EDID identity underneath.
        Assert.All(snapshot.Displays,
            d => Assert.Equal("AUS43E1_125727", MonitorIdentityResolver.BaseId(d.Identity.StableId)));
    }

    [Fact]
    public void Capture_ResolvesTheRawEdidIdsItReadsFromTheRegistry()
    {
        // What Capture() holds mid-flight, before identities are resolved: the raw EDID id on
        // all three. Covers the wiring in DisplayService, which can't be exercised end-to-end
        // without the hardware.
        var raw = LoadSnapshot().Displays
            .Select(d => d with { Identity = d.Identity with { StableId = "AUS43E1_125727" } })
            .ToList();

        DisplayService.ResolveIdentities(raw);

        Assert.Equal(3, raw.Select(d => d.Identity.StableId).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(
            LoadSnapshot().Displays.Select(d => d.Identity.StableId),
            raw.Select(d => d.Identity.StableId));
    }

    [Fact]
    public void SaveCurrentSetup_DoesNotThrow_AndRoundTrips()
    {
        // The reporting action: "Applied Save current setup and received message ...".
        var snapshot = LoadSnapshot();

        var profile = ProfileStore.FromSnapshot(snapshot, "Save current setup");
        var match = ProfileMatcher.Match(profile, snapshot);

        Assert.True(match.IsActive);
        Assert.Empty(match.UnexpectedActiveDisplays);
        Assert.All(match.Displays, d => Assert.Equal(DisplayMatchKind.Match, d.Kind));
    }

    [Fact]
    public void ProfileVariant_DoesNotThrow()
    {
        var snapshot = LoadSnapshot();
        var target = snapshot.Displays[1];

        // No overrides: avoids GDI mode enumeration, which needs real hardware. The point is
        // that building the display index over duplicate-EDID panels no longer throws.
        var profile = ProfileVariantBuilder.Build(snapshot, "variant",
            [new DisplayOverride { StableId = target.Identity.StableId }]);

        Assert.Equal(3, profile.Displays.Count);
        Assert.True(ProfileMatcher.Match(profile, snapshot).IsActive);
    }

    [Fact]
    public void LegacyProfile_IsRepairedOnLoad()
    {
        var snapshot = LoadSnapshot();
        var legacy = LegacyProfile(snapshot);
        Assert.Single(legacy.Displays.Select(d => d.Identity.StableId).Distinct(StringComparer.OrdinalIgnoreCase));

        var file = Path.Combine(Path.GetTempPath(), $"vantage-issue9-{Guid.NewGuid():N}.json");
        try
        {
            var store = new ProfileStore(file);
            store.Upsert(legacy);

            var loaded = store.Load().Profiles.Single();

            // Repaired to the same ids the live snapshot now reports, so the profile reads as
            // active without reconfiguring anything.
            Assert.Equal(
                snapshot.Displays.Select(d => d.Identity.StableId).OrderBy(x => x, StringComparer.Ordinal),
                loaded.Displays.Select(d => d.Identity.StableId).OrderBy(x => x, StringComparer.Ordinal));
            Assert.True(ProfileMatcher.Match(loaded, snapshot).IsActive);
        }
        finally
        {
            foreach (var leftover in Directory.GetFiles(Path.GetDirectoryName(file)!, Path.GetFileName(file) + "*"))
                File.Delete(leftover);
        }
    }

    [Fact]
    public void LegacyProfile_MatchesEvenWithoutRepair()
    {
        // Independent of the store's repair pass: the matcher recognizes a pre-1.0.2 profile
        // on sight, via the PnP instance paths it already stored.
        var snapshot = LoadSnapshot();

        var match = ProfileMatcher.Match(LegacyProfile(snapshot), snapshot);

        Assert.True(match.IsActive);
        Assert.Empty(match.UnexpectedActiveDisplays);
    }

    /// <summary>
    /// The three cables swapped between ports: every id and instance path in the profile is now
    /// stale, so only the EDID id is left to match on. Real for identical panels — nothing on
    /// the outside of one distinguishes it from the next.
    /// </summary>
    private static VantageProfile MovedCablesProfile(SystemSnapshot snapshot)
    {
        var profile = ProfileStore.FromSnapshot(snapshot, "moved cables");
        for (var i = 0; i < profile.Displays.Count; i++)
        {
            var d = profile.Displays[i];
            profile.Displays[i] = d with
            {
                Identity = d.Identity with
                {
                    StableId = $"AUS43E1_125727#UID9900{i}",
                    DeviceInstanceId = $@"DISPLAY\AUS43E1\5&1f256ffa&0&UID9900{i}",
                },
            };
        }
        return profile;
    }

    [Fact]
    public void MovedCables_StillMatchOnTheSharedEdidId()
    {
        var snapshot = LoadSnapshot();

        var match = ProfileMatcher.Match(MovedCablesProfile(snapshot), snapshot);

        Assert.True(match.IsActive);
        Assert.Empty(match.UnexpectedActiveDisplays);
    }

    [Fact]
    public void EachProfileDisplay_ClaimsADifferentMonitor()
    {
        // The last-resort pass matches on the shared EDID id, which every panel here satisfies.
        // Without one-to-one assignment all three entries would latch onto the same monitor and
        // two would report as missing.
        var snapshot = LoadSnapshot();

        var match = ProfileMatcher.Match(MovedCablesProfile(snapshot), snapshot);

        Assert.Equal(3, match.Displays.Count);
        Assert.DoesNotContain(match.Displays, d => d.Kind == DisplayMatchKind.DisplayMissing);
    }

    [Fact]
    public void UnpluggingOnePanel_ReportsExactlyOneMissing()
    {
        // Two of the three still connected. Every profile entry shares an EDID id with every
        // live panel, so without one-to-one assignment all three would find a "match" among
        // the two survivors and the profile would claim to be applicable when it is not.
        var full = LoadSnapshot();
        var unplugged = full with { Displays = full.Displays.Take(2).ToList() };

        var match = ProfileMatcher.Match(MovedCablesProfile(full), unplugged);

        Assert.Single(match.Displays, d => d.Kind == DisplayMatchKind.DisplayMissing);
        Assert.False(match.IsPossible);
        Assert.Empty(match.UnexpectedActiveDisplays);
    }

    [Fact]
    public void Fallback_PairsPanelsByPositionRatherThanAtRandom()
    {
        // Three interchangeable panels: the assignment needing no mode changes is the right
        // one. Profile order must not decide which monitor an entry lands on.
        var snapshot = LoadSnapshot();
        var profile = MovedCablesProfile(snapshot);
        var shuffled = profile with { Displays = Enumerable.Reverse(profile.Displays).ToList() };

        var match = ProfileMatcher.Match(shuffled, snapshot);

        Assert.True(match.IsActive);
    }

    [Fact]
    public void DisabledDisplay_DoesNotStealAMonitorFromAnEnabledOne()
    {
        // Three entries, two panels connected, and the entry the profile wants switched off is
        // listed first — so plain profile order would let it take a monitor and leave one of
        // the two it wants switched on reading as "not connected".
        var full = LoadSnapshot();
        var unplugged = full with { Displays = full.Displays.Take(2).ToList() };
        var profile = MovedCablesProfile(full);
        var displays = new List<ProfileDisplay> { profile.Displays[0] with { Enabled = false } };
        displays.AddRange(profile.Displays.Skip(1));

        var match = ProfileMatcher.Match(profile with { Displays = displays }, unplugged);

        Assert.DoesNotContain(match.Displays.Skip(1), d => d.Kind == DisplayMatchKind.DisplayMissing);
    }
}
