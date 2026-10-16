using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Windows.Storage;

namespace Clippr;

public sealed class ClipprSettings
{
    private const int CurrentSchemaVersion = 2;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.Strict
    };
    private static readonly ClipprSettingsJsonContext JsonContext = new(JsonOptions);

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public string CaptureMode { get; set; } = "Screen";
    public string PreferredDisplay { get; set; } = "";
    public uint PreferredApplicationProcessId { get; set; }
    public int ClipLengthSeconds { get; set; } = 30;
    public string Resolution { get; set; } = "Source";
    public uint FrameRate { get; set; } = 30;
    public string Encoder { get; set; } = "MediaFoundationHardware";
    public string QualityPreset { get; set; } = "Standard";
    public string VideoFormat { get; set; } = "mp4";
    public bool DesktopAudioEnabled { get; set; } = true;
    public bool MicrophoneAudioEnabled { get; set; }
    public string AudioOutputId { get; set; } = "";
    public string AudioInputId { get; set; } = "";
    public uint GameAudioProcessId { get; set; }
    public uint DiscordAudioProcessId { get; set; }
    public uint MusicAudioProcessId { get; set; }
    public int DesktopVolume { get; set; } = 100;
    public int MicrophoneVolume { get; set; } = 100;
    public int GameVolume { get; set; } = 100;
    public int ChatVolume { get; set; } = 100;
    public int MusicVolume { get; set; } = 100;
    public bool DesktopMuted { get; set; }
    public bool MicrophoneMuted { get; set; }
    public bool GameMuted { get; set; }
    public bool ChatMuted { get; set; }
    public bool MusicMuted { get; set; }
    public string ClipFolder { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "ClipDat");
    public bool OrganizeClipsByGame { get; set; } = true;
    public string GameDetectionMethod { get; set; } = "Automatic";
    public string UnknownGameDestination { get; set; } = "Other";
    public Dictionary<string, GameMapping> CustomGameMappings { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string FilenameFormat { get; set; } = "ClipDat";
    public string Hotkey { get; set; } = "Ctrl+Shift+F10";
    public string ScreenshotHotkey { get; set; } = "Ctrl+Alt+F10";
    public bool ScreenshotsEnabled { get; set; } = true;
    public bool QuickShareEnabled { get; set; } = true;
    public string QuickShareMethod { get; set; } = "AnonMP4";
    public bool QuickShareAutoUpload { get; set; }
    public string QuickShareFailureAction { get; set; } = "CopyFile";
    public bool QuickShareAutoCopyLink { get; set; } = true;
    public bool QuickShareOpenAfterUpload { get; set; }
    public bool QuickShareShowProgress { get; set; } = true;
    public bool QuickShareShowNotification { get; set; } = true;
    public bool QuickShareKeepHistory { get; set; } = true;
    public string ScreenshotFormat { get; set; } = "png";
    public int ScreenshotJpegQuality { get; set; } = 90;
    public bool CopyScreenshotToClipboard { get; set; }
    public bool ScreenshotNotificationSound { get; set; }
    public bool SaveNotificationSound { get; set; }
    public string NotificationStyle { get; set; } = "Windows";
    public bool StartWithWindows { get; set; }
    public bool MinimizeToTrayOnClose { get; set; } = true;
    public bool KeepCaptureRunningWhileHidden { get; set; } = true;
    public bool StartCaptureAutomatically { get; set; } = true;
    public int ReplayMemoryLimitMb { get; set; } = 128;
    public int MinimumFreeDiskSpaceGb { get; set; } = 2;
    public bool AdvancedModeEnabled { get; set; }
    public bool ShowPreviewDiagnostics { get; set; }
    public bool VoiceCommandsEnabled { get; set; }
    public string VoiceWakePhrase { get; set; } = "";
    public string VoiceConfidenceThreshold { get; set; } = "High";
    public int VoiceCooldownSeconds { get; set; } = 3;
    public Dictionary<string, string> VoiceCommandPhrases { get; set; } = VoiceCommandCatalog.Defaults();

    public static bool LastLoadWasCorrupt { get; private set; }

    public static ClipprSettings Load()
    {
        var result = NativeCapture.ClipprLoadSettingsJson(out var jsonPointer, out var state);
        Marshal.ThrowExceptionForHR(result);
        string json;
        try { json = Marshal.PtrToStringUni(jsonPointer) ?? "{}"; }
        finally { Marshal.FreeCoTaskMem(jsonPointer); }

        LastLoadWasCorrupt = state == 2;
        ClipprSettings? loaded = null;
        if (state == 1)
        {
            try { loaded = JsonSerializer.Deserialize(json, JsonContext.ClipprSettings); }
            catch (JsonException) { LastLoadWasCorrupt = true; }
        }
        else if (state == 0)
        {
            // One-time migration from the older LocalSettings store. New saves are JSON only.
            loaded = LoadLegacyLocalSettings();
        }

        var settings = loaded ?? new ClipprSettings();
        // Existing installations retain their flat folder layout until the user enables organisation.
        if (loaded is not null && (state == 0 || loaded.SchemaVersion < 2)) settings.OrganizeClipsByGame = false;
        settings.ValidateAndNormalize();
        return settings;
    }

    private static ClipprSettings? LoadLegacyLocalSettings()
    {
        if (!PackageIdentity.IsPackaged) return null;
        var values = ApplicationData.Current.LocalSettings.Values;
        if (!values.ContainsKey("ClipFolder") && !values.ContainsKey("CaptureMode") &&
            !values.ContainsKey("ReplaySeconds") && !values.ContainsKey("ClippingHotkey")) return null;
        var settings = new ClipprSettings();
        if (values.TryGetValue("CaptureMode", out var captureMode) && captureMode is string mode && mode is "Screen" or "Application") settings.CaptureMode = mode;
        if (values.TryGetValue("PreferredDisplay", out var display) && display is string displayName) settings.PreferredDisplay = displayName;
        if (values.TryGetValue("PreferredApplicationProcessId", out var appPid) && appPid is int appId && appId > 0) settings.PreferredApplicationProcessId = (uint)appId;
        if (values.TryGetValue("RecordingResolution", out var resolution) && resolution is string res && res is "Source" or "1920x1080") settings.Resolution = res;
        if (values.TryGetValue("RecordingFps", out var fps) && fps is int rate && rate is 30 or 60) settings.FrameRate = (uint)rate;
        if (values.TryGetValue("StartCaptureAutomatically", out var autoStart) && autoStart is bool auto) settings.StartCaptureAutomatically = auto;
        if (values.TryGetValue("DesktopAudioEnabled", out var desktop) && desktop is bool desktopOn) settings.DesktopAudioEnabled = desktopOn;
        if (values.TryGetValue("MicrophoneAudioEnabled", out var microphone) && microphone is bool micOn) settings.MicrophoneAudioEnabled = micOn;
        if (values.TryGetValue("AudioOutputId", out var output) && output is string outputId) settings.AudioOutputId = outputId;
        if (values.TryGetValue("AudioInputId", out var input) && input is string inputId) settings.AudioInputId = inputId;
        if (values.TryGetValue("GameAudioPid", out var game) && game is int gameId && gameId > 0) settings.GameAudioProcessId = (uint)gameId;
        if (values.TryGetValue("ChatAudioPid", out var chat) && chat is int chatId && chatId > 0) settings.DiscordAudioProcessId = (uint)chatId;
        if (values.TryGetValue("ReplaySeconds", out var length) && length is int seconds) settings.ClipLengthSeconds = seconds;
        if (values.TryGetValue("ReplayRamMb", out var memory) && memory is int megabytes) settings.ReplayMemoryLimitMb = megabytes;
        if (values.TryGetValue("QualityPreset", out var quality) && quality is string preset) settings.QualityPreset = preset;
        if (values.TryGetValue("ClipFormat", out var format) && format is string extension) settings.VideoFormat = extension;
        if (values.TryGetValue("ClipFolder", out var folder) && folder is string path) settings.ClipFolder = path;
        if (values.TryGetValue("SaveNotificationSound", out var sound) && sound is bool enabled) settings.SaveNotificationSound = enabled;
        if (values.TryGetValue("ClippingHotkey", out var hotkey) && hotkey is string key) settings.Hotkey = key;
        settings.ValidateAndNormalize();
        return settings;
    }

    public ClipprSettings Clone()
    {
        var clone = (ClipprSettings)MemberwiseClone();
        clone.VoiceCommandPhrases = new Dictionary<string, string>(VoiceCommandPhrases, StringComparer.Ordinal);
        clone.CustomGameMappings = CustomGameMappings.ToDictionary(pair => pair.Key,
            pair => new GameMapping { DisplayName = pair.Value.DisplayName, FolderName = pair.Value.FolderName },
            StringComparer.OrdinalIgnoreCase);
        return clone;
    }

    public string? ValidateAndNormalize()
    {
        if (QuickShareMethod is not ("AnonMP4" or "CopyFile" or "WindowsShare" or "Ask")) QuickShareMethod = "AnonMP4";
        if (QuickShareFailureAction is not ("CopyFile" or "ShareMenu" or "Error")) QuickShareFailureAction = "CopyFile";
        if (CaptureMode is not ("Screen" or "Application")) CaptureMode = "Screen";
        if (Resolution is not ("Source" or "1920x1080")) Resolution = "Source";
        if (FrameRate is not (30 or 60)) FrameRate = 30;
        if (ClipLengthSeconds is < 1 or > 3600) ClipLengthSeconds = 30;
        if (ReplayMemoryLimitMb is < 16 or > 4096) ReplayMemoryLimitMb = 128;
        if (MinimumFreeDiskSpaceGb is < 1 or > 100) MinimumFreeDiskSpaceGb = 2;
        if (VoiceConfidenceThreshold is not ("Medium" or "High")) VoiceConfidenceThreshold = "High";
        VoiceCooldownSeconds = Math.Clamp(VoiceCooldownSeconds, 1, 10);
        VoiceWakePhrase = (VoiceWakePhrase ?? "").Trim();
        if (VoiceWakePhrase.Length > 32 || VoiceWakePhrase.Any(c => !char.IsLetterOrDigit(c) && c != ' ')) VoiceWakePhrase = "";
        VoiceCommandPhrases ??= VoiceCommandCatalog.Defaults();
        var supportedVoiceCommandKeys = VoiceCommandCatalog.Commands.Select(command => command.Key).ToHashSet(StringComparer.Ordinal);
        VoiceCommandPhrases = VoiceCommandPhrases
            .Where(pair => supportedVoiceCommandKeys.Contains(pair.Key))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        foreach (var command in VoiceCommandCatalog.Commands)
        {
            if (!VoiceCommandPhrases.TryGetValue(command.Key, out var phrase) || !VoiceCommandCatalog.IsValid(phrase))
                VoiceCommandPhrases[command.Key] = command.DefaultPhrase;
            else VoiceCommandPhrases[command.Key] = VoiceCommandCatalog.Normalize(phrase);
        }
        if (VoiceCommandCatalog.Commands.Select(command => VoiceCommandPhrases[command.Key])
            .GroupBy(phrase => phrase, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1))
            VoiceCommandPhrases = VoiceCommandCatalog.Defaults();
        if (QualityPreset is not ("Standard" or "High")) QualityPreset = "Standard";
        if (GameDetectionMethod is not ("Automatic" or "CapturedApplication" or "ForegroundApplication")) GameDetectionMethod = "Automatic";
        if (UnknownGameDestination is not ("Other" or "Root" or "CapturedApplication")) UnknownGameDestination = "Other";
        CustomGameMappings ??= new Dictionary<string, GameMapping>(StringComparer.OrdinalIgnoreCase);
        CustomGameMappings = CustomGameMappings.Where(pair => GameOrganisation.NormalizeExecutable(pair.Key) is not null &&
            pair.Value is not null && !string.IsNullOrWhiteSpace(pair.Value.DisplayName) && pair.Value.DisplayName.Length <= 80 &&
            GameOrganisation.SafeFolderName(pair.Value.FolderName) is not null)
            .GroupBy(pair => GameOrganisation.NormalizeExecutable(pair.Key)!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Last().Value, StringComparer.OrdinalIgnoreCase);
        if (VideoFormat is not ("mp4" or "mkv")) VideoFormat = "mp4";
        if (FilenameFormat is not ("ClipDat" or "Timestamp" or "Application")) FilenameFormat = "ClipDat";
        if (NotificationStyle is not ("Windows" or "ClipDat" or "None")) NotificationStyle = "Windows";
        if (Encoder is not ("MediaFoundationHardware" or "MediaFoundationSoftware" or "MediaFoundationAuto")) Encoder = "MediaFoundationHardware";
        if (!ClipHotkey.TryParse(Hotkey, out _, out _)) Hotkey = "Ctrl+Shift+F10";
        if (ScreenshotHotkey is not ("Ctrl+Alt+F10" or "Ctrl+Alt+F9" or "Ctrl+Shift+F8")) ScreenshotHotkey = "Ctrl+Alt+F10";
        if (ScreenshotFormat is not ("png" or "jpg")) ScreenshotFormat = "png";
        ScreenshotJpegQuality = Math.Clamp(ScreenshotJpegQuality, 1, 100);
        if (PreferredDisplay is null) PreferredDisplay = "";
        if (AudioOutputId is null) AudioOutputId = "";
        if (AudioInputId is null) AudioInputId = "";
        if (ClipFolder is null) ClipFolder = "";
        if (GameAudioProcessId != 0 && GameAudioProcessId == DiscordAudioProcessId) DiscordAudioProcessId = 0;
        if (MusicAudioProcessId != 0 && (MusicAudioProcessId == GameAudioProcessId || MusicAudioProcessId == DiscordAudioProcessId)) MusicAudioProcessId = 0;
        DesktopVolume = Math.Clamp(DesktopVolume, 0, 200);
        MicrophoneVolume = Math.Clamp(MicrophoneVolume, 0, 200);
        GameVolume = Math.Clamp(GameVolume, 0, 200);
        ChatVolume = Math.Clamp(ChatVolume, 0, 200);
        MusicVolume = Math.Clamp(MusicVolume, 0, 200);
        if (string.IsNullOrWhiteSpace(ClipFolder)) ClipFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "ClipDat");
        try { ClipFolder = Path.GetFullPath(ClipFolder.Trim()); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            ClipFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "ClipDat");
        }
        // Keep an unknown newer version so saving it does not downgrade the document.
        if (SchemaVersion < CurrentSchemaVersion) SchemaVersion = CurrentSchemaVersion;
        return null;
    }

    public string? Validate()
    {
        if (QuickShareMethod is not ("AnonMP4" or "CopyFile" or "WindowsShare" or "Ask")) return "Choose a Quick Share method.";
        if (QuickShareFailureAction is not ("CopyFile" or "ShareMenu" or "Error")) return "Choose a Quick Share fallback.";
        if (CaptureMode is not ("Screen" or "Application")) return "Choose Screen or Application capture.";
        if (Resolution is not ("Source" or "1920x1080")) return "Choose a supported recording resolution.";
        if (FrameRate is not (30 or 60)) return "Frame rate must be 30 or 60 FPS.";
        if (ClipLengthSeconds is < 1 or > 3600) return "Clip length must be between 1 second and 60 minutes.";
        if (ReplayMemoryLimitMb is < 16 or > 4096) return "Replay memory must be between 16 MB and 4096 MB.";
        if (MinimumFreeDiskSpaceGb is < 1 or > 100) return "Minimum free disk space must be between 1 GB and 100 GB.";
        if (VoiceConfidenceThreshold is not ("Medium" or "High")) return "Choose Medium or High voice recognition confidence.";
        if (VoiceCooldownSeconds is < 1 or > 10) return "Voice command cooldown must be between 1 and 10 seconds.";
        if (VoiceWakePhrase.Length > 32 || VoiceWakePhrase.Any(c => !char.IsLetterOrDigit(c) && c != ' ')) return "The wake phrase can contain letters, numbers, and spaces only (up to 32 characters).";
        if (VoiceCommandPhrases is null) return "Voice commands are missing. Reopen Settings to restore them.";
        var commandPhrases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var command in VoiceCommandCatalog.Commands)
        {
            if (!VoiceCommandPhrases.TryGetValue(command.Key, out var phrase) || !VoiceCommandCatalog.IsValid(phrase))
                return $"The phrase for {command.DefaultPhrase} must be 1–60 letters, numbers, or spaces.";
            if (!commandPhrases.Add(VoiceCommandCatalog.Normalize(phrase)))
                return "Voice command phrases must be unique.";
        }
        if (QualityPreset is not ("Standard" or "High")) return "Choose a supported quality preset.";
        if (GameDetectionMethod is not ("Automatic" or "CapturedApplication" or "ForegroundApplication")) return "Choose a game detection method.";
        if (UnknownGameDestination is not ("Other" or "Root" or "CapturedApplication")) return "Choose where unknown clips are saved.";
        if (CustomGameMappings.Any(pair => GameOrganisation.NormalizeExecutable(pair.Key) is null ||
            pair.Value is null || string.IsNullOrWhiteSpace(pair.Value.DisplayName) || pair.Value.DisplayName.Length > 80 ||
            GameOrganisation.SafeFolderName(pair.Value.FolderName) is null)) return "Correct the custom game mappings.";
        if (Encoder is not ("MediaFoundationHardware" or "MediaFoundationSoftware" or "MediaFoundationAuto")) return "Choose a supported H.264 encoder.";
        if (VideoFormat is not ("mp4" or "mkv")) return "Choose MP4 or MKV.";
        if (FilenameFormat is not ("ClipDat" or "Timestamp" or "Application")) return "Choose a supported filename format.";
        if (NotificationStyle is not ("Windows" or "ClipDat" or "None")) return "Choose a notification style.";
        if (!ClipHotkey.TryParse(Hotkey, out _, out _)) return "Choose Ctrl + Alt/Shift + F1–F12 for clipping.";
        if (ScreenshotHotkey is not ("Ctrl+Alt+F10" or "Ctrl+Alt+F9" or "Ctrl+Shift+F8")) return "Choose a supported screenshot hotkey.";
        if (Hotkey == ScreenshotHotkey) return "Clipping and screenshot hotkeys must be different.";
        if (ScreenshotFormat is not ("png" or "jpg")) return "Choose PNG or JPEG for screenshots.";
        if (ScreenshotJpegQuality is < 1 or > 100) return "JPEG quality must be between 1 and 100.";
        if (GameAudioProcessId != 0 && GameAudioProcessId == DiscordAudioProcessId) return "Game audio and Discord audio must use different applications.";
        if (MusicAudioProcessId != 0 && (MusicAudioProcessId == GameAudioProcessId || MusicAudioProcessId == DiscordAudioProcessId)) return "Music audio must use a different application.";
        if (DesktopVolume is < 0 or > 200 || MicrophoneVolume is < 0 or > 200 || GameVolume is < 0 or > 200 ||
            ChatVolume is < 0 or > 200 || MusicVolume is < 0 or > 200) return "Audio volume must be between 0 and 200%.";
        if (string.IsNullOrWhiteSpace(ClipFolder)) return "Choose a clip folder.";
        try { ClipFolder = Path.GetFullPath(ClipFolder.Trim()); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return "The clip folder path is not valid."; }
        return null;
    }

    public void SaveAtomicJson()
    {
        SchemaVersion = Math.Max(SchemaVersion, CurrentSchemaVersion);
        var json = JsonSerializer.Serialize(this, JsonContext.ClipprSettings);
        Marshal.ThrowExceptionForHR(NativeCapture.ClipprSaveSettingsJson(json));
    }
}

[JsonSerializable(typeof(ClipprSettings))]
internal partial class ClipprSettingsJsonContext : JsonSerializerContext { }
