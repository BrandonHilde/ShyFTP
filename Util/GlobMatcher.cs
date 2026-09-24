using System.Text;
using System.Text.RegularExpressions;

namespace ShyFtp.Util;

public static class GlobMatcher
{
    public const string AllFiles = "*";

    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);

    public static bool IsMatch(string pattern, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(pattern))
            return false;

        var path = Normalize(relativePath);
        var pat = Normalize(pattern.Trim());

        if (pat == "*" || pat == "**")
            return true;

        var hasWildcard = pat.IndexOfAny(new[] { '*', '?', '[' }) >= 0;
        if (!hasWildcard)
        {
            return path.Contains(pat, StringComparison.OrdinalIgnoreCase);
        }

        var regex = "^" + GlobToRegex(pat) + "$";

        if (Regex.IsMatch(path, regex, RegexOptions.IgnoreCase, RegexTimeout))
            return true;

        if (!pat.Contains('/'))
        {
            var name = path[(path.LastIndexOf('/') + 1)..];
            if (Regex.IsMatch(name, regex, RegexOptions.IgnoreCase, RegexTimeout))
                return true;
        }

        return false;
    }

    public static bool AnyMatches(IEnumerable<string> patterns, string relativePath)
    {
        foreach (var pattern in patterns)
        {
            if (IsMatch(pattern, relativePath))
                return true;
        }

        return false;
    }

    private static string Normalize(string value) => value.Replace('\\', '/').Trim();

    private static string GlobToRegex(string glob)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < glob.Length; i++)
        {
            var c = glob[i];
            switch (c)
            {
                case '*':
                    if (i + 1 < glob.Length && glob[i + 1] == '*')
                    {
                        i++;
                        if (i + 1 < glob.Length && glob[i + 1] == '/')
                        {
                            i++;
                            sb.Append("(?:.*/)?");
                        }
                        else
                        {
                            sb.Append(".*");
                        }
                    }
                    else
                    {
                        sb.Append("[^/]*");
                    }

                    break;

                case '?':
                    sb.Append("[^/]");
                    break;

                case '[':
                    var close = glob.IndexOf(']', i + 1);
                    if (close < 0)
                    {
                        sb.Append("\\[");
                        break;
                    }

                    var set = glob.Substring(i + 1, close - i - 1);
                    if (set.StartsWith('!'))
                        set = "^" + set[1..];
                    if (set.Length == 0 || set == "^")
                    {
                        sb.Append("\\[\\]");
                        i = close;
                        break;
                    }

                    sb.Append('[').Append(set).Append(']');
                    i = close;
                    break;

                default:
                    sb.Append(Regex.Escape(c.ToString()));
                    break;
            }
        }

        return sb.ToString();
    }
}