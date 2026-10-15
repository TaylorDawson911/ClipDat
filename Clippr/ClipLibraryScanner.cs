using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace Clippr;

internal static class ClipLibraryScanner
{
    public static List<FileInfo> Scan(string folder, Func<string, bool> supported, CancellationToken token)
    {
        var files = new List<FileInfo>();
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint
        };
        foreach (var path in Directory.EnumerateFiles(folder, "*", options))
        {
            token.ThrowIfCancellationRequested();
            if (!supported(path)) continue;
            try
            {
                var info = new FileInfo(path);
                if (info.Length > 0) files.Add(info);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return files;
    }
}
