using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace Clippr;

internal static class LibraryLauncher
{
    internal static void Open(string clipFolder)
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\ClipDat\Library");
        var executable = key?.GetValue("Executable") as string;
        if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable))
            throw new FileNotFoundException("Clip Library is not installed. Install the optional ClipDat Library application first.");
        if (string.IsNullOrWhiteSpace(clipFolder) || !Directory.Exists(clipFolder))
            throw new DirectoryNotFoundException("The configured clips folder is unavailable.");
        var start = new ProcessStartInfo(executable) { UseShellExecute = false };
        start.ArgumentList.Add("--folder");
        start.ArgumentList.Add(Path.GetFullPath(clipFolder));
        if (Process.Start(start) is null) throw new Win32Exception("Windows could not open Clip Library.");
    }
}
