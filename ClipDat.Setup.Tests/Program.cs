using System.Text.Json.Nodes;
using ClipDat.Setup;

var folder = Path.Combine(Path.GetTempPath(), "ClipDat setup choices");
SetupChoices Choices(SetupMode mode, bool keep = false) => new(
    mode, folder, keep, true, true, true, true, true,
    true, true, true, true, true, true, "ClipDat", 60, "High", "mkv", 60, "MediaFoundationSoftware");

var express = SetupSettings.Build(Choices(SetupMode.Express), null);
Require(express["libraryEnabled"]?.GetValue<bool>() == true &&
        express["screenshotsEnabled"]?.GetValue<bool>() == true &&
        express["voiceCommandsEnabled"]?.GetValue<bool>() == false &&
        express["clipLengthSeconds"]?.GetValue<int>() == 30,
    "Express should apply the documented defaults");

var bareBones = SetupSettings.Build(Choices(SetupMode.BareBones), null);
Require(bareBones["libraryEnabled"]?.GetValue<bool>() == false &&
        bareBones["screenshotsEnabled"]?.GetValue<bool>() == false &&
        bareBones["quickShareEnabled"]?.GetValue<bool>() == false &&
        bareBones["voiceCommandsEnabled"]?.GetValue<bool>() == false &&
        bareBones["desktopAudioEnabled"]?.GetValue<bool>() == true &&
        bareBones["notificationStyle"]?.GetValue<string>() == "None",
    "BareBones should keep capture and turn optional features off");

var custom = SetupSettings.Build(Choices(SetupMode.Custom), null);
Require(custom["clipLengthSeconds"]?.GetValue<int>() == 60 &&
        custom["videoFormat"]?.GetValue<string>() == "mkv" &&
        custom["encoder"]?.GetValue<string>() == "MediaFoundationSoftware" &&
        custom["startWithWindows"]?.GetValue<bool>() == true,
    "Custom should apply chosen recording and startup options");

var previous = new JsonObject { ["qualityPreset"] = "High", ["libraryEnabled"] = false,
    ["clipFolder"] = "C:\\OldClips" };
var kept = SetupSettings.Build(Choices(SetupMode.Express, keep: true), previous);
Require(kept["qualityPreset"]?.GetValue<string>() == "High" &&
        kept["libraryEnabled"]?.GetValue<bool>() == false &&
        kept["clipFolder"]?.GetValue<string>() == Path.GetFullPath(folder),
    "Upgrade preserve option should change only the clips folder");
Require(previous["clipFolder"]?.GetValue<string>() == "C:\\OldClips", "Build must not change the existing settings object");
Console.WriteLine("Setup mode and upgrade checks passed.");

static void Require(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}
