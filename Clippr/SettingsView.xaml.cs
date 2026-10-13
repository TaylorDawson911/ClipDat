using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.ApplicationModel;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Pickers;
using Windows.System;
using WinRT.Interop;

namespace Clippr;

public sealed partial class SettingsView : UserControl
{
    private readonly MainWindow _mainWindow;
    private readonly ClipprSettings _draft;
    private bool _ready;
    private bool _applying;
    private bool _transitioning;
    private FrameworkElement? _currentPage;
    private FrameworkElement? _pendingPage;
    private string _feedbackUrl = "";

    public SettingsView(MainWindow mainWindow, ClipprSettings settings)
    {
        InitializeComponent();
        _mainWindow = mainWindow;
        _draft = settings.Clone();
        SettingsNavigation.RequestedTheme = ElementTheme.Dark;
        PopulateControls();
        WireEvents();
        VersionText.Text = GetVersion();
        ConfigureFeedback();
        _ready = true;
        if (ClipprSettings.LastLoadWasCorrupt)
        {
            SettingsStatus.Text = "Settings file was unreadable · defaults loaded. Save Settings to replace it.";
            SettingsStatus.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(ColorHelper.FromArgb(255, 237, 190, 120));
        }
        SettingsNavigation.SelectedItem = SettingsNavigation.MenuItems.OfType<NavigationViewItem>().FirstOrDefault();
    }

