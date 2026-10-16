using System.Text.Json.Nodes;

namespace ClipDat.Setup;

internal enum SetupMode { Express, BareBones, Custom }

internal sealed record SetupChoices(
    SetupMode Mode, string ClipFolder, bool KeepExisting,
    bool Library, bool Screenshots, bool Voice, bool QuickShare, bool GameFolders,
    bool DesktopAudio, bool MicrophoneAudio, bool StartWithWindows, bool DesktopShortcut,
    bool MinimizeToTray, bool SaveSound, string NotificationStyle,
    int ClipLengthSeconds, string QualityPreset, string VideoFormat, uint FrameRate, string Encoder);

internal static class SetupSettings
{
    internal static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Clippr", "settings.json");

    internal static string? ExistingClipFolder()
    {
        try { return JsonNode.Parse(File.ReadAllText(SettingsPath))?["clipFolder"]?.GetValue<string>(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException)
        { return null; }
    }

    internal static JsonObject Build(SetupChoices choices, JsonObject? existing)
    {
        if (choices.KeepExisting && existing is not null)
        {
            var kept = (JsonObject)existing.DeepClone();
            kept["clipFolder"] = Path.GetFullPath(choices.ClipFolder);
            return kept;
        }

        var settings = new JsonObject
        {
            ["schemaVersion"] = 2,
            ["clipFolder"] = Path.GetFullPath(choices.ClipFolder),
            ["captureMode"] = "Screen",
            ["startCaptureAutomatically"] = true,
            ["quickShareAutoUpload"] = false,
            ["replayMemoryLimitMb"] = 128
        };
        if (choices.Mode == SetupMode.Express)
        {
            settings["libraryEnabled"] = true;
            settings["screenshotsEnabled"] = true;
            settings["voiceCommandsEnabled"] = false;
            settings["quickShareEnabled"] = true;
            settings["organizeClipsByGame"] = true;
            settings["desktopAudioEnabled"] = true;
            settings["microphoneAudioEnabled"] = false;
            settings["notificationStyle"] = "Windows";
            settings["startWithWindows"] = false;
            settings["clipLengthSeconds"] = 30;
            settings["qualityPreset"] = "Standard";
            settings["videoFormat"] = "mp4";
            settings["frameRate"] = 30;
            settings["encoder"] = "MediaFoundationHardware";
        }
        else if (choices.Mode == SetupMode.BareBones)
        {
            settings["libraryEnabled"] = false;
            settings["screenshotsEnabled"] = false;
            settings["voiceCommandsEnabled"] = false;
            settings["quickShareEnabled"] = false;
            settings["organizeClipsByGame"] = false;
            settings["desktopAudioEnabled"] = true;
            settings["microphoneAudioEnabled"] = false;
            settings["notificationStyle"] = "None";
            settings["startWithWindows"] = false;
            settings["saveNotificationSound"] = false;
            settings["screenshotNotificationSound"] = false;
            settings["clipLengthSeconds"] = 30;
            settings["qualityPreset"] = "Standard";
            settings["videoFormat"] = "mp4";
            settings["frameRate"] = 30;
            settings["encoder"] = "MediaFoundationHardware";
        }
        else
        {
            settings["libraryEnabled"] = choices.Library;
            settings["screenshotsEnabled"] = choices.Screenshots;
            settings["voiceCommandsEnabled"] = choices.Voice;
            settings["quickShareEnabled"] = choices.QuickShare;
            settings["organizeClipsByGame"] = choices.GameFolders;
            settings["desktopAudioEnabled"] = choices.DesktopAudio;
            settings["microphoneAudioEnabled"] = choices.MicrophoneAudio;
            settings["notificationStyle"] = choices.NotificationStyle;
            settings["startWithWindows"] = choices.StartWithWindows;
            settings["saveNotificationSound"] = choices.SaveSound;
            settings["clipLengthSeconds"] = choices.ClipLengthSeconds;
            settings["qualityPreset"] = choices.QualityPreset;
            settings["videoFormat"] = choices.VideoFormat;
            settings["frameRate"] = choices.FrameRate;
            settings["encoder"] = choices.Encoder;
        }
        settings["minimizeToTrayOnClose"] = choices.MinimizeToTray;
        return settings;
    }

    internal static void Save(SetupChoices choices)
    {
        JsonObject? existing = null;
        if (choices.KeepExisting && File.Exists(SettingsPath))
        {
            try { existing = JsonNode.Parse(File.ReadAllText(SettingsPath)) as JsonObject; }
            catch (System.Text.Json.JsonException) { throw new InvalidDataException("Existing settings could not be read. Turn off Keep current settings to replace them."); }
        }
        var settings = Build(choices, existing);
        var parent = Path.GetDirectoryName(SettingsPath)!;
        Directory.CreateDirectory(parent);
        var temporary = SettingsPath + ".setup-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temporary, settings.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            if (File.Exists(SettingsPath)) File.Copy(SettingsPath, SettingsPath + ".bak", overwrite: true);
            File.Move(temporary, SettingsPath, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
