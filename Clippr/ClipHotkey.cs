using System;
using System.Text.RegularExpressions;

namespace Clippr;

internal static partial class ClipHotkey
{
    [GeneratedRegex(@"^Ctrl\+(Alt|Shift)\+F(1[0-2]|[1-9])$", RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();

    internal static bool TryParse(string? value, out uint modifiers, out uint virtualKey)
    {
        modifiers = virtualKey = 0;
        var match = Pattern().Match(value ?? "");
        if (!match.Success) return false;
        modifiers = 2u | (match.Groups[1].Value == "Alt" ? 1u : 4u);
        virtualKey = (uint)(0x6F + int.Parse(match.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture));
        return true;
    }
}