    private void PopulateControls()
    {
        AddOptions(CaptureModeCombo, ("Screen", "Screen"), ("Application", "Application"));
        AddOptions(ResolutionCombo, ("Source", "Source resolution"), ("1920x1080", "1920 × 1080"));
        AddOptions(FrameRateCombo, ("30", "30 FPS"), ("60", "60 FPS"));
        EncoderCombo.Items.Add(new ComboBoxItem { Content = "Media Foundation · hardware H.264", Tag = "MediaFoundationHardware" });
        SelectTag(EncoderCombo, _draft.Encoder);
        AddOptions(ClipLengthCombo, ("10", "10 seconds"), ("20", "20 seconds"), ("30", "30 seconds"),
            ("60", "60 seconds"), ("120", "2 minutes"), ("900", "15 minutes"), ("custom", "Custom…"));
        AddOptions(ReplayMemoryCombo, ("64", "64 MB"), ("128", "128 MB"), ("256", "256 MB"),
            ("512", "512 MB"), ("1024", "1 GB"), ("2048", "2 GB"), ("4096", "4 GB"));
        AddOptions(MinimumFreeSpaceCombo, ("1", "1 GB"), ("2", "2 GB"), ("5", "5 GB"),
            ("10", "10 GB"), ("20", "20 GB"));
        AddOptions(QualityCombo, ("Standard", "Standard · 8 Mbps"), ("High", "High · 16 Mbps"));
        AddOptions(VideoFormatCombo, ("mp4", "MP4"), ("mkv", "Matroska (MKV)"));
        AddOptions(NotificationStyleCombo, ("Windows", "Windows notifications"),
            ("ClipDat", "ClipDat slide-in cards"));
        AddOptions(HotkeyCombo, ("Ctrl+Shift+F10", "Ctrl + Shift + F10"),
            ("Ctrl+Alt+F10", "Ctrl + Alt + F10"), ("Ctrl+Shift+F9", "Ctrl + Shift + F9"));
        AddOptions(ScreenshotFormatCombo, ("png", "PNG · lossless"), ("jpg", "JPEG"));
        AddOptions(ScreenshotHotkeyCombo, ("Ctrl+Alt+F10", "Ctrl + Alt + F10"),
            ("Ctrl+Alt+F9", "Ctrl + Alt + F9"), ("Ctrl+Shift+F8", "Ctrl + Shift + F8"));
        AddOptions(VoiceConfidenceCombo, ("Medium", "Medium or higher"), ("High", "High only"));

        SelectTag(CaptureModeCombo, _draft.CaptureMode);
        SelectTag(ResolutionCombo, _draft.Resolution);
        SelectTag(FrameRateCombo, _draft.FrameRate.ToString());
        SelectTag(ClipLengthCombo, new[] { 10, 20, 30, 60, 120, 900 }.Contains(_draft.ClipLengthSeconds)
            ? _draft.ClipLengthSeconds.ToString() : "custom");
        SelectTag(ReplayMemoryCombo, _draft.ReplayMemoryLimitMb.ToString());
        SelectTag(MinimumFreeSpaceCombo, _draft.MinimumFreeDiskSpaceGb.ToString());
        SelectTag(QualityCombo, _draft.QualityPreset);
        SelectTag(VideoFormatCombo, _draft.VideoFormat);
        SelectTag(NotificationStyleCombo, _draft.NotificationStyle);
        SelectTag(HotkeyCombo, _draft.Hotkey);
        SelectTag(ScreenshotFormatCombo, _draft.ScreenshotFormat);
        SelectTag(ScreenshotHotkeyCombo, _draft.ScreenshotHotkey);
        SelectTag(VoiceConfidenceCombo, _draft.VoiceConfidenceThreshold);
        VoiceCommandsCheck.IsOn = _draft.VoiceCommandsEnabled;
        VoiceWakePhraseBox.Text = _draft.VoiceWakePhrase;
        VoiceCooldownBox.Value = _draft.VoiceCooldownSeconds;
        VoiceWakePhraseBox.IsEnabled = _draft.VoiceCommandsEnabled;
        VoiceConfidenceCombo.IsEnabled = _draft.VoiceCommandsEnabled;
        VoiceCooldownBox.IsEnabled = _draft.VoiceCommandsEnabled;
        VoiceSettingsStatus.Text = _draft.VoiceCommandsEnabled
            ? _mainWindow.CurrentVoiceStatus : "Voice commands are off.";
        ScreenshotJpegQualityBox.Value = _draft.ScreenshotJpegQuality;
        ScreenshotJpegQualityRow.Visibility = _draft.ScreenshotFormat == "jpg" ? Visibility.Visible : Visibility.Collapsed;
        CopyScreenshotCheck.IsOn = _draft.CopyScreenshotToClipboard;
        ScreenshotSoundCheck.IsOn = _draft.ScreenshotNotificationSound;
        CustomLengthBox.Value = _draft.ClipLengthSeconds;
        CustomLengthRow.Visibility = GetTag(ClipLengthCombo) == "custom" ? Visibility.Visible : Visibility.Collapsed;
        ClipFolderBox.Text = _draft.ClipFolder;
        AutoCaptureCheck.IsOn = _draft.StartCaptureAutomatically;
        MinimizeToTrayCheck.IsOn = _draft.MinimizeToTrayOnClose;
        StartWithWindowsCheck.IsOn = _draft.StartWithWindows;
        DesktopAudioCheck.IsOn = _draft.DesktopAudioEnabled;
        MicrophoneCheck.IsOn = _draft.MicrophoneAudioEnabled;
        NotificationSoundCheck.IsOn = _draft.SaveNotificationSound;

        PopulateDisplays();
        PopulateApplications(PreferredApplicationCombo, _draft.PreferredApplicationProcessId);
        PopulateAudioDevices(renderDevices: true, OutputDeviceCombo, _draft.AudioOutputId);
        PopulateAudioDevices(renderDevices: false, InputDeviceCombo, _draft.AudioInputId);
        PopulateApplications(GameAudioCombo, _draft.GameAudioProcessId);
        PopulateApplications(DiscordAudioCombo, _draft.DiscordAudioProcessId);
    }

