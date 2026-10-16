using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Clippr;

public sealed class ShareHistory
{
    private static readonly object Gate = new();
    private readonly string _filePath;
    public ShareHistory(string? filePath = null) => _filePath = filePath ?? DefaultPath();

    private static string DefaultPath()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var packages = Path.Combine(local, "Packages");
        if (PackageIdentity.IsPackaged && Directory.Exists(packages))
        {
            var core = Directory.EnumerateDirectories(packages, "ce842948-d72b-4d57-ae7b-474f481f442e_*")
                .FirstOrDefault();
            if (core is not null) return Path.Combine(core, "LocalCache", "Local", "ClipDat", "share-history.json");
        }
        return Path.Combine(local, "ClipDat", "share-history.json");
    }

    public ShareUpload? FindCurrent(string path)
    {
        var file = new FileInfo(Path.GetFullPath(path));
        if (!file.Exists) return null;
        lock (Gate) return Read().LastOrDefault(item =>
            string.Equals(item.FilePath, file.FullName, StringComparison.OrdinalIgnoreCase) &&
            item.FileSize == file.Length && item.ModifiedUtcTicks == file.LastWriteTimeUtc.Ticks &&
            AnonMp4ShareService.IsValidWatchUrl(item.WatchUrl, item.VideoId));
    }

    public void Save(ShareUpload upload)
    {
        lock (Gate)
        {
            var list = Read();
            list.Add(upload);
            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
            var temporary = _filePath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(list));
            File.Move(temporary, _filePath, true);
        }
    }

    private List<ShareUpload> Read()
    {
        if (!File.Exists(_filePath)) return [];
        try { return JsonSerializer.Deserialize<List<ShareUpload>>(File.ReadAllText(_filePath)) ?? []; }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException) { return []; }
    }
}
