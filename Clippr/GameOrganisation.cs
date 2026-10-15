using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace Clippr;

public sealed class GameMapping
{
    public string DisplayName { get; set; } = "";
    public string FolderName { get; set; } = "";
}

internal sealed record GameObservation(string? GameName, string? FolderName, string? CapturedApplicationName);
internal sealed record PreparedGameFolder(string Folder, string? Warning);

internal static class GameOrganisation
{
    private static readonly Dictionary<string, GameMapping> BuiltIn = new(StringComparer.OrdinalIgnoreCase)
    {
        ["deadlock.exe"] = new() { DisplayName = "Deadlock", FolderName = "Deadlock" },
        ["valorant-win64-shipping.exe"] = new() { DisplayName = "Valorant", FolderName = "Valorant" },
        ["cs2.exe"] = new() { DisplayName = "Counter-Strike 2", FolderName = "Counter-Strike 2" },
        ["minecraft.windows.exe"] = new() { DisplayName = "Minecraft", FolderName = "Minecraft" }
    };

    public static IReadOnlyDictionary<string, GameMapping> DefaultMappings => BuiltIn;

    public static string? NormalizeExecutable(string? name)
    {
        name = name?.Trim();
        if (string.IsNullOrWhiteSpace(name) || name.Length > 120 ||
            name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.Contains('/') || name.Contains('\\')) return null;
        if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name += ".exe";
        return name;
    }

    public static string? SafeFolderName(string? name)
    {
        name = name?.Trim().TrimEnd('.', ' ');
        if (string.IsNullOrEmpty(name) || name.Length > 80 || name is "." or ".." ||
            name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            name.Any(char.IsControl) || name.Contains('/') || name.Contains('\\')) return null;
        if (name.Equals("Screenshots", StringComparison.OrdinalIgnoreCase)) return null;
        var first = name.Split('.')[0].TrimEnd(' ');
        if (first.Equals("CON", StringComparison.OrdinalIgnoreCase) || first.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
            first.Equals("AUX", StringComparison.OrdinalIgnoreCase) || first.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
            (first.Length == 4 && (first.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ||
                                   first.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) && first[3] is >= '1' and <= '9')) return null;
        return name;
    }

    public static GameObservation Resolve(string? executable, bool capturedApplication, ClipprSettings settings)
    {
        var normalized = NormalizeExecutable(executable);
        if (normalized is null) return new GameObservation(null, null, null);
        var capturedName = capturedApplication ? Path.GetFileNameWithoutExtension(normalized) : null;
        if (settings.CustomGameMappings.TryGetValue(normalized, out var custom) &&
            SafeFolderName(custom.FolderName) is { } customFolder && !string.IsNullOrWhiteSpace(custom.DisplayName))
            return new GameObservation(custom.DisplayName, customFolder, capturedName);
        if (BuiltIn.TryGetValue(normalized, out var builtIn))
            return new GameObservation(builtIn.DisplayName, builtIn.FolderName, capturedName);
        return new GameObservation(null, null, capturedName);
    }

    public static string DestinationFolder(ClipprSettings settings, string? gameFolder, string? capturedApplication)
    {
        var root = Path.GetFullPath(settings.ClipFolder);
        if (!settings.OrganizeClipsByGame) return root;
        var safe = SafeFolderName(gameFolder);
        if (safe is null && settings.UnknownGameDestination == "CapturedApplication")
            safe = SafeFolderName(capturedApplication);
        if (safe is null && settings.UnknownGameDestination != "Root") safe = "Other";
        return safe is null ? root : Path.Combine(root, safe);
    }

