using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace AltiumMcp.Contracts.Model;

/// <summary>
/// Normalises agent-supplied inputs before they reach any Altium API. LLM clients regularly send
/// <c>"None"</c>, <c>"null"</c>, <c>""</c> or <c>"undefined"</c> for an omitted optional argument; passing such a
/// string to <c>LoadSchDocumentByPath</c> made Altium show a modal "file None not found" dialog (observed live).
/// Pure, no Altium dependency — unit-tested.
/// </summary>
public static class InputNormalizer
{
    private static readonly HashSet<string> Placeholders = new(StringComparer.OrdinalIgnoreCase)
    {
        "none", "null", "nil", "undefined", "nan", "n/a", "na", "-", "(null)", "<none>", "[none]", "string",
    };

    /// <summary>Returns the trimmed value, or null when the value is absent or a placeholder for "absent".</summary>
    public static string? Optional(string? value)
    {
        if (value == null)
        {
            return null;
        }

        string t = value.Trim().Trim('"', '\'');
        return t.Length == 0 || Placeholders.Contains(t) ? null : t;
    }

    /// <summary>Optional list: drops null/placeholder/blank entries, trims, de-duplicates case-insensitively.</summary>
    public static List<string> OptionalList(IEnumerable<string?>? values) =>
        (values ?? Enumerable.Empty<string?>())
            .Select(Optional)
            .Where(v => v != null)
            .Select(v => v!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>
    /// Validates a file path argument: absent/placeholder → null; otherwise it must look like a file path
    /// (no invalid characters, a file name with an extension). Returns the full path (relative paths are resolved
    /// against <paramref name="baseDirectory"/> when given). Throws <see cref="ArgumentException"/> with an
    /// agent-readable message for malformed values — the caller maps it to INVALID_ARGUMENT.
    /// </summary>
    public static string? FilePath(string? value, string? baseDirectory = null)
    {
        string? v = Optional(value);
        if (v == null)
        {
            return null;
        }

        if (v.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
        {
            throw new ArgumentException($"Path contains invalid characters: '{v}'.");
        }

        string full;
        try
        {
            full = Path.IsPathRooted(v) || baseDirectory == null ? Path.GetFullPath(v) : Path.GetFullPath(Path.Combine(baseDirectory, v));
        }
        catch (Exception ex)
        {
            throw new ArgumentException($"Not a valid path: '{v}' ({ex.Message}).");
        }

        if (string.IsNullOrEmpty(Path.GetFileName(full)))
        {
            throw new ArgumentException($"Path has no file name: '{v}'.");
        }

        return full;
    }

    /// <summary>True when the value is just a file name (no directory part), e.g. "Bluetooth.SchDoc".</summary>
    public static bool IsBareFileName(string value) =>
        value.IndexOfAny(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar, ':' }) < 0;

    /// <summary>Clamps a page size: non-positive → default, upper bound → max.</summary>
    public static int Limit(int requested, int defaultValue, int max) => Math.Clamp(requested <= 0 ? defaultValue : requested, 1, max);
}
