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
        new("screenshot", "screenshot", "Take screenshot", "Capture a screenshot", "Clipping"),
        new("start", "start", "Start recording", "Start clipping your screen and audio", "Recording"),
        new("stop", "stop", "Stop recording", "Stop clipping", "Recording")
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