    private void WireEvents()
    {
        foreach (var combo in new[] { CaptureModeCombo, PreferredDisplayCombo, PreferredApplicationCombo, ResolutionCombo, FrameRateCombo,
                     OutputDeviceCombo, InputDeviceCombo, GameAudioCombo, DiscordAudioCombo, ClipLengthCombo,
                     ReplayMemoryCombo, QualityCombo, VideoFormatCombo, NotificationStyleCombo, HotkeyCombo })
            combo.SelectionChanged += OnSettingChanged;
        MinimumFreeSpaceCombo.SelectionChanged += OnSettingChanged;
        ScreenshotFormatCombo.SelectionChanged += OnSettingChanged;
        ScreenshotHotkeyCombo.SelectionChanged += OnSettingChanged;
        VoiceConfidenceCombo.SelectionChanged += OnSettingChanged;
        CopyScreenshotCheck.Toggled += OnSettingChanged;
        ScreenshotSoundCheck.Toggled += OnSettingChanged;
        VoiceCommandsCheck.Toggled += OnVoiceCommandsToggled;
        VoiceWakePhraseBox.LostFocus += OnClipFolderLostFocus;
        VoiceCooldownBox.ValueChanged += OnVoiceCooldownChanged;
        ScreenshotJpegQualityBox.ValueChanged += OnScreenshotJpegQualityChanged;
        AutoCaptureCheck.Toggled += OnSettingChanged;
        MinimizeToTrayCheck.Toggled += OnSettingChanged;
        StartWithWindowsCheck.Toggled += OnSettingChanged;
        DesktopAudioCheck.Toggled += OnSettingChanged;
        MicrophoneCheck.Toggled += OnSettingChanged;
        NotificationSoundCheck.Toggled += OnSettingChanged;
        CustomLengthBox.ValueChanged += OnCustomLengthChanged;
        ClipFolderBox.LostFocus += OnClipFolderLostFocus;
    }

    private static void AddOptions(ComboBox combo, params (string Tag, string Label)[] options)
    {
        foreach (var option in options)
            combo.Items.Add(new ComboBoxItem { Tag = option.Tag, Content = option.Label });
    }

    private static void SelectTag(ComboBox combo, string tag)
    {
        combo.SelectedItem = combo.Items.OfType<ComboBoxItem>().FirstOrDefault(item => Equals(item.Tag?.ToString(), tag));
        if (combo.SelectedIndex < 0 && combo.Items.Count > 0) combo.SelectedIndex = 0;
    }

