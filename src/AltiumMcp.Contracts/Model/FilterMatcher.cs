using System;
using System.Text.RegularExpressions;

namespace AltiumMcp.Contracts.Model;

/// <summary>
/// Filter semantics shared by all list tools:
/// plain text = case-insensitive substring; text containing '*' or '?' = wildcard anchored to the whole field.
/// </summary>
public static class FilterMatcher
{
    public static bool Matches(string? filter, params string?[] fields)
    {
        if (string.IsNullOrWhiteSpace(filter))
        {
            return true;
        }

        Regex? wildcard = null;
        if (filter.IndexOfAny(new[] { '*', '?' }) >= 0)
        {
            string pattern = "^" + Regex.Escape(filter).Replace("\\*", ".*").Replace("\\?", ".") + "$";
            wildcard = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        foreach (string? f in fields)
        {
            if (string.IsNullOrEmpty(f))
            {
                continue;
            }

            if (wildcard != null ? wildcard.IsMatch(f) : f.Contains(filter, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
