using System.Globalization;
using ShyFtp.Sync;
using ShyFtp.Util;

namespace ShyFtp.Cli;

/// <summary>
/// The one "what" syntax shared by add, drop, list, upload and download:
/// indices (3, 1,4, 5-9), keywords (all, selected, dropped, up, down, new, changed, review),
/// time windows (30m, 8h, 2d, or the older "recent 8"), and anything else as a glob/substring.
/// </summary>
public static class ItemSelector
{
    private static readonly string[] Keywords =
        { "all", "selected", "added", "dropped", "up", "uploads", "down", "downloads", "new", "changed", "review", "unchanged" };

    public static bool IsKeyword(string token) => Keywords.Contains(token.ToLowerInvariant());

    /// <summary>Splits comma lists and rewrites the legacy "recent N" (hours) form to "Nh".</summary>
    public static List<string> Normalize(IEnumerable<string> tokens)
    {
        var parts = tokens
            .SelectMany(t => t.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .ToList();

        var result = new List<string>();
        for (var i = 0; i < parts.Count; i++)
        {
            if (parts[i].Equals("recent", StringComparison.OrdinalIgnoreCase)
                && i + 1 < parts.Count
                && double.TryParse(parts[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out _))
            {
                result.Add(parts[++i] + "h");
                continue;
            }

            result.Add(parts[i]);
        }

        return result;
    }

    /// <summary>Parses "90s", "30m", "8h", "1.5d", "2w". A bare number is not a duration.</summary>
    public static bool TryParseDuration(string token, out TimeSpan span)
    {
        span = default;
        if (token.Length < 2)
            return false;

        var unit = char.ToLowerInvariant(token[^1]);
        if (!double.TryParse(token[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || value < 0)
            return false;

        span = unit switch
        {
            's' => TimeSpan.FromSeconds(value),
            'm' => TimeSpan.FromMinutes(value),
            'h' => TimeSpan.FromHours(value),
            'd' => TimeSpan.FromDays(value),
            'w' => TimeSpan.FromDays(value * 7),
            _ => TimeSpan.MinValue
        };
        return span != TimeSpan.MinValue;
    }

    /// <summary>Parses "7" or "5-9" as a one-based inclusive range.</summary>
    public static bool TryParseRange(string token, out int start, out int end)
    {
        var dash = token.IndexOf('-');
        if (dash > 0)
        {
            if (int.TryParse(token[..dash], out start) && int.TryParse(token[(dash + 1)..], out end))
                return true;
        }
        else if (int.TryParse(token, out start))
        {
            end = start;
            return true;
        }

        start = end = 0;
        return false;
    }

    /// <summary>Returns the zero-based indices of items matched by any token, in list order.</summary>
    public static List<int> Resolve(IReadOnlyList<SyncItem> items, IEnumerable<string> tokens, DateTime nowUtc, ICollection<string>? warnings = null)
    {
        var hits = new SortedSet<int>();
        foreach (var token in Normalize(tokens))
        {
            if (TryParseRange(token, out var start, out var end))
            {
                for (var n = Math.Max(start, 1); n <= Math.Min(end, items.Count); n++)
                    hits.Add(n - 1);
                if (start < 1 || end > items.Count || start > end)
                    warnings?.Add($"'{token}' is outside the list (items 1-{items.Count}).");

                continue;
            }

            Func<SyncItem, bool> predicate = MatchesItem(token, nowUtc);
            var matched = false;
            for (var i = 0; i < items.Count; i++)
            {
                if (predicate(items[i]))
                {
                    hits.Add(i);
                    matched = true;
                }
            }

            if (!matched)
                warnings?.Add($"Nothing matched '{token}'.");
        }

        return hits.ToList();
    }

    private static Func<SyncItem, bool> MatchesItem(string token, DateTime nowUtc)
    {
        switch (token.ToLowerInvariant())
        {
            case "all": return _ => true;
            case "selected" or "added": return i => i.Selected;
            case "dropped": return i => i.Dropped;
            case "up" or "uploads": return i => i.State is SyncState.UploadNew or SyncState.UploadChanged;
            case "down" or "downloads": return i => i.State is SyncState.DownloadNew or SyncState.DownloadChanged;
            case "new": return i => i.State is SyncState.UploadNew or SyncState.DownloadNew;
            case "changed": return i => i.State is SyncState.UploadChanged or SyncState.DownloadChanged or SyncState.Different;
            case "review": return i => i.State == SyncState.Different;
            case "unchanged": return i => i.State == SyncState.Unchanged;
        }

        if (TryParseDuration(token, out var span))
        {
            var cutoff = nowUtc - span;
            return i => i.LocalExists && i.LocalTime >= cutoff;
        }

        return i => GlobMatcher.IsMatch(token, i.RelativePath);
    }

    /// <summary>Matches a file on local disk (used by 'add'): "all", a time window, or a glob/substring.</summary>
    public static bool MatchesLocal(string token, string relativePath, DateTime modifiedUtc, DateTime nowUtc)
    {
        if (token.Equals("all", StringComparison.OrdinalIgnoreCase))
            return true;
        if (TryParseDuration(token, out var span))
            return modifiedUtc >= nowUtc - span;
        return GlobMatcher.IsMatch(token, relativePath);
    }
}