    private static string GetTag(ComboBox combo) => (combo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "";

    private void PopulateDisplays()
    {
        PreferredDisplayCombo.Items.Clear();
        var displays = _mainWindow.EnumerateSettingsDisplays();
        foreach (var display in displays)
            PreferredDisplayCombo.Items.Add(new ComboBoxItem
            {
                Content = $"{display.FriendlyName} ({display.Width} × {display.Height})",
                Tag = display.DeviceName
            });
        SelectTag(PreferredDisplayCombo, _draft.PreferredDisplay);
        if (PreferredDisplayCombo.SelectedIndex < 0 && PreferredDisplayCombo.Items.Count > 0)
            PreferredDisplayCombo.SelectedIndex = 0;
        if (PreferredDisplayCombo.Items.Count == 0)
            PreferredDisplayCombo.Items.Add(new ComboBoxItem { Content = "No displays available", Tag = "" });
    }

    private void PopulateAudioDevices(bool renderDevices, ComboBox combo, string selectedId)
    {
        combo.Items.Clear();
        combo.Items.Add(new ComboBoxItem { Content = "Default device", Tag = "" });
        foreach (var device in _mainWindow.EnumerateSettingsAudioDevices(renderDevices))
            combo.Items.Add(new ComboBoxItem
            {
                Content = device.Name + (device.IsDefault != 0 ? " · Default" : ""),
                Tag = device.Id
            });
        SelectTag(combo, selectedId);
    }

    private void PopulateApplications(ComboBox combo, uint selectedPid)
    {
        combo.Items.Clear();
        combo.Items.Add(new ComboBoxItem { Content = "None", Tag = "0" });
        foreach (var app in _mainWindow.EnumerateSettingsApplications())
            combo.Items.Add(new ComboBoxItem { Content = app.ApplicationName, Tag = app.ProcessId.ToString() });
        SelectTag(combo, selectedPid.ToString());
    }

    private void OnNavigationSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is not NavigationViewItem item) return;
        _pendingPage = item.Tag?.ToString() switch
        {
            "Voice" => VoicePage,
            "Audio" => AudioPage,
            "Output" => OutputPage,
            "Feedback" => FeedbackPage,
            _ => CapturePage
        };
        if (_currentPage is null)
        {
            _currentPage = _pendingPage;
            _currentPage.Visibility = Visibility.Visible;
            return;
        }
        if (!_transitioning) _ = TransitionPagesAsync();
    }

    private async Task TransitionPagesAsync()
    {
        _transitioning = true;
        try
        {
            while (_pendingPage is not null && !ReferenceEquals(_pendingPage, _currentPage))
            {
                var oldPage = _currentPage;
                if (oldPage is not null)
                {
                    await FadePageAsync(oldPage, 1, 0, 110);
                    oldPage.Visibility = Visibility.Collapsed;
                }
                var newPage = _pendingPage;
                newPage.Visibility = Visibility.Visible;
                _currentPage = newPage;
                await FadePageAsync(newPage, 0, 1, 190);
            }
        }
        finally { _transitioning = false; }
    }

    private static async Task FadePageAsync(FrameworkElement page, double from, double to, int milliseconds)
    {
        page.Opacity = from;
        var animation = new DoubleAnimation
        {
            From = from,
            To = to,
            Duration = new Duration(TimeSpan.FromMilliseconds(milliseconds)),
            EnableDependentAnimation = true
        };
        Storyboard.SetTarget(animation, page);
        Storyboard.SetTargetProperty(animation, "Opacity");
        var storyboard = new Storyboard();
        storyboard.Children.Add(animation);
        storyboard.Begin();
        await Task.Delay(milliseconds);
        storyboard.Stop();
        page.Opacity = to;
    }

    private void OnAdvancedToggled(object sender, RoutedEventArgs e)
    {
        var visibility = AdvancedToggle.IsOn ? Visibility.Visible : Visibility.Collapsed;
        AdvancedStartupRow.Visibility = visibility;
        AdvancedEncoderRow.Visibility = visibility;
        AdvancedApplicationAudioCard.Visibility = visibility;
        AdvancedReplayMemoryRow.Visibility = visibility;
        AdvancedNotificationRow.Visibility = visibility;
        AdvancedHotkeyRow.Visibility = visibility;
        AdvancedFeedbackActions.Visibility = visibility;
        AdvancedVoiceCard.Visibility = visibility;
    }

    private void OnSettingChanged(object sender, object e)
    {
        if (!_ready || _applying) return;
        if (ReferenceEquals(sender, ClipLengthCombo))
            CustomLengthRow.Visibility = GetTag(ClipLengthCombo) == "custom" ? Visibility.Visible : Visibility.Collapsed;
        UpdateDraftAndDirtyState();
    }

    private void OnCustomLengthChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (!_ready || GetTag(ClipLengthCombo) != "custom") return;
        UpdateDraftAndDirtyState();
    }

    private void OnClipFolderLostFocus(object sender, RoutedEventArgs e) => UpdateDraftAndDirtyState();

    private void UpdateDraftAndDirtyState()
    {
        _draft.CaptureMode = GetTag(CaptureModeCombo);
        _draft.PreferredDisplay = GetTag(PreferredDisplayCombo);
        _draft.PreferredApplicationProcessId = uint.TryParse(GetTag(PreferredApplicationCombo), out var preferredPid) ? preferredPid : 0;
        _draft.Resolution = GetTag(ResolutionCombo);
        _draft.FrameRate = uint.TryParse(GetTag(FrameRateCombo), out var fps) ? fps : 30;
        _draft.Encoder = GetTag(EncoderCombo);
        _draft.StartCaptureAutomatically = AutoCaptureCheck.IsOn;
        _draft.MinimizeToTrayOnClose = MinimizeToTrayCheck.IsOn;
        _draft.StartWithWindows = StartWithWindowsCheck.IsOn;
        _draft.VoiceCommandsEnabled = VoiceCommandsCheck.IsOn;
        _draft.VoiceWakePhrase = VoiceWakePhraseBox.Text;
        _draft.VoiceConfidenceThreshold = GetTag(VoiceConfidenceCombo);
        _draft.VoiceCooldownSeconds = double.IsFinite(VoiceCooldownBox.Value)
            ? (int)Math.Round(VoiceCooldownBox.Value) : 3;
        _draft.DesktopAudioEnabled = DesktopAudioCheck.IsOn;
        _draft.MicrophoneAudioEnabled = MicrophoneCheck.IsOn;
        _draft.AudioOutputId = GetTag(OutputDeviceCombo);
        _draft.AudioInputId = GetTag(InputDeviceCombo);
        _draft.GameAudioProcessId = uint.TryParse(GetTag(GameAudioCombo), out var gamePid) ? gamePid : 0;
        _draft.DiscordAudioProcessId = uint.TryParse(GetTag(DiscordAudioCombo), out var discordPid) ? discordPid : 0;
        _draft.ClipLengthSeconds = GetTag(ClipLengthCombo) == "custom"
            ? double.IsFinite(CustomLengthBox.Value) ? (int)Math.Round(CustomLengthBox.Value) : 0
            : int.TryParse(GetTag(ClipLengthCombo), out var seconds) ? seconds : 30;
        _draft.ReplayMemoryLimitMb = int.TryParse(GetTag(ReplayMemoryCombo), out var memory) ? memory : 128;
        _draft.MinimumFreeDiskSpaceGb = int.TryParse(GetTag(MinimumFreeSpaceCombo), out var minimumFree) ? minimumFree : 2;
        _draft.QualityPreset = GetTag(QualityCombo);
        _draft.VideoFormat = GetTag(VideoFormatCombo);
        _draft.ClipFolder = ClipFolderBox.Text;
        _draft.SaveNotificationSound = NotificationSoundCheck.IsOn;
        _draft.NotificationStyle = GetTag(NotificationStyleCombo);
        _draft.Hotkey = GetTag(HotkeyCombo);
        _draft.ScreenshotFormat = GetTag(ScreenshotFormatCombo);
        _draft.ScreenshotJpegQuality = double.IsFinite(ScreenshotJpegQualityBox.Value)
            ? (int)Math.Round(ScreenshotJpegQualityBox.Value) : 90;
        _draft.CopyScreenshotToClipboard = CopyScreenshotCheck.IsOn;
        _draft.ScreenshotNotificationSound = ScreenshotSoundCheck.IsOn;
        _draft.ScreenshotHotkey = GetTag(ScreenshotHotkeyCombo);
        ScreenshotJpegQualityRow.Visibility = _draft.ScreenshotFormat == "jpg" ? Visibility.Visible : Visibility.Collapsed;
        SaveSettingsButton.IsEnabled = true;
        SettingsStatus.Text = "Unsaved changes · press Save changes to apply.";
        SettingsStatus.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(ColorHelper.FromArgb(255, 174, 182, 188));
    }

    internal void RefreshReliabilityDiagnostics(string health, string recentErrors)
    {
        ReliabilityHealthText.Text = health;
        ReliabilityErrorsText.Text = string.IsNullOrWhiteSpace(recentErrors) ? "No recent errors." : recentErrors;
    }

    private void OnScreenshotJpegQualityChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_ready && !_applying) UpdateDraftAndDirtyState();
    }

    private void OnVoiceCommandsToggled(object sender, RoutedEventArgs e)
    {
        var enabled = VoiceCommandsCheck.IsOn;
        VoiceWakePhraseBox.IsEnabled = enabled;
        VoiceConfidenceCombo.IsEnabled = enabled;
        VoiceCooldownBox.IsEnabled = enabled;
        OnSettingChanged(sender, e);
    }

    private void OnVoiceCooldownChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_ready && !_applying) UpdateDraftAndDirtyState();
    }

    internal void RefreshVoiceStatus(string status)
    {
        if (VoiceCommandsCheck.IsOn) VoiceSettingsStatus.Text = status;
    }

    private void OnPreviewNotificationClick(object sender, RoutedEventArgs e)
    {
        _mainWindow.PreviewNotification(GetTag(NotificationStyleCombo));
    }

    private async void OnSaveSettingsClick(object sender, RoutedEventArgs e)
    {
        if (!_ready || _applying) return;
        UpdateDraftAndDirtyState();
        var validationError = _draft.Validate();
        if (validationError is not null)
        {
            SettingsStatus.Text = validationError;
            SettingsStatus.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(ColorHelper.FromArgb(255, 237, 142, 143));
            return;
        }
        var persisted = false;
        try
        {
            Directory.CreateDirectory(_draft.ClipFolder);
            _draft.SaveAtomicJson();
            persisted = true;
            _applying = true;
            SettingsNavigation.IsEnabled = false;
            SaveSettingsButton.IsEnabled = false;
            await _mainWindow.ApplySettingsAsync(_draft);
            ClipFolderBox.Text = _draft.ClipFolder;
            SettingsStatus.Text = "Settings saved · recording changes apply to the next recording.";
            SettingsStatus.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(ColorHelper.FromArgb(255, 174, 182, 188));
        }
        catch (Exception ex)
        {
            SettingsStatus.Text = persisted ? $"Saved, but could not apply: {ex.Message}" : $"Not saved: {ex.Message}";
            SettingsStatus.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(ColorHelper.FromArgb(255, 237, 142, 143));
            SaveSettingsButton.IsEnabled = true;
        }
        finally
        {
            _applying = false;
            SettingsNavigation.IsEnabled = true;
        }
    }

    private async void OnBrowseFolderClick(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker();
        picker.SuggestedStartLocation = PickerLocationId.VideosLibrary;
        picker.FileTypeFilter.Add("*");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(_mainWindow));
        var folder = await picker.PickSingleFolderAsync();
        if (folder is null) return;
        ClipFolderBox.Text = folder.Path;
        UpdateDraftAndDirtyState();
    }

    private void ConfigureFeedback()
    {
        try { _feedbackUrl = Windows.Storage.ApplicationData.Current.LocalSettings.Values["FeedbackUrl"] as string ?? ""; }
        catch { _feedbackUrl = ""; }
        if (Uri.TryCreate(_feedbackUrl, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http")
        {
            OpenFeedbackButton.Visibility = Visibility.Visible;
            FeedbackUrlMessage.Text = uri.Host;
        }
        else
        {
            _feedbackUrl = "";
            OpenFeedbackButton.Visibility = Visibility.Collapsed;
            FeedbackUrlMessage.Text = "No feedback URL is configured for this build.";
        }
    }

    private static string GetVersion()
    {
        try
        {
            var version = Package.Current.Id.Version;
            return $"{version.Major}.{version.Minor}.{version.Build}.{version.Revision}";
        }
        catch { return Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "Unknown"; }
    }

    private void OnCopyDiagnosticsClick(object sender, RoutedEventArgs e)
    {
        var packageVersion = GetVersion();
        var text = _mainWindow.BuildDiagnosticInformation(packageVersion);
        var data = new DataPackage();
        data.SetText(text);
        Clipboard.SetContent(data);
        FeedbackMessage.Text = "Diagnostic information copied to the clipboard.";
    }

    private async void OnOpenFeedbackClick(object sender, RoutedEventArgs e)
    {
        if (!Uri.TryCreate(_feedbackUrl, UriKind.Absolute, out var uri)) return;
        try { await Launcher.LaunchUriAsync(uri); }
        catch (Exception ex) { FeedbackMessage.Text = $"Could not open feedback page: {ex.Message}"; }
    }
}
