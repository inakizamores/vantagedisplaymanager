using System.Collections.Generic;
using System.Linq;
using Vantage.Core.Services;
using Xunit;

namespace Vantage.Core.Tests;

/// <summary>
/// Identity resolution for panels that share an EDID serial (issue #9). The seeds are the real
/// ones from that report: three ASUS ROG Strix XG438Q, all burned with serial 125727, so all
/// three produced the id <c>AUS43E1_125727</c> and every index built from it threw
/// "An item with the same key has already been added".
/// </summary>
public class MonitorIdentityResolverTests
{
    private const string Xg438Q = "AUS43E1_125727";

    private static MonitorIdentitySeed Asus(int uid) =>
        new(Xg438Q, $@"DISPLAY\AUS43E1\5&1f256ffa&0&UID{uid}");

    private static readonly MonitorIdentitySeed[] ReportedSetup =
        [Asus(37123), Asus(37120), Asus(37125)];

    [Fact]
    public void ThreeIdenticalPanels_GetDistinctIds()
    {
        var resolved = MonitorIdentityResolver.Resolve(ReportedSetup);

        Assert.Equal(3, resolved.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(
            ["AUS43E1_125727#UID37123", "AUS43E1_125727#UID37120", "AUS43E1_125727#UID37125"],
            resolved);
    }

    [Fact]
    public void DistinctPanels_KeepTheirIdsVerbatim()
    {
        // The common case must stay byte-identical, or every existing profile stops matching.
        MonitorIdentitySeed[] seeds =
        [
            new("SAM7454_HNTX500005", @"DISPLAY\SAM7454\5&35454913&0&UID4355"),
            new("DEL41CD_9GX2P3", @"DISPLAY\DELA1CD\5&35454913&0&UID4356"),
        ];

        Assert.Equal(["SAM7454_HNTX500005", "DEL41CD_9GX2P3"], MonitorIdentityResolver.Resolve(seeds));
    }

    [Fact]
    public void Resolution_DoesNotDependOnEnumerationOrder()
    {
        // Windows does not promise a stable path order, and an order-dependent id would hand
        // one monitor another's settings after a reboot — worse than the crash it replaced.
        var forward = Pairs(ReportedSetup);
        // Enumerable.Reverse spelled out: on a newer SDK, ReportedSetup.Reverse() binds to the
        // void MemoryExtensions.Reverse(Span<T>) instead, which does not compile here.
        var reversed = Pairs(Enumerable.Reverse(ReportedSetup).ToArray());

        Assert.Equal(forward, reversed);

        static Dictionary<string, string> Pairs(IReadOnlyList<MonitorIdentitySeed> seeds) =>
            MonitorIdentityResolver.Resolve(seeds)
                .Select((id, i) => (seeds[i].DeviceInstanceId, id))
                .ToDictionary(x => x.DeviceInstanceId, x => x.id);
    }

    [Fact]
    public void Resolution_IsIdempotent()
    {
        var once = MonitorIdentityResolver.Resolve(ReportedSetup);
        var twice = MonitorIdentityResolver.Resolve(
            once.Select((id, i) => new MonitorIdentitySeed(id, ReportedSetup[i].DeviceInstanceId)).ToArray());

        Assert.Equal(once, twice);
    }

    [Fact]
    public void InstanceIdWithoutUidToken_FallsBackToAStableHash()
    {
        MonitorIdentitySeed[] seeds =
        [
            new("NOEDID_X", @"UNKNOWN\EE78_37120"),
            new("NOEDID_X", @"UNKNOWN\EE78_37125"),
        ];

        var resolved = MonitorIdentityResolver.Resolve(seeds);

        Assert.Equal(2, resolved.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(resolved, id => Assert.StartsWith("NOEDID_X#X", id, StringComparison.Ordinal));
        // Stable: the same input always yields the same discriminator.
        Assert.Equal(resolved, MonitorIdentityResolver.Resolve(seeds));
    }

    [Fact]
    public void IdenticalInstancePaths_StillResolveToDistinctIds()
    {
        // Windows never does this, but the uniqueness guarantee is unconditional — that is the
        // whole point of the change: callers can index by id without a defensive check.
        MonitorIdentitySeed[] seeds = [Asus(37120), Asus(37120), Asus(37120)];

        Assert.Equal(3, MonitorIdentityResolver.Resolve(seeds).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Theory]
    [InlineData("AUS43E1_125727#UID37123", "AUS43E1_125727")]
    [InlineData("AUS43E1_125727#UID37123-2", "AUS43E1_125727")]
    [InlineData("NOEDID_X#X1A2B3C4D", "NOEDID_X")]
    [InlineData("AUS43E1_125727", "AUS43E1_125727")]
    // An EDID serial string is free-form: a '#' the resolver did not put there stays put.
    [InlineData("AUS43E1_SER#42", "AUS43E1_SER#42")]
    public void BaseId_StripsOnlyOurOwnDiscriminators(string stableId, string expected) =>
        Assert.Equal(expected, MonitorIdentityResolver.BaseId(stableId));

    [Fact]
    public void SafeIndex_KeepsFirstEntryInsteadOfThrowing()
    {
        string[] items = ["a", "A", "b"];

        var index = SafeIndex.By(items, x => x, "test", StringComparer.OrdinalIgnoreCase);

        Assert.Equal(2, index.Count);
        Assert.Equal("a", index["A"]);
    }
}
