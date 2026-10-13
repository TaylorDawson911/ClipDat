using System;
using System.Collections.Generic;
using System.Linq;

namespace Clippr;

internal sealed record VoiceCommandDefinition(
    string Key, string Action, string DefaultPhrase, string Description, string Category);

internal static class VoiceCommandCatalog
{
    internal static readonly IReadOnlyList<VoiceCommandDefinition> Commands =
    [
        new("clip-that", "save-configured", "Clip that", "Save the configured replay length", "Clipping"),
        new("save-clip", "save-configured", "Save clip", "Save the configured replay length", "Clipping"),
        new("save-30", "save-30", "Clip last 30 seconds", "Save the last 30 seconds", "Clipping"),
        new("save-60", "save-60", "Clip last 60 seconds", "Save the last 60 seconds", "Clipping"),
        new("screenshot", "screenshot", "Take screenshot", "Capture a screenshot", "Clipping"),
        new("start", "start", "Start recording", "Start clipping your screen and audio", "Recording"),
        new("stop", "stop", "Stop recording", "Stop clipping", "Recording"),
        new("open", "open", "Open ClipDat", "Bring ClipDat to the front", "General"),
        new("settings", "settings", "Show settings", "Open ClipDat settings", "General")
    ];

    internal static Dictionary<string, string> Defaults() =>
        Commands.ToDictionary(command => command.Key, command => command.DefaultPhrase, StringComparer.Ordinal);

    internal static string Normalize(string? phrase) => string.Join(' ',
        (phrase ?? "").Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    internal static bool IsValid(string? phrase)
    {
        var normalized = Normalize(phrase);
        return normalized.Length is > 0 and <= 60 &&
               normalized.All(character => char.IsLetterOrDigit(character) || character == ' ');
    }
}
