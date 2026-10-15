using System;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace Clippr;

internal static class ClipFilename
{
    internal static string SourceName(ClipprSettings settings)
    {
        if (settings.CaptureMode != "Application" || settings.PreferredApplicationProcessId == 0) return "ClipDat";
        try
        {
            using var process = Process.GetProcessById(checked((int)settings.PreferredApplicationProcessId));
            return SafePart(process.ProcessName);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or OverflowException)
        {
            return "ClipDat";
        }
    }

    internal static string SafePart(string value)
    {
        var clean = new string(value.Where(c => !Path.GetInvalidFileNameChars().Contains(c) && !char.IsControl(c)).ToArray()).Trim(' ', '.');
        return string.IsNullOrWhiteSpace(clean) ? "ClipDat" : clean.Length > 64 ? clean[..64].TrimEnd(' ', '.') : clean;
    }

    internal static string Stem(ClipprSettings settings, DateTime time)
    {
        var stamp = time.ToString("yyyy-MM-dd HH-mm-ss-fff", System.Globalization.CultureInfo.InvariantCulture);
        return settings.FilenameFormat switch
        {
            "Timestamp" => stamp,
            "Application" => $"{SourceName(settings)} - {stamp}",
            _ => $"ClipDat - {stamp}"
        };
    }
}
