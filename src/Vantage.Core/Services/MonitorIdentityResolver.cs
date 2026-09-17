using System.Security.Cryptography;
using System.Text;

namespace Vantage.Core.Services;

/// <summary>A monitor awaiting identity resolution: its EDID-derived id plus its PnP instance path.</summary>
public readonly record struct MonitorIdentitySeed(string BaseId, string DeviceInstanceId);

/// <summary>
/// Guarantees that every monitor in a snapshot ends up with a distinct <c>StableId</c>.
///
/// The EDID-derived id (vendor + product + serial) is only as unique as the serial the vendor
/// burned in, and plenty of panels ship with the same serial across every unit of a model —
/// three ASUS XG438Q all report <c>AUS43E1_125727</c> (issue #9). That turned the id into a
/// model id, and every <c>ToDictionary(d =&gt; d.Identity.StableId)</c> in the codebase threw.
///
/// The fix keeps the EDID id as the primary key and only appends a discriminator when a
/// snapshot actually contains a collision, so the id is unchanged for the overwhelming
/// majority of machines and existing profiles keep matching. The discriminator comes from the
/// PnP instance path's <c>UID</c> token (<c>DISPLAY\AUS43E1\5&amp;1f256ffa&amp;0&amp;UID37123</c> →
/// <c>UID37123</c>), which identifies the GPU connector the panel is plugged into and survives
/// reboots — so the three monitors keep their identities across sessions rather than being
/// numbered by enumeration order, which would silently swap their settings.
///
/// Two invariants callers may rely on:
/// <list type="bullet">
/// <item>the returned ids are always distinct, whatever the inputs look like;</item>
/// <item>resolution is deterministic (never enumeration-order dependent) and idempotent —
/// re-resolving already-resolved ids returns them unchanged.</item>
/// </list>
/// </summary>
public static class MonitorIdentityResolver
{
    /// <summary>Separates the EDID id from the connector discriminator.</summary>
    public const char DiscriminatorSeparator = '#';

    /// <summary>
    /// The EDID-derived portion of an id — what the id would have been before disambiguation.
    /// Used to reunite a pre-disambiguation profile with the hardware it was saved from.
    /// </summary>
    public static string BaseId(string stableId)
    {
        var cut = stableId.IndexOf(DiscriminatorSeparator);
        if (cut < 0)
            return stableId;

        // Only treat '#' as our separator when what follows is a discriminator we minted.
        // An EDID serial string is free-form and could legitimately contain a '#'.
        return LooksLikeDiscriminator(stableId.AsSpan(cut + 1)) ? stableId[..cut] : stableId;
    }

    /// <summary>True when this id carries a connector discriminator (i.e. it shares an EDID id with another panel).</summary>
    public static bool IsDisambiguated(string stableId) => BaseId(stableId).Length != stableId.Length;

    /// <summary>
    /// Resolves one distinct id per monitor, positionally matching <paramref name="monitors"/>.
    /// Ids that need no discriminator are returned exactly as they came in.
    /// </summary>
    public static IReadOnlyList<string> Resolve(IReadOnlyList<MonitorIdentitySeed> monitors)
    {
        var resolved = new string[monitors.Count];

        // Group on the base id so the pass is idempotent: feeding back already-resolved ids
        // regroups them exactly as before and reproduces the same discriminators.
        var groups = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < monitors.Count; i++)
        {
            var key = BaseId(monitors[i].BaseId);
            if (!groups.TryGetValue(key, out var members))
                groups[key] = members = [];
            members.Add(i);
        }

        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Ordered by base id, then by instance path — never by enumeration order, so the same
        // hardware resolves to the same ids on every boot.
        foreach (var (baseId, members) in groups.OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (members.Count == 1)
            {
                resolved[members[0]] = Claim(taken, baseId);
                continue;
            }

            var ordered = members
                .OrderBy(i => monitors[i].DeviceInstanceId, StringComparer.OrdinalIgnoreCase)
                .ThenBy(i => i);

            foreach (var i in ordered)
                resolved[i] = Claim(taken, baseId + DiscriminatorSeparator + Discriminator(monitors[i].DeviceInstanceId));
        }

        return resolved;
    }

    /// <summary>
    /// The connector token for a PnP instance path, e.g. <c>UID37123</c>. Falls back to a hash
    /// of the whole path when the vendor's enumerator doesn't use a UID token.
    /// </summary>
    private static string Discriminator(string deviceInstanceId)
    {
        if (!string.IsNullOrEmpty(deviceInstanceId))
        {
            var tail = deviceInstanceId[(deviceInstanceId.LastIndexOf('\\') + 1)..];
            foreach (var part in tail.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                if (part.Length > 3 && part.StartsWith("UID", StringComparison.OrdinalIgnoreCase)
                    && IsHex(part.AsSpan(3)))
                    return part.ToUpperInvariant();
            }
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(deviceInstanceId.ToUpperInvariant()));
        return "X" + Convert.ToHexString(hash)[..8];
    }

    /// <summary>Hands out <paramref name="candidate"/>, suffixing <c>-2</c>, <c>-3</c>… if it is already spoken for.</summary>
    private static string Claim(HashSet<string> taken, string candidate)
    {
        if (taken.Add(candidate))
            return candidate;

        // Only reachable if two monitors share a PnP instance path, which Windows does not do.
        // Kept as a hard backstop: this method must never return a duplicate.
        for (var n = 2; ; n++)
        {
            var next = $"{candidate}-{n}";
            if (taken.Add(next))
                return next;
        }
    }

    private static bool LooksLikeDiscriminator(ReadOnlySpan<char> s)
    {
        // Strip the "-N" collision suffix Claim may have appended.
        var dash = s.LastIndexOf('-');
        if (dash > 0 && IsDigits(s[(dash + 1)..]))
            s = s[..dash];

        if (s.Length > 3 && s.StartsWith("UID", StringComparison.OrdinalIgnoreCase) && IsHex(s[3..]))
            return true;

        return s.Length == 9 && (s[0] is 'X' or 'x') && IsHex(s[1..]);
    }

    private static bool IsHex(ReadOnlySpan<char> s)
    {
        if (s.IsEmpty)
            return false;
        foreach (var c in s)
            if (!Uri.IsHexDigit(c))
                return false;
        return true;
    }

    private static bool IsDigits(ReadOnlySpan<char> s)
    {
        if (s.IsEmpty)
            return false;
        foreach (var c in s)
            if (!char.IsAsciiDigit(c))
                return false;
        return true;
    }
}
