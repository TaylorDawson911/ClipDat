using Clippr;

var settings = new ClipprSettings { ClipFolder = Path.Combine(Path.GetTempPath(), "ClipDatGameTests") };
Require(GameOrganisation.Resolve("deadlock.exe", false, settings).GameName == "Deadlock", "Deadlock mapping");
Require(GameOrganisation.Resolve("VALORANT-Win64-Shipping.exe", false, settings).GameName == "Valorant", "Valorant mapping");
Require(GameOrganisation.Resolve("cs2.exe", false, settings).FolderName == "Counter-Strike 2", "Counter-Strike mapping");
Require(GameOrganisation.Resolve("chrome.exe", false, settings).GameName is null, "browser is not guessed to be a game");
Require(GameOrganisation.Resolve("Discord.exe", false, settings).GameName is null, "Discord is not guessed to be a game");

settings.CustomGameMappings["deadlock.exe"] = new GameMapping { DisplayName = "My Deadlock", FolderName = "My Deadlock" };
Require(GameOrganisation.Resolve("deadlock.exe", false, settings).FolderName == "My Deadlock", "custom mapping overrides built-in");
Require(GameOrganisation.SafeFolderName("../escape") is null, "path traversal folder rejected");
Require(GameOrganisation.SafeFolderName("CON.txt") is null, "reserved Windows folder rejected");
Require(GameOrganisation.SafeFolderName("LPT1") is null, "reserved port folder rejected");
Require(GameOrganisation.NormalizeExecutable("C:\\games\\deadlock.exe") is null, "executable path rejected");

var root = Path.GetFullPath(settings.ClipFolder);
Require(GameOrganisation.DestinationFolder(settings, "Deadlock", null) == Path.Combine(root, "Deadlock"), "known game destination");
Require(GameOrganisation.DestinationFolder(settings, null, null) == Path.Combine(root, "Other"), "unknown game destination");
settings.UnknownGameDestination = "CapturedApplication";
Require(GameOrganisation.DestinationFolder(settings, null, "My Game") == Path.Combine(root, "My Game"), "captured application fallback");
settings.UnknownGameDestination = "Root";
Require(GameOrganisation.DestinationFolder(settings, null, null) == root, "root fallback");
settings.OrganizeClipsByGame = false;
Require(GameOrganisation.DestinationFolder(settings, "Deadlock", null) == root, "disabled organisation stays flat");
settings.ClipFolder = Path.Combine(AppContext.BaseDirectory, "organize-tests");
Directory.CreateDirectory(settings.ClipFolder);
settings.OrganizeClipsByGame = true;
var prepared = GameOrganisation.PrepareFolder(settings,
    new GameObservation("Deadlock", "Deadlock", null), "ClipDat-2026-10-15");
Require(prepared.Warning is null && Directory.Exists(prepared.Folder), "game folder is created");
var occupiedName = Path.Combine(settings.ClipFolder, "Valorant");
await File.WriteAllBytesAsync(occupiedName, [1]);
var fallback = GameOrganisation.PrepareFolder(settings,
    new GameObservation("Valorant", "Valorant", null), "ClipDat-2026-10-15");
Require(fallback.Folder == settings.ClipFolder && fallback.Warning is not null,
    "unavailable game folder safely falls back to clips root");

var end = DateTimeOffset.UtcNow;
var timeline = new GameReplayTimeline();
for (var i = 29; i >= 0; i--)
    timeline.Record(end.AddSeconds(-i), i >= 10
        ? new GameObservation("Deadlock", "Deadlock", null)
        : new GameObservation("Valorant", "Valorant", null));
Require(timeline.Dominant(end, TimeSpan.FromSeconds(30)).FolderName == "Deadlock", "dominant game wins replay interval");
timeline.Clear();
for (var i = 29; i >= 0; i--)
    timeline.Record(end.AddSeconds(-i), i >= 15
        ? new GameObservation("Deadlock", "Deadlock", null)
        : new GameObservation("Valorant", "Valorant", null));
Require(timeline.Dominant(end, TimeSpan.FromSeconds(30)).FolderName is null, "mixed replay is ambiguous");
timeline.Clear();
timeline.Record(end, new GameObservation("Deadlock", "Deadlock", null));
Require(timeline.Dominant(end, TimeSpan.FromSeconds(30)).FolderName is null, "one late sample cannot label full replay");
timeline.Clear();
for (var i = 29; i >= 0; i--)
    timeline.Record(end.AddSeconds(-i), new GameObservation(null, null, i >= 15 ? "App A" : "App B"));
Require(timeline.Dominant(end, TimeSpan.FromSeconds(30)).CapturedApplicationName is null,
    "mixed captured applications do not get a guessed fallback");

var libraryRoot = Path.Combine(AppContext.BaseDirectory, "scan-tests");
Directory.CreateDirectory(Path.Combine(libraryRoot, "Deadlock"));
Directory.CreateDirectory(Path.Combine(libraryRoot, "Screenshots"));
await File.WriteAllBytesAsync(Path.Combine(libraryRoot, "Deadlock", "test.mp4"), [1, 2]);
await File.WriteAllBytesAsync(Path.Combine(libraryRoot, "Screenshots", "test.png"), [3]);
await File.WriteAllBytesAsync(Path.Combine(libraryRoot, "Deadlock", "partial.tmp"), [4]);
var discovered = ClipLibraryScanner.Scan(libraryRoot,
    path => Path.GetExtension(path) is ".mp4" or ".png", CancellationToken.None);
Require(discovered.Count == 2 && discovered.Any(file => file.Directory?.Name == "Deadlock") &&
        discovered.Any(file => file.Directory?.Name == "Screenshots"), "nested clips and screenshots are discovered without temporary files");

Console.WriteLine("Game organisation checks passed.");

static void Require(bool condition, string description)
{
    if (!condition) throw new Exception($"Failed: {description}");
}

namespace Clippr
{
    public sealed class ClipprSettings
    {
        public string ClipFolder { get; set; } = "";
        public bool OrganizeClipsByGame { get; set; } = true;
        public string GameDetectionMethod { get; set; } = "Automatic";
        public string UnknownGameDestination { get; set; } = "Other";
        public Dictionary<string, GameMapping> CustomGameMappings { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }
}
