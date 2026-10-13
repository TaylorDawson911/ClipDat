using System;
using System.IO;
using System.Linq;
using System.Text;

namespace Clippr;

internal static class LocalReliabilityLog
{
    private const long MaxFileBytes = 512 * 1024;
    private static readonly object Gate = new();
    private static readonly string Folder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Clippr", "Logs");
    private static readonly string CurrentPath = Path.Combine(Folder, "reliability.log");

    public static void Error(string component, int hresult, string? detail = null)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Folder);
                if (File.Exists(CurrentPath) && new FileInfo(CurrentPath).Length >= MaxFileBytes)
                {
                    Rotate(Path.Combine(Folder, "reliability.2.log"), Path.Combine(Folder, "reliability.3.log"));
                    Rotate(Path.Combine(Folder, "reliability.1.log"), Path.Combine(Folder, "reliability.2.log"));
                    File.Move(CurrentPath, Path.Combine(Folder, "reliability.1.log"), true);
                }
                var safeDetail = (detail ?? "").Replace('\r', ' ').Replace('\n', ' ');
                if (safeDetail.Length > 180) safeDetail = safeDetail[..180];
                var line = $"{DateTimeOffset.UtcNow:O}\t{Sanitize(component)}\t0x{hresult:X8}\t{safeDetail}{Environment.NewLine}";
                File.AppendAllText(CurrentPath, line, new UTF8Encoding(false));
            }
        }
        catch { }
    }

    public static string ReadRecent(int count = 5)
    {
        try
        {
            lock (Gate)
            {
                if (!File.Exists(CurrentPath)) return "";
                return string.Join(Environment.NewLine, File.ReadLines(CurrentPath).TakeLast(count)
                    .Select(line => line.Replace('\t', ' ')));
            }
        }
        catch { return "Recent local errors could not be read."; }
    }

    private static void Rotate(string source, string destination)
    {
        if (File.Exists(destination)) File.Delete(destination);
        if (File.Exists(source)) File.Move(source, destination);
    }

    private static string Sanitize(string value) => new(value.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_').Take(32).ToArray());
}
