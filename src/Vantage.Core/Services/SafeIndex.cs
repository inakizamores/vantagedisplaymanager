namespace Vantage.Core.Services;

/// <summary>
/// <c>ToDictionary</c> that degrades instead of throwing.
///
/// Indexing displays by identity is the codebase's most common operation, and a plain
/// <c>ToDictionary</c> turns any unexpected duplicate into an <see cref="ArgumentException"/>
/// that surfaces to the user as "An item with the same key has already been added" — issue #9,
/// where three identical panels shared one EDID serial. <see cref="MonitorIdentityResolver"/>
/// removes that particular cause at the source; this removes the failure mode itself, so a
/// duplicate that slips in from anywhere (a hand-edited profile, a driver quirk we have not
/// seen yet) costs one display's worth of precision and a log line rather than the feature.
/// </summary>
public static class SafeIndex
{
    /// <summary>Indexes <paramref name="source"/> by key, keeping the first entry per key and logging any duplicate.</summary>
    public static Dictionary<TKey, TSource> By<TSource, TKey>(
        IEnumerable<TSource> source,
        Func<TSource, TKey> keySelector,
        string context,
        IEqualityComparer<TKey>? comparer = null)
        where TKey : notnull
        => By(source, keySelector, static x => x, context, comparer);

    /// <summary>Indexes <paramref name="source"/> by key and value, keeping the first entry per key and logging any duplicate.</summary>
    public static Dictionary<TKey, TValue> By<TSource, TKey, TValue>(
        IEnumerable<TSource> source,
        Func<TSource, TKey> keySelector,
        Func<TSource, TValue> valueSelector,
        string context,
        IEqualityComparer<TKey>? comparer = null)
        where TKey : notnull
    {
        var map = new Dictionary<TKey, TValue>(comparer);
        foreach (var item in source)
        {
            var key = keySelector(item);
            if (!map.TryAdd(key, valueSelector(item)))
                AppLog.Write(context, $"Duplicate key '{key}' while indexing; keeping the first entry.");
        }
        return map;
    }
}
