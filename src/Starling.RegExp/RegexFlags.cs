// SPDX-License-Identifier: Apache-2.0

namespace Starling.RegExp;

/// <summary>ES2024 RegExp flag bits per §22.2.1.1.</summary>
[System.Flags]
public enum RegexFlags
{
    None = 0,
    Global = 1 << 0,         // g
    IgnoreCase = 1 << 1,     // i
    Multiline = 1 << 2,      // m
    DotAll = 1 << 3,         // s
    Unicode = 1 << 4,        // u
    UnicodeSets = 1 << 5,    // v
    Sticky = 1 << 6,         // y
    HasIndices = 1 << 7,     // d
}

public static class RegexFlagParser
{
    /// <summary>Parse a JS-source flag string (e.g. "gim") into bits. Rejects
    /// unknown flags or duplicates with a non-null error string.</summary>
    public static bool TryParse(string s, out RegexFlags flags, out string? error)
    {
        flags = RegexFlags.None;
        error = null;
        if (string.IsNullOrEmpty(s))
        {
            return true;
        }

        foreach (var c in s)
        {
            RegexFlags bit = c switch
            {
                'g' => RegexFlags.Global,
                'i' => RegexFlags.IgnoreCase,
                'm' => RegexFlags.Multiline,
                's' => RegexFlags.DotAll,
                'u' => RegexFlags.Unicode,
                'v' => RegexFlags.UnicodeSets,
                'y' => RegexFlags.Sticky,
                'd' => RegexFlags.HasIndices,
                _ => RegexFlags.None,
            };
            if (bit == RegexFlags.None)
            {
                error = $"Invalid regular expression flag '{c}'";
                return false;
            }
            if ((flags & bit) != 0)
            {
                error = $"Duplicate regular expression flag '{c}'";
                return false;
            }
            flags |= bit;
        }
        if ((flags & RegexFlags.Unicode) != 0 && (flags & RegexFlags.UnicodeSets) != 0)
        {
            error = "u and v flags are mutually exclusive";
            return false;
        }
        return true;
    }

    public static string ToFlagString(RegexFlags flags)
    {
        var sb = new System.Text.StringBuilder();
        // Spec §22.2.6.4 ordering: d, g, i, m, s, u, v, y
        if ((flags & RegexFlags.HasIndices) != 0)
        {
            sb.Append('d');
        }

        if ((flags & RegexFlags.Global) != 0)
        {
            sb.Append('g');
        }

        if ((flags & RegexFlags.IgnoreCase) != 0)
        {
            sb.Append('i');
        }

        if ((flags & RegexFlags.Multiline) != 0)
        {
            sb.Append('m');
        }

        if ((flags & RegexFlags.DotAll) != 0)
        {
            sb.Append('s');
        }

        if ((flags & RegexFlags.Unicode) != 0)
        {
            sb.Append('u');
        }

        if ((flags & RegexFlags.UnicodeSets) != 0)
        {
            sb.Append('v');
        }

        if ((flags & RegexFlags.Sticky) != 0)
        {
            sb.Append('y');
        }

        return sb.ToString();
    }
}