    public static PreparedGameFolder PrepareFolder(ClipprSettings settings, GameObservation observation, string filenameStem)
    {
        var root = Path.GetFullPath(settings.ClipFolder);
        var destination = DestinationFolder(settings, observation.FolderName, observation.CapturedApplicationName);
        if (string.Equals(destination, root, StringComparison.OrdinalIgnoreCase)) return new PreparedGameFolder(root, null);
        if (destination.Length + filenameStem.Length + 45 > 240)
            return new PreparedGameFolder(root, "The game folder path was too long, so this clip was saved in the main Clips folder.");
        try
        {
            Directory.CreateDirectory(destination);
            if ((File.GetAttributes(destination) & FileAttributes.ReparsePoint) != 0)
                return new PreparedGameFolder(root, "The game folder redirects to another location, so this clip was saved in the main Clips folder.");
            var probePath = Path.Combine(destination, $".ClipDat-write-probe-{Guid.NewGuid():N}");
            using (new FileStream(probePath, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 1, FileOptions.DeleteOnClose)) { }
            return new PreparedGameFolder(destination, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return new PreparedGameFolder(root, "The game folder was unavailable, so this clip was saved in the main Clips folder.");
        }
    }

    public static string? ReadProcessName(uint pid)
    {
        if (pid == 0 || pid == (uint)Environment.ProcessId) return null;
        try
        {
            using var process = Process.GetProcessById((int)pid);
            return NormalizeExecutable(process.ProcessName);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        { return null; }
    }

    [DllImport("user32.dll", ExactSpelling = true)] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", ExactSpelling = true)] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
    [DllImport("user32.dll", ExactSpelling = true)] private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);

    public static string? ForegroundExecutable(IntPtr selectedMonitor)
    {
        var window = GetForegroundWindow();
        if (window == IntPtr.Zero || selectedMonitor == IntPtr.Zero || MonitorFromWindow(window, 0) != selectedMonitor) return null;
        GetWindowThreadProcessId(window, out var pid);
        return ReadProcessName(pid);
    }

    public static string? CapturedExecutable(IntPtr capturedWindow)
    {
        if (capturedWindow == IntPtr.Zero) return null;
        GetWindowThreadProcessId(capturedWindow, out var pid);
        return ReadProcessName(pid);
    }

    public static GameObservation Sample(ClipprSettings settings, bool applicationCapture,
        IntPtr capturedWindow, IntPtr selectedMonitor)
    {
        // Application capture always follows its explicit target, even when another game has focus.
        var executable = applicationCapture ? CapturedExecutable(capturedWindow) :
            settings.GameDetectionMethod == "CapturedApplication" ? null : ForegroundExecutable(selectedMonitor);
        return Resolve(executable, applicationCapture, settings);
    }
}

internal sealed class GameReplayTimeline
{
    private readonly Queue<(DateTimeOffset At, GameObservation Observation)> _samples = new();
    private readonly object _gate = new();

    public void Clear() { lock (_gate) _samples.Clear(); }

    public void Record(DateTimeOffset at, GameObservation observation)
    {
        lock (_gate)
        {
            _samples.Enqueue((at, observation));
            while (_samples.Count > 0 && at - _samples.Peek().At > TimeSpan.FromMinutes(61)) _samples.Dequeue();
        }
    }

    public GameObservation Dominant(DateTimeOffset end, TimeSpan duration)
    {
        lock (_gate)
        {
            var samples = _samples.Where(sample => sample.At > end - duration && sample.At <= end).ToArray();
            if (samples.Length == 0) return new GameObservation(null, null, null);
            var captured = samples.Select(sample => sample.Observation.CapturedApplicationName)
                .Where(name => name is not null).GroupBy(name => name, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(group => group.Count()).FirstOrDefault();
            var games = samples.Where(sample => sample.Observation.FolderName is not null)
                .GroupBy(sample => sample.Observation.FolderName!, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(group => group.Count()).ToArray();
            var minimumSamples = Math.Max(1, (int)Math.Ceiling(duration.TotalSeconds * 0.5));
            var capturedName = samples.Length >= minimumSamples && captured is not null &&
                captured.Count() > samples.Length * 0.6 ? captured.Key : null;
            if (samples.Length < minimumSamples || games.Length == 0 || games[0].Count() <= samples.Length * 0.6)
                return new GameObservation(null, null, capturedName);
            var winner = games[0].First().Observation;
            return winner with { CapturedApplicationName = capturedName };
        }
    }

    public GameObservation Latest(DateTimeOffset now)
    {
        lock (_gate)
            return _samples.Count > 0 && now - _samples.Last().At <= TimeSpan.FromSeconds(3)
                ? _samples.Last().Observation : new GameObservation(null, null, null);
    }
}
