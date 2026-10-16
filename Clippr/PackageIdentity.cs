using System;
using System.IO;
using Windows.ApplicationModel;

namespace Clippr;

internal static class PackageIdentity
{
    private const string PackagePrefix = "ce842948-d72b-4d57-ae7b-474f481f442e_*";
    internal static bool IsPackaged { get; } = Detect();

    internal static void MigrateUserData()
    {
        if (IsPackaged) return;
        try
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var packages = Path.Combine(local, "Packages");
            if (!Directory.Exists(packages)) return;
            foreach (var package in Directory.EnumerateDirectories(packages, PackagePrefix))
            {
                CopyIfMissing(Path.Combine(package, "LocalCache", "Local", "Clippr", "settings.json"),
                    Path.Combine(local, "Clippr", "settings.json"));
                CopyIfMissing(Path.Combine(package, "LocalCache", "Local", "ClipDat", "share-history.json"),
                    Path.Combine(local, "ClipDat", "share-history.json"));
            }
        }
        catch (Exception ex) { LocalReliabilityLog.Error("unpackaged-migration", ex.HResult, ex.Message); }
    }

    private static void CopyIfMissing(string source, string destination)
    {
        if (!File.Exists(source) || File.Exists(destination)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Copy(source, destination, overwrite: false);
    }

    private static bool Detect()
    {
        try { return !string.IsNullOrEmpty(Package.Current.Id.FullName); }
        catch (InvalidOperationException) { return false; }
        catch (System.Runtime.InteropServices.COMException) { return false; }
    }
}
