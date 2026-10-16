using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
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
    private ClipprSettings _savedSnapshot;
    private bool _ready;
    private bool _applying;
    private bool _transitioning;
    private bool _deviceListsLoaded;
    private readonly Dictionary<uint, ImageSource> _applicationIconSources = new();
    private bool? _captureSourceCompact;
    private DateTime _nextAudioPreviewUpdate;
    private DateTime _microphoneTestUntil;
    private bool _microphoneTestObserved;
    private FrameworkElement? _currentPage;
    private FrameworkElement? _pendingPage;
    private string _feedbackUrl = "";
    private readonly Dictionary<string, Border> _voiceCommandRows = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Border> _voiceCommandGroups = new(StringComparer.Ordinal);
    private string _voiceCommandCategory = "All";
    private readonly ObservableCollection<GameMappingRow> _gameMappingRows = [];
    private sealed record GameMappingRow(string Executable, string Label);

    public SettingsView(MainWindow mainWindow, ClipprSettings settings)
    {
        InitializeComponent();
        _mainWindow = mainWindow;
        _draft = settings.Clone();
        _savedSnapshot = settings.Clone();
        GameMappingList.ItemsSource = _gameMappingRows;
        SettingsNavigation.RequestedTheme = ElementTheme.Dark;
        PopulateControls();
        WireEvents();
        VersionText.Text = GetVersion();
        ConfigureFeedback();
        _ready = true;
        Loaded += OnSettingsLoaded;
        SaveSettingsButton.IsEnabled = ClipprSettings.LastLoadWasCorrupt;
        if (ClipprSettings.LastLoadWasCorrupt)
        {
            SettingsStatus.Text = "Settings file was unreadable · defaults loaded. Save Settings to replace it.";
            SettingsStatus.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(ColorHelper.FromArgb(255, 237, 190, 120));
        }
        SettingsNavigation.SelectedItem = SettingsNavigation.MenuItems.OfType<NavigationViewItem>().FirstOrDefault();
        CapturePage.SizeChanged += (_, _) => UpdateCaptureSourceLayout();
        AudioPage.SizeChanged += (_, _) => UpdateAudioLayout();
        foreach (var meter in new[] { DesktopPreviewMeter, PreviewDesktopMeter, PreviewMicrophoneMeter, PreviewGameMeter, PreviewChatMeter })
            for (var segment = 0; segment < 16; segment++)
                meter.Children.Add(new Border { Width = 7, Height = 13, CornerRadius = new CornerRadius(1), Background = new SolidColorBrush(ColorHelper.FromArgb(255, 46, 57, 69)) });
        _ = RefreshAppStorageUsageAsync();
    }

    private void UpdateCaptureSourceLayout()
    {
        var compact = CapturePage.ActualWidth < 1100;
        if (_captureSourceCompact == compact) return;
        _captureSourceCompact = compact;
        CaptureSourceGrid.ColumnDefinitions[2].Width = compact ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        Grid.SetColumn(CaptureApplicationField, compact ? 0 : 2);
        Grid.SetRow(CaptureApplicationField, compact ? 1 : 0);
        Grid.SetColumnSpan(CaptureApplicationField, compact ? 2 : 1);
    }

    private void UpdateOutputLayout()
    {
        var compact = OutputPage.ActualWidth < 720;
        OutputCardsGrid.ColumnDefinitions[0].Width = compact ? new GridLength(1, GridUnitType.Star) : new GridLength(1.15, GridUnitType.Star);
        OutputCardsGrid.ColumnDefinitions[1].Width = compact ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        Grid.SetColumn(OutputRightCards, compact ? 0 : 1);
        Grid.SetRow(OutputRightCards, compact ? 1 : 0);
    }

    private void UpdateAudioLayout()
    {
        var compact = AudioPage.ActualWidth < 720;
        AudioCardsGrid.ColumnDefinitions[1].Width = compact ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        Grid.SetColumn(AudioRightCards, compact ? 0 : 1);
        Grid.SetRow(AudioRightCards, compact ? 1 : 0);
        Grid.SetRow(AudioBehaviourCard, compact ? 2 : 1);
        Grid.SetColumnSpan(AudioBehaviourCard, compact ? 1 : 2);
    }

    private void UpdateFilenameExample()
    {
        var example = _draft.Clone();
        example.FilenameFormat = GetTag(FilenameFormatCombo);
        var extension = GetTag(VideoFormatCombo);
        FilenameExampleText.Text = $"{ClipFilename.Stem(example, DateTime.Now)}-001.{extension}";
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
        AddOptions(FilenameFormatCombo, ("ClipDat", "ClipDat - YYYY-MM-DD HH-mm-ss"),
            ("Timestamp", "YYYY-MM-DD HH-mm-ss"), ("Application", "Application - YYYY-MM-DD HH-mm-ss"));
        AddOptions(GameDetectionCombo, ("Automatic", "Automatic (recommended)"),
            ("CapturedApplication", "Captured application"), ("ForegroundApplication", "Foreground application"));
        AddOptions(UnknownGameCombo, ("Other", "Save to Clips/Other"),
            ("Root", "Save directly to Clips"), ("CapturedApplication", "Use captured application name"));
        AddOptions(NotificationStyleCombo, ("Windows", "Windows notifications"),
            ("ClipDat", "ClipDat slide-in cards"), ("None", "Off"));
        AddOptions(HotkeyCombo, ("Ctrl+Shift+F10", "Ctrl + Shift + F10"),
            ("Ctrl+Alt+F10", "Ctrl + Alt + F10"), ("Ctrl+Shift+F9", "Ctrl + Shift + F9"));
        if (!HotkeyCombo.Items.OfType<ComboBoxItem>().Any(item => Equals(item.Tag, _draft.Hotkey)))
            AddOptions(HotkeyCombo, (_draft.Hotkey, _draft.Hotkey.Replace("+", " + ", StringComparison.Ordinal)));
        AddOptions(ScreenshotFormatCombo, ("png", "PNG · lossless"), ("jpg", "JPEG"));
        AddOptions(ScreenshotHotkeyCombo, ("Ctrl+Alt+F10", "Ctrl + Alt + F10"),
            ("Ctrl+Alt+F9", "Ctrl + Alt + F9"), ("Ctrl+Shift+F8", "Ctrl + Shift + F8"));
        AddOptions(VoiceConfidenceCombo, ("Medium", "Medium or higher"), ("High", "High only"));
        AddOptions(QuickShareMethodCombo, ("AnonMP4", "AnonMP4 · upload and copy link"),
            ("CopyFile", "Copy video file to clipboard"), ("WindowsShare", "Windows Share"), ("Ask", "Ask every time"));
        AddOptions(QuickShareFailureCombo, ("CopyFile", "Copy video file to clipboard"),
            ("ShareMenu", "Open share menu"), ("Error", "Show error only"));

        SelectTag(CaptureModeCombo, _draft.CaptureMode);
        SelectTag(ResolutionCombo, _draft.Resolution);
        SelectTag(FrameRateCombo, _draft.FrameRate.ToString());
        SelectTag(ClipLengthCombo, new[] { 10, 20, 30, 60, 120, 900 }.Contains(_draft.ClipLengthSeconds)
            ? _draft.ClipLengthSeconds.ToString() : "custom");
        SelectTag(ReplayMemoryCombo, _draft.ReplayMemoryLimitMb.ToString());
        SelectTag(MinimumFreeSpaceCombo, _draft.MinimumFreeDiskSpaceGb.ToString());
        SelectTag(QualityCombo, _draft.QualityPreset);
        SelectTag(VideoFormatCombo, _draft.VideoFormat);
        SelectTag(FilenameFormatCombo, _draft.FilenameFormat);
        SelectTag(GameDetectionCombo, _draft.GameDetectionMethod);
        SelectTag(UnknownGameCombo, _draft.UnknownGameDestination);
        OrganiseClipsCheck.IsOn = _draft.OrganizeClipsByGame;
        RefreshGameMappingRows();
        RefreshGameFolderPreview();
        UpdateFilenameExample();
        SelectTag(NotificationStyleCombo, _draft.NotificationStyle);
        SelectTag(HotkeyCombo, _draft.Hotkey);
        SelectTag(ScreenshotFormatCombo, _draft.ScreenshotFormat);
        ScreenshotsEnabledCheck.IsOn = _draft.ScreenshotsEnabled;
        QuickShareEnabledCheck.IsOn = _draft.QuickShareEnabled;
        SelectTag(QuickShareMethodCombo, _draft.QuickShareMethod);
        SelectTag(QuickShareFailureCombo, _draft.QuickShareFailureAction);
        QuickShareAutoUploadCheck.IsOn = _draft.QuickShareAutoUpload;
        QuickShareAutoCopyCheck.IsOn = _draft.QuickShareAutoCopyLink;
        QuickShareOpenAfterCheck.IsOn = _draft.QuickShareOpenAfterUpload;
        QuickShareProgressCheck.IsOn = _draft.QuickShareShowProgress;
        QuickShareNotificationCheck.IsOn = _draft.QuickShareShowNotification;
        QuickShareHistoryCheck.IsOn = _draft.QuickShareKeepHistory;
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
        UpdateVoiceListeningVisual(_mainWindow.CurrentVoiceStatus);
        VoiceTranscriptText.Text = _mainWindow.CurrentVoiceTranscript;
        VoiceTranscriptResultText.Text = _mainWindow.CurrentVoiceTranscriptResult;
        PopulateVoiceCommandRows();
        UpdateVoiceFilterButtons();
        ScreenshotJpegQualityBox.Value = _draft.ScreenshotJpegQuality;
        ScreenshotJpegQualityRow.Visibility = _draft.ScreenshotFormat == "jpg" ? Visibility.Visible : Visibility.Collapsed;
        CopyScreenshotCheck.IsOn = _draft.CopyScreenshotToClipboard;
        ScreenshotSoundCheck.IsOn = _draft.ScreenshotNotificationSound;
        SettingsMicrophoneVolumeSlider.Value = _draft.MicrophoneVolume;
        SettingsMicrophoneVolumeText.Text = $"{_draft.MicrophoneVolume}%";
        CustomLengthBox.Value = _draft.ClipLengthSeconds;
        CustomLengthRow.Visibility = GetTag(ClipLengthCombo) == "custom" ? Visibility.Visible : Visibility.Collapsed;
        ClipFolderBox.Text = _draft.ClipFolder;
        AutoCaptureCheck.IsChecked = _draft.StartCaptureAutomatically;
        MinimizeToTrayCheck.IsChecked = _draft.MinimizeToTrayOnClose;
        KeepCaptureRunningWhileHiddenCheck.IsChecked = _draft.KeepCaptureRunningWhileHidden;
        StartWithWindowsCheck.IsChecked = _draft.StartWithWindows;
        ShowPreviewDiagnosticsCheck.IsOn = _draft.ShowPreviewDiagnostics;
        AdvancedToggle.IsOn = _draft.AdvancedModeEnabled;
        DesktopAudioCheck.IsOn = _draft.DesktopAudioEnabled;
        MicrophoneCheck.IsOn = _draft.MicrophoneAudioEnabled;
        NotificationSoundCheck.IsOn = _draft.SaveNotificationSound;

        PreferredDisplayCombo.Items.Add(new ComboBoxItem { Content = "Finding displays…", IsEnabled = false });
        PreferredApplicationCombo.Items.Add(new ComboBoxItem { Content = "Finding applications…", IsEnabled = false });
        OutputDeviceCombo.Items.Add(new ComboBoxItem { Content = "Finding audio devices…", IsEnabled = false });
        InputDeviceCombo.Items.Add(new ComboBoxItem { Content = "Finding audio devices…", IsEnabled = false });
        GameAudioCombo.Items.Add(new ComboBoxItem { Content = "Finding applications…", IsEnabled = false });
        DiscordAudioCombo.Items.Add(new ComboBoxItem { Content = "Finding applications…", IsEnabled = false });
        UpdateAudioAvailability();
    }

    private async void OnSettingsLoaded(object sender, RoutedEventArgs e)
    {
        if (_deviceListsLoaded) return;
        _deviceListsLoaded = true;
        try
        {
            var data = await Task.Run(() =>
            {
                var displays = _mainWindow.EnumerateSettingsDisplays();
                var output = _mainWindow.EnumerateSettingsAudioDevices(true);
                var input = _mainWindow.EnumerateSettingsAudioDevices(false);
                var applications = _mainWindow.EnumerateSettingsApplications();
                var icons = new Dictionary<uint, byte[]>();
                foreach (var app in applications)
                {
                    var pixels = new byte[32 * 32 * 4];
                    if (NativeCapture.ClipprCopyWindowIcon(app.Handle, pixels, pixels.Length) == 0)
                        icons[app.ProcessId] = pixels;
                }
                return (displays, output, input, applications, icons);
            });
            if (!IsLoaded) return;
            _applying = true;
            try
            {
                PopulateDisplays(data.displays);
                PopulateAudioDevices(OutputDeviceCombo, _draft.AudioOutputId, data.output);
                PopulateAudioDevices(InputDeviceCombo, _draft.AudioInputId, data.input);
                CacheApplicationIcons(data.icons);
                PopulateApplications(PreferredApplicationCombo, _draft.PreferredApplicationProcessId, data.applications);
                PopulateApplications(GameAudioCombo, _draft.GameAudioProcessId, data.applications);
                PopulateApplications(DiscordAudioCombo, _draft.DiscordAudioProcessId, data.applications);
                UpdateAudioAvailability();
            }
            finally { _applying = false; }
        }
        catch (Exception ex)
        {
            SettingsStatus.Text = $"Some device lists could not load: {ex.Message}";
        }
    }

    private void WireEvents()
    {
        foreach (var combo in new[] { CaptureModeCombo, PreferredDisplayCombo, PreferredApplicationCombo,
                     ResolutionCombo, FrameRateCombo, EncoderCombo, MinimumFreeSpaceCombo, VoiceConfidenceCombo,
                     OutputDeviceCombo, InputDeviceCombo, GameAudioCombo, DiscordAudioCombo, ClipLengthCombo,
                     ReplayMemoryCombo, QualityCombo, VideoFormatCombo, ScreenshotFormatCombo,
                     ScreenshotHotkeyCombo, FilenameFormatCombo, NotificationStyleCombo, HotkeyCombo,
                     GameDetectionCombo, UnknownGameCombo, QuickShareMethodCombo, QuickShareFailureCombo })
            ComboBoxPopupPlacement.Attach(combo);
        foreach (var combo in new[] { CaptureModeCombo, PreferredDisplayCombo, PreferredApplicationCombo, ResolutionCombo, FrameRateCombo,
                     OutputDeviceCombo, InputDeviceCombo, GameAudioCombo, DiscordAudioCombo, ClipLengthCombo,
                     ReplayMemoryCombo, QualityCombo, VideoFormatCombo, FilenameFormatCombo, NotificationStyleCombo, HotkeyCombo })
            combo.SelectionChanged += OnSettingChanged;
        MinimumFreeSpaceCombo.SelectionChanged += OnSettingChanged;
        GameDetectionCombo.SelectionChanged += OnSettingChanged;
        UnknownGameCombo.SelectionChanged += OnSettingChanged;
        OrganiseClipsCheck.Toggled += OnSettingChanged;
        ScreenshotsEnabledCheck.Toggled += OnSettingChanged;
        QuickShareEnabledCheck.Toggled += OnSettingChanged;
        QuickShareMethodCombo.SelectionChanged += OnSettingChanged;
        QuickShareFailureCombo.SelectionChanged += OnSettingChanged;
        foreach (var toggle in new[] { QuickShareAutoUploadCheck, QuickShareAutoCopyCheck,
                     QuickShareOpenAfterCheck, QuickShareProgressCheck, QuickShareNotificationCheck,
                     QuickShareHistoryCheck }) toggle.Toggled += OnSettingChanged;
        ScreenshotFormatCombo.SelectionChanged += OnSettingChanged;
        ScreenshotHotkeyCombo.SelectionChanged += OnSettingChanged;
        VoiceConfidenceCombo.SelectionChanged += OnSettingChanged;
        CopyScreenshotCheck.Toggled += OnSettingChanged;
        ScreenshotSoundCheck.Toggled += OnSettingChanged;
        VoiceCommandsCheck.Toggled += OnVoiceCommandsToggled;
        VoiceWakePhraseBox.LostFocus += OnClipFolderLostFocus;
        VoiceCooldownBox.ValueChanged += OnVoiceCooldownChanged;
        VoiceCommandSearchBox.TextChanged += (_, _) => FilterVoiceCommands();
        ScreenshotJpegQualityBox.ValueChanged += OnScreenshotJpegQualityChanged;
        AutoCaptureCheck.Click += OnSettingChanged;
        MinimizeToTrayCheck.Click += OnSettingChanged;
        KeepCaptureRunningWhileHiddenCheck.Click += OnSettingChanged;
        StartWithWindowsCheck.Click += OnSettingChanged;
        ShowPreviewDiagnosticsCheck.Toggled += OnSettingChanged;
        DesktopAudioCheck.Toggled += OnSettingChanged;
        MicrophoneCheck.Toggled += OnSettingChanged;
        NotificationSoundCheck.Toggled += OnSettingChanged;
        CustomLengthBox.ValueChanged += OnCustomLengthChanged;
        ClipFolderBox.LostFocus += OnClipFolderLostFocus;
        ClipFolderBox.TextChanged += (_, _) => { if (_ready) UpdateDraftAndDirtyState(); };
        OutputPage.SizeChanged += (_, _) => UpdateOutputLayout();
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

    private void PopulateVoiceCommandRows()
    {
        foreach (var category in new[] { "Clipping", "Recording" })
        {
            var group = new StackPanel { Spacing = 4 };
            var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(3, 0, 0, 3) };
            if (category == "Recording")
                header.Children.Add(new Microsoft.UI.Xaml.Shapes.Ellipse
                {
                    Width = 12, Height = 12, Fill = new SolidColorBrush(ColorHelper.FromArgb(255, 237, 78, 88)),
                    VerticalAlignment = VerticalAlignment.Center
                });
            else
                header.Children.Add(new FontIcon
                {
                    Glyph = category == "Clipping" ? "\uE8C6" : "\uE713", FontSize = 15,
                    VerticalAlignment = VerticalAlignment.Center
                });
            header.Children.Add(new TextBlock
            {
                Text = category,
                FontSize = 13,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center
            });
            group.Children.Add(header);
            var groupBorder = new Border { Style = (Style)Resources["VoiceCommandGroupStyle"], Child = group };
            _voiceCommandGroups[category] = groupBorder;
            VoiceCommandList.Children.Add(groupBorder);

            foreach (var command in VoiceCommandCatalog.Commands.Where(item => item.Category == category))
            {
                var row = new Border { Style = (Style)Resources["VoiceCommandRowStyle"] };
                var layout = new Grid { ColumnSpacing = 8 };
                layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                var phraseAndEdit = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3, VerticalAlignment = VerticalAlignment.Center };
                var phraseSlot = new Grid { VerticalAlignment = VerticalAlignment.Center };

                var phrase = new TextBlock
                {
                    Text = _draft.VoiceCommandPhrases[command.Key],
                    FontSize = 12,
                    MaxWidth = 120,
                    TextWrapping = TextWrapping.Wrap,
                    VerticalAlignment = VerticalAlignment.Center
                };
                var description = new TextBlock
                {
                    Text = command.Description,
                    FontSize = 10,
                    TextWrapping = TextWrapping.Wrap,
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = new SolidColorBrush(ColorHelper.FromArgb(255, 170, 180, 190))
                };
                phraseSlot.Children.Add(phrase);

                var editor = new TextBox
                {
                    Style = (Style)Resources["VoicePhraseEditorStyle"],
                    Text = _draft.VoiceCommandPhrases[command.Key],
                    MaxLength = 60,
                    Width = Math.Clamp(_draft.VoiceCommandPhrases[command.Key].Length * 6.5 + 12, 58, 120),
                    Height = 23,
                    Visibility = Visibility.Collapsed
                };
                AutomationProperties.SetName(editor, $"Phrase for {command.DefaultPhrase}");
                phraseSlot.Children.Add(editor);
                phraseAndEdit.Children.Add(phraseSlot);

                var edit = new Button
                {
                    Content = new FontIcon { Glyph = "\uE70F", FontSize = 11 },
                    Width = 20, Height = 22, Padding = new Thickness(0),
                    Background = new SolidColorBrush(ColorHelper.FromArgb(0, 0, 0, 0)),
                    BorderThickness = new Thickness(0),
                    VerticalAlignment = VerticalAlignment.Center,
                    Visibility = Visibility.Collapsed
                };
                AutomationProperties.SetName(edit, $"Edit {command.DefaultPhrase} voice command");
                ToolTipService.SetToolTip(edit, "Edit phrase");
                phraseAndEdit.Children.Add(edit);
                layout.Children.Add(phraseAndEdit);
                Grid.SetColumn(description, 1);
                layout.Children.Add(description);
                row.Child = layout;
                group.Children.Add(row);
                _voiceCommandRows[command.Key] = row;

                var original = editor.Text;
                var originalStatus = SettingsStatus.Text;
                Brush? originalStatusBrush = SettingsStatus.Foreground;
                var cancelledEdit = false;
                void BeginEditing()
                {
                    cancelledEdit = false;
                    original = _draft.VoiceCommandPhrases[command.Key];
                    originalStatus = SettingsStatus.Text;
                    originalStatusBrush = SettingsStatus.Foreground;
                    phrase.Visibility = Visibility.Collapsed;
                    edit.Visibility = Visibility.Collapsed;
                    editor.Visibility = Visibility.Visible;
                    editor.Focus(FocusState.Programmatic);
                    editor.SelectAll();
                }
                edit.Click += (_, _) => BeginEditing();
                phrase.Tapped += (_, _) => BeginEditing();
                phrase.PointerEntered += (_, _) =>
                {
                    phrase.TextDecorations = Windows.UI.Text.TextDecorations.Underline;
                    phrase.Foreground = new SolidColorBrush(ColorHelper.FromArgb(255, 115, 185, 245));
                };
                phrase.PointerExited += (_, _) =>
                {
                    phrase.TextDecorations = Windows.UI.Text.TextDecorations.None;
                    phrase.ClearValue(TextBlock.ForegroundProperty);
                };
                phraseAndEdit.PointerEntered += (_, _) =>
                {
                    if (editor.Visibility != Visibility.Visible) edit.Visibility = Visibility.Visible;
                };
                phraseAndEdit.PointerExited += (_, _) =>
                {
                    if (editor.Visibility != Visibility.Visible) edit.Visibility = Visibility.Collapsed;
                };
                editor.TextChanged += (_, _) =>
                {
                    if (!_ready || _applying || cancelledEdit) return;
                    _draft.VoiceCommandPhrases[command.Key] = editor.Text;
                    phrase.Text = string.IsNullOrWhiteSpace(editor.Text) ? "Enter a phrase" : editor.Text.Trim();
                    UpdateDraftAndDirtyState();
                };
                editor.KeyDown += (_, args) =>
                {
                    if (args.Key == VirtualKey.Escape)
                    {
                        cancelledEdit = true;
                        editor.Text = original;
                        _draft.VoiceCommandPhrases[command.Key] = original;
                        phrase.Text = original;
                        editor.Visibility = Visibility.Collapsed;
                        phrase.Visibility = Visibility.Visible;
                        SettingsStatus.Text = originalStatus;
                        SettingsStatus.Foreground = originalStatusBrush;
                        args.Handled = true;
                    }
                    else if (args.Key == VirtualKey.Enter)
                    {
                        editor.Visibility = Visibility.Collapsed;
                        phrase.Visibility = Visibility.Visible;
                        args.Handled = true;
                    }
                };
                editor.LostFocus += (_, _) =>
                {
                    editor.Visibility = Visibility.Collapsed;
                    phrase.Visibility = Visibility.Visible;
                };
            }
        }
    }

    private void FilterVoiceCommands()
    {
        var search = VoiceCommandSearchBox.Text.Trim();
        foreach (var group in _voiceCommandGroups)
        {
            var anyVisible = false;
            foreach (var command in VoiceCommandCatalog.Commands.Where(item => item.Category == group.Key))
            {
                var matches = (_voiceCommandCategory == "All" || _voiceCommandCategory == group.Key) &&
                    (search.Length == 0 || _draft.VoiceCommandPhrases[command.Key].Contains(search, StringComparison.OrdinalIgnoreCase) ||
                     command.Description.Contains(search, StringComparison.OrdinalIgnoreCase));
                _voiceCommandRows[command.Key].Visibility = matches ? Visibility.Visible : Visibility.Collapsed;
                anyVisible |= matches;
            }
            group.Value.Visibility = anyVisible ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void OnVoiceFilterClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string category }) return;
        _voiceCommandCategory = category;
        UpdateVoiceFilterButtons();
        FilterVoiceCommands();
    }

    private void UpdateVoiceFilterButtons()
    {
        foreach (var button in new[] { VoiceFilterAllButton, VoiceFilterClippingButton,
                     VoiceFilterRecordingButton })
        {
            var selected = Equals(button.Tag, _voiceCommandCategory);
            button.Background = new SolidColorBrush(selected
                ? ColorHelper.FromArgb(255, 41, 120, 204)
                : ColorHelper.FromArgb(255, 30, 37, 44));
            button.BorderBrush = new SolidColorBrush(selected
                ? ColorHelper.FromArgb(255, 64, 146, 229)
                : ColorHelper.FromArgb(255, 57, 66, 75));
            button.BorderThickness = new Thickness(1);
        }
    }

    private void PopulateDisplays(NativeCapture.Display[] displays)
    {
        PreferredDisplayCombo.Items.Clear();
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
        => PopulateAudioDevices(combo, selectedId, _mainWindow.EnumerateSettingsAudioDevices(renderDevices));

    private static void PopulateAudioDevices(ComboBox combo, string selectedId, NativeCapture.AudioDevice[] devices)
    {
        combo.Items.Clear();
        combo.Items.Add(new ComboBoxItem { Content = "Default device", Tag = "" });
        foreach (var device in devices)
            combo.Items.Add(new ComboBoxItem
            {
                Content = device.Name + (device.IsDefault != 0 ? " · Default" : ""),
                Tag = device.Id
            });
        if (!string.IsNullOrEmpty(selectedId) && !combo.Items.OfType<ComboBoxItem>().Any(item => Equals(item.Tag, selectedId)))
            combo.Items.Add(new ComboBoxItem { Content = "Unavailable · reconnect or choose a device", Tag = selectedId });
        SelectTag(combo, selectedId);
    }

    private void CacheApplicationIcons(Dictionary<uint, byte[]> icons)
    {
        _applicationIconSources.Clear();
        foreach (var (processId, pixels) in icons)
        {
            var bitmap = new WriteableBitmap(32, 32);
            using (var stream = bitmap.PixelBuffer.AsStream()) stream.Write(pixels, 0, pixels.Length);
            bitmap.Invalidate();
            _applicationIconSources[processId] = bitmap;
        }
    }

    private void PopulateApplications(ComboBox combo, uint selectedPid, NativeCapture.Window[] applications)
    {
        combo.Items.Clear();
        combo.Items.Add(new ComboBoxItem { Content = "None", Tag = "0" });
        foreach (var app in applications)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 9 };
            if (_applicationIconSources.TryGetValue(app.ProcessId, out var source))
                row.Children.Add(new Image { Source = source, Width = 20, Height = 20 });
            else
                row.Children.Add(new FontIcon { Glyph = "\uE71D", Width = 20, Height = 20, FontSize = 17 });
            row.Children.Add(new TextBlock { Text = app.ApplicationName, VerticalAlignment = VerticalAlignment.Center });
            combo.Items.Add(new ComboBoxItem { Content = row, Tag = app.ProcessId.ToString() });
        }
        if (selectedPid != 0 && !combo.Items.OfType<ComboBoxItem>().Any(item => Equals(item.Tag, selectedPid.ToString())))
            combo.Items.Add(new ComboBoxItem { Content = "Application unavailable · refresh or choose another", Tag = selectedPid.ToString() });
        SelectTag(combo, selectedPid.ToString());
    }

    private void UpdateAudioAvailability()
    {
        var missing = new List<string>();
        if ((OutputDeviceCombo.SelectedItem as ComboBoxItem)?.Content?.ToString()?.StartsWith("Unavailable", StringComparison.Ordinal) == true) missing.Add("output device");
        if ((InputDeviceCombo.SelectedItem as ComboBoxItem)?.Content?.ToString()?.StartsWith("Unavailable", StringComparison.Ordinal) == true) missing.Add("microphone");
        if ((GameAudioCombo.SelectedItem as ComboBoxItem)?.Content?.ToString()?.StartsWith("Application unavailable", StringComparison.Ordinal) == true) missing.Add("game application");
        if ((DiscordAudioCombo.SelectedItem as ComboBoxItem)?.Content?.ToString()?.StartsWith("Application unavailable", StringComparison.Ordinal) == true) missing.Add("chat application");
        AudioAvailabilityText.Text = missing.Count == 0 ? "" : $"Unavailable: {string.Join(", ", missing)}. Reconnect or choose another source; the saved selection has been preserved.";
    }

    private void OnRefreshAudioDevicesClick(object sender, RoutedEventArgs e)
    {
        var output = GetTag(OutputDeviceCombo);
        var input = GetTag(InputDeviceCombo);
        _applying = true;
        try
        {
            PopulateAudioDevices(true, OutputDeviceCombo, output);
            PopulateAudioDevices(false, InputDeviceCombo, input);
            UpdateAudioAvailability();
        }
        finally { _applying = false; }
    }

    private async void OnRefreshAudioApplicationsClick(object sender, RoutedEventArgs e)
    {
        var game = uint.TryParse(GetTag(GameAudioCombo), out var gamePid) ? gamePid : 0;
        var chat = uint.TryParse(GetTag(DiscordAudioCombo), out var chatPid) ? chatPid : 0;
        _applying = true;
        try
        {
            var data = await Task.Run(() =>
            {
                var applications = _mainWindow.EnumerateSettingsApplications();
                var icons = new Dictionary<uint, byte[]>();
                foreach (var app in applications)
                {
                    var pixels = new byte[32 * 32 * 4];
                    if (NativeCapture.ClipprCopyWindowIcon(app.Handle, pixels, pixels.Length) == 0)
                        icons[app.ProcessId] = pixels;
                }
                return (applications, icons);
            });
            if (!IsLoaded) return;
            CacheApplicationIcons(data.icons);
            PopulateApplications(GameAudioCombo, game, data.applications);
            PopulateApplications(DiscordAudioCombo, chat, data.applications);
            UpdateAudioAvailability();
        }
        catch (Exception ex)
        {
            SettingsStatus.Text = $"Could not refresh applications: {ex.Message}";
        }
        finally { _applying = false; }
    }

    private void OnSettingsMicrophoneVolumeChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (!_ready || _applying) return;
        SettingsMicrophoneVolumeText.Text = $"{Math.Round(SettingsMicrophoneVolumeSlider.Value)}%";
        UpdateDraftAndDirtyState();
    }

    private void OnTestMicrophoneClick(object sender, RoutedEventArgs e)
    {
        var status = _mainWindow.GetCurrentAudioStatus();
        if (!_savedSnapshot.MicrophoneAudioEnabled)
            MicrophoneTestStatus.Text = "Enable microphone capture and save changes before testing.";
        else if (status.MicrophoneRunning == 0)
            MicrophoneTestStatus.Text = status.MicrophoneError != 0
                ? $"Microphone unavailable · 0x{status.MicrophoneError:X8}. Check device access and permissions."
                : "Microphone is not running. Check the selected device and capture state.";
        else
        {
            _microphoneTestObserved = false;
            _microphoneTestUntil = DateTime.UtcNow.AddSeconds(10);
            MicrophoneTestStatus.Text = "Listening for microphone signal for 10 seconds…";
        }
    }

    internal void RefreshAudioPreview(NativeCapture.AudioLevels levels, NativeCapture.AudioStatus status)
    {
        if (AudioPage.Visibility != Visibility.Visible || DateTime.UtcNow < _nextAudioPreviewUpdate) return;
        _nextAudioPreviewUpdate = DateTime.UtcNow.AddMilliseconds(120);
        UpdateSignalMeter(DesktopPreviewMeter, DesktopPreviewLevel, status.DesktopRunning != 0, levels.Desktop);
        UpdateSignalMeter(PreviewDesktopMeter, PreviewDesktopLevel, status.DesktopRunning != 0, levels.Desktop);
        UpdateSignalMeter(PreviewMicrophoneMeter, PreviewMicrophoneLevel, status.MicrophoneRunning != 0, levels.Microphone);
        UpdateSignalMeter(PreviewGameMeter, PreviewGameLevel, status.GameRunning != 0, levels.Game);
        UpdateSignalMeter(PreviewChatMeter, PreviewChatLevel, status.ChatRunning != 0, levels.Chat);
        if (_microphoneTestUntil == DateTime.MinValue) return;
        if (levels.Microphone > 0.001f) _microphoneTestObserved = true;
        if (DateTime.UtcNow >= _microphoneTestUntil)
        {
            MicrophoneTestStatus.Text = _microphoneTestObserved
                ? "Test finished · microphone signal detected." : "Test finished · no microphone signal detected.";
            _microphoneTestUntil = DateTime.MinValue;
            return;
        }
        MicrophoneTestStatus.Text = levels.Microphone > 0.001f
            ? $"Microphone signal detected · {20 * Math.Log10(levels.Microphone):F0} dBFS"
            : "Listening… no microphone signal detected yet.";
    }

    internal bool IsAudioPageVisible => AudioPage.Visibility == Visibility.Visible;

    private static void UpdateSignalMeter(StackPanel meter, TextBlock label, bool running, float peak)
    {
        var decibels = peak > 0 ? 20 * Math.Log10(peak) : double.NegativeInfinity;
        var count = running && peak > 0.001f ? (int)Math.Ceiling(Math.Clamp((decibels + 60) / 60, 0, 1) * meter.Children.Count) : 0;
        for (var i = 0; i < meter.Children.Count; i++)
            ((Border)meter.Children[i]).Background = new SolidColorBrush(i < count
                ? ColorHelper.FromArgb(255, 48, 136, 235) : ColorHelper.FromArgb(255, 46, 57, 69));
        label.Text = !running ? "Off" : peak <= 0 ? "−∞ dBFS" : $"{decibels:F0} dBFS";
    }

    private void OnNavigationSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is not NavigationViewItem item) return;
        _pendingPage = item.Tag?.ToString() switch
        {
            "Voice" => VoicePage,
            "Audio" => AudioPage,
            "Output" => OutputPage,
            "QuickShare" => QuickSharePage,
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
                newPage.UpdateLayout();
                if (ReferenceEquals(newPage, OutputPage)) UpdateOutputLayout();
                if (ReferenceEquals(newPage, AudioPage)) UpdateAudioLayout();
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
        AdvancedPreviewDiagnosticsCard.Visibility = visibility;
        AdvancedEncoderRow.Visibility = visibility;
        AdvancedApplicationAudioCard.Visibility = visibility;
        AdvancedReplayMemoryRow.Visibility = visibility;
        AdvancedNotificationRow.Visibility = visibility;
        AdvancedHotkeyRow.Visibility = visibility;
        AdvancedHotkeyCard.Visibility = visibility;
        AdvancedFeedbackActions.Visibility = visibility;
        if (_ready && !_applying) UpdateDraftAndDirtyState();
    }

    private void OnSettingChanged(object sender, object e)
    {
        if (!_ready || _applying) return;
        if (ReferenceEquals(sender, ClipLengthCombo))
            CustomLengthRow.Visibility = GetTag(ClipLengthCombo) == "custom" ? Visibility.Visible : Visibility.Collapsed;
        UpdateDraftAndDirtyState();
        if (ReferenceEquals(sender, FilenameFormatCombo) || ReferenceEquals(sender, VideoFormatCombo) ||
            ReferenceEquals(sender, CaptureModeCombo) || ReferenceEquals(sender, PreferredApplicationCombo)) UpdateFilenameExample();
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
        _draft.StartCaptureAutomatically = AutoCaptureCheck.IsChecked == true;
        _draft.MinimizeToTrayOnClose = MinimizeToTrayCheck.IsChecked == true;
        _draft.KeepCaptureRunningWhileHidden = KeepCaptureRunningWhileHiddenCheck.IsChecked == true;
        _draft.StartWithWindows = StartWithWindowsCheck.IsChecked == true;
        _draft.AdvancedModeEnabled = AdvancedToggle.IsOn;
        _draft.ShowPreviewDiagnostics = ShowPreviewDiagnosticsCheck.IsOn;
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
        _draft.FilenameFormat = GetTag(FilenameFormatCombo);
        _draft.ClipFolder = ClipFolderBox.Text;
        _draft.OrganizeClipsByGame = OrganiseClipsCheck.IsOn;
        _draft.GameDetectionMethod = GetTag(GameDetectionCombo);
        _draft.UnknownGameDestination = GetTag(UnknownGameCombo);
        _draft.SaveNotificationSound = NotificationSoundCheck.IsOn;
        _draft.NotificationStyle = GetTag(NotificationStyleCombo);
        _draft.Hotkey = GetTag(HotkeyCombo);
        _draft.ScreenshotFormat = GetTag(ScreenshotFormatCombo);
        _draft.ScreenshotsEnabled = ScreenshotsEnabledCheck.IsOn;
        _draft.QuickShareEnabled = QuickShareEnabledCheck.IsOn;
        _draft.QuickShareMethod = GetTag(QuickShareMethodCombo);
        _draft.QuickShareFailureAction = GetTag(QuickShareFailureCombo);
        _draft.QuickShareAutoUpload = QuickShareAutoUploadCheck.IsOn;
        _draft.QuickShareAutoCopyLink = QuickShareAutoCopyCheck.IsOn;
        _draft.QuickShareOpenAfterUpload = QuickShareOpenAfterCheck.IsOn;
        _draft.QuickShareShowProgress = QuickShareProgressCheck.IsOn;
        _draft.QuickShareShowNotification = QuickShareNotificationCheck.IsOn;
        _draft.QuickShareKeepHistory = QuickShareHistoryCheck.IsOn;
        _draft.ScreenshotJpegQuality = double.IsFinite(ScreenshotJpegQualityBox.Value)
            ? (int)Math.Round(ScreenshotJpegQualityBox.Value) : 90;
        _draft.CopyScreenshotToClipboard = CopyScreenshotCheck.IsOn;
        _draft.ScreenshotNotificationSound = ScreenshotSoundCheck.IsOn;
        _draft.ScreenshotHotkey = GetTag(ScreenshotHotkeyCombo);
        _draft.MicrophoneVolume = (int)Math.Round(SettingsMicrophoneVolumeSlider.Value);
        ScreenshotJpegQualityRow.Visibility = _draft.ScreenshotFormat == "jpg" ? Visibility.Visible : Visibility.Collapsed;
        var dirty = CountUnsavedOptions() > 0;
        SaveSettingsButton.IsEnabled = dirty;
        SettingsStatus.Text = dirty ? "Unsaved changes · press Save changes to apply." : "Settings loaded · no changes to save.";
        SettingsStatus.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(ColorHelper.FromArgb(255, 174, 182, 188));
        RefreshGameFolderPreview();
    }

    internal void RefreshGameFolderPreview()
    {
        if (GameFolderPreviewText is null) return;
        try
        {
            var (folder, confirmed) = _mainWindow.GetNextClipFolderPreview(_draft);
            GameFolderPreviewText.Text = folder;
            GameFolderPreviewStatus.Text = !_draft.OrganizeClipsByGame
                ? "Organisation is off. Clips save directly to the configured folder."
                : confirmed ? "Based on the dominant confirmed game in the current replay interval."
                : "No game has been confirmed for the current replay interval; this is the fallback destination.";
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            GameFolderPreviewText.Text = "Choose a valid clips folder";
            GameFolderPreviewStatus.Text = "The destination will update when the folder is valid.";
        }
    }

    private void RefreshGameMappingRows()
    {
        _gameMappingRows.Clear();
        foreach (var pair in _draft.CustomGameMappings.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
            _gameMappingRows.Add(new GameMappingRow(pair.Key,
                $"{pair.Key}  →  {pair.Value.DisplayName}  ·  {pair.Value.FolderName}"));
    }

    private async Task<(string Executable, string DisplayName, string FolderName)?> EditGameMappingAsync(
        string title, string executable = "", string displayName = "", string folderName = "")
    {
        var executableBox = new TextBox { Text = executable, PlaceholderText = "game.exe" };
        var displayBox = new TextBox { Text = displayName, PlaceholderText = "Game title" };
        var folderBox = new TextBox { Text = folderName, PlaceholderText = "Folder name" };
        var error = new TextBlock { Foreground = new SolidColorBrush(ColorHelper.FromArgb(255, 237, 142, 143)), TextWrapping = TextWrapping.Wrap };
        var content = new StackPanel { Spacing = 8 };
        content.Children.Add(new TextBlock { Text = "Executable name" });
        content.Children.Add(executableBox);
        content.Children.Add(new TextBlock { Text = "Display name" });
        content.Children.Add(displayBox);
        content.Children.Add(new TextBlock { Text = "Folder name" });
        content.Children.Add(folderBox);
        content.Children.Add(error);
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot, Title = title, Content = content,
            PrimaryButtonText = "Save mapping", CloseButtonText = "Cancel"
        };
        dialog.PrimaryButtonClick += (_, args) =>
        {
            var key = GameOrganisation.NormalizeExecutable(executableBox.Text);
            var folder = GameOrganisation.SafeFolderName(folderBox.Text);
            if (key is null || folder is null || string.IsNullOrWhiteSpace(displayBox.Text) || displayBox.Text.Trim().Length > 80)
            {
                error.Text = "Enter an executable name, a display name up to 80 characters, and a safe Windows folder name.";
                args.Cancel = true;
            }
            else if (!string.Equals(key, executable, StringComparison.OrdinalIgnoreCase) &&
                     _draft.CustomGameMappings.ContainsKey(key))
            {
                error.Text = "A custom mapping already exists for this executable.";
                args.Cancel = true;
            }
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return null;
        return (GameOrganisation.NormalizeExecutable(executableBox.Text)!, displayBox.Text.Trim(),
            GameOrganisation.SafeFolderName(folderBox.Text)!);
    }

    private async void OnAddGameMappingClick(object sender, RoutedEventArgs e)
    {
        var mapping = await EditGameMappingAsync("Add game mapping");
        if (mapping is null) return;
        _draft.CustomGameMappings[mapping.Value.Executable] = new GameMapping
        { DisplayName = mapping.Value.DisplayName, FolderName = mapping.Value.FolderName };
        RefreshGameMappingRows();
        UpdateDraftAndDirtyState();
    }

    private async void OnEditGameMappingClick(object sender, RoutedEventArgs e)
    {
        if (GameMappingList.SelectedItem is not GameMappingRow selected ||
            !_draft.CustomGameMappings.TryGetValue(selected.Executable, out var existing)) return;
        var mapping = await EditGameMappingAsync("Edit game mapping", selected.Executable,
            existing.DisplayName, existing.FolderName);
        if (mapping is null) return;
        _draft.CustomGameMappings.Remove(selected.Executable);
        _draft.CustomGameMappings[mapping.Value.Executable] = new GameMapping
        { DisplayName = mapping.Value.DisplayName, FolderName = mapping.Value.FolderName };
        RefreshGameMappingRows();
        UpdateDraftAndDirtyState();
    }

    private void OnRemoveGameMappingClick(object sender, RoutedEventArgs e)
    {
        if (GameMappingList.SelectedItem is not GameMappingRow selected) return;
        _draft.CustomGameMappings.Remove(selected.Executable);
        RefreshGameMappingRows();
        UpdateDraftAndDirtyState();
    }

    private void OnResetGameMappingsClick(object sender, RoutedEventArgs e)
    {
        _draft.CustomGameMappings.Clear();
        RefreshGameMappingRows();
        UpdateDraftAndDirtyState();
    }

    internal void RefreshReliabilityDiagnostics(string health, string status, bool healthy)
    {
        ReliabilityHealthText.Text = health;
        ReliabilityHealthStatus.Text = status;
        var color = healthy ? ColorHelper.FromArgb(255, 48, 199, 117) : ColorHelper.FromArgb(255, 244, 178, 70);
        ReliabilityHealthDot.Fill = new SolidColorBrush(color);
        ReliabilityHealthStatus.Foreground = new SolidColorBrush(color);
    }

    private async Task RefreshAppStorageUsageAsync()
    {
        try
        {
            var roots = new[]
            {
                AppContext.BaseDirectory,
                Windows.Storage.ApplicationData.Current.LocalFolder.Path,
                Windows.Storage.ApplicationData.Current.LocalCacheFolder.Path,
                Windows.Storage.ApplicationData.Current.RoamingFolder.Path,
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Clippr")
            }.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var clips = Path.GetFullPath(_draft.ClipFolder);
            var total = await Task.Run(() => MeasureAppFiles(roots, clips));
            AppStorageText.Text = $"ClipDat app files and data: {FormatAppSize(total)} · recordings excluded";
        }
        catch
        {
            AppStorageText.Text = "ClipDat app storage size unavailable";
        }
    }

    private static long MeasureAppFiles(string[] roots, string clips)
    {
        long total = 0;
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<string>(roots);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            if (!visited.Add(directory) || IsInside(directory, clips)) continue;
            try
            {
                if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) continue;
                foreach (var file in Directory.EnumerateFiles(directory))
                {
                    try
                    {
                        var info = new FileInfo(file);
                        if ((info.Attributes & FileAttributes.ReparsePoint) == 0) total += info.Length;
                    }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
                foreach (var child in Directory.EnumerateDirectories(directory)) pending.Push(child);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return total;
    }

    private static bool IsInside(string path, string parent) =>
        path.Equals(parent, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(parent.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static string FormatAppSize(long bytes) => bytes >= 1024L * 1024 * 1024
        ? $"{bytes / (1024d * 1024 * 1024):0.0} GB"
        : $"{bytes / (1024d * 1024):0.0} MB";

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
        if (_ready) VoiceSettingsStatus.Text = enabled ? "Save changes to start listening." : "Save changes to stop listening.";
        OnSettingChanged(sender, e);
    }

    private void OnVoiceCooldownChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_ready && !_applying) UpdateDraftAndDirtyState();
    }

    internal void RefreshVoiceStatus(string status)
    {
        if (VoiceCommandsCheck.IsOn) VoiceSettingsStatus.Text = status;
        UpdateVoiceListeningVisual(status);
    }

    private void UpdateVoiceListeningVisual(string status)
    {
        var listening = status.StartsWith("Listening with Windows", StringComparison.Ordinal);
        VoiceListeningDot.Fill = new SolidColorBrush(listening
            ? ColorHelper.FromArgb(255, 88, 201, 145)
            : ColorHelper.FromArgb(255, 123, 135, 147));
        VoiceListeningLabel.Text = listening ? "Listening" : "Not listening";
    }

    internal void RefreshVoiceTranscript(string words, string result)
    {
        VoiceTranscriptText.Text = words;
        VoiceTranscriptResultText.Text = result;
    }

    private void OnPreviewNotificationClick(object sender, RoutedEventArgs e)
    {
        _mainWindow.PreviewNotification(GetTag(NotificationStyleCombo));
    }

    [DllImport("user32.dll")]
    private static extern short GetKeyState(int virtualKey);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint virtualKey);
    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr window, int id);

    private async void OnEditHotkeyClick(object sender, RoutedEventArgs e)
    {
        var entry = new TextBox { Text = _draft.Hotkey.Replace("+", " + ", StringComparison.Ordinal),
            IsReadOnly = true, PlaceholderText = "Press Ctrl + Alt/Shift + F1–F12" };
        var message = new TextBlock { Text = "Press a shortcut. It must differ from the screenshot hotkey.",
            TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(ColorHelper.FromArgb(255, 174, 182, 188)) };
        var content = new StackPanel { Spacing = 10 };
        content.Children.Add(entry);
        content.Children.Add(message);
        var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = "Edit clipping hotkey", Content = content,
            PrimaryButtonText = "Use shortcut", CloseButtonText = "Cancel", IsPrimaryButtonEnabled = false };
        string? captured = null;
        entry.KeyDown += (_, args) =>
        {
            var key = (int)args.Key;
            var ctrl = GetKeyState(0x11) < 0;
            var alt = GetKeyState(0x12) < 0;
            var shift = GetKeyState(0x10) < 0;
            if (key is < 0x70 or > 0x7B || !ctrl || alt == shift) return;
            captured = $"Ctrl+{(alt ? "Alt" : "Shift")}+F{key - 0x6F}";
            entry.Text = captured.Replace("+", " + ", StringComparison.Ordinal);
            var conflict = captured == GetTag(ScreenshotHotkeyCombo);
            var available = captured == _savedSnapshot.Hotkey;
            if (!conflict && !available && ClipHotkey.TryParse(captured, out var modifiers, out var virtualKey))
            {
                available = RegisterHotKey(IntPtr.Zero, 0x4D4C, modifiers | 0x4000u, virtualKey);
                if (available) UnregisterHotKey(IntPtr.Zero, 0x4D4C);
            }
            message.Text = conflict ? "That shortcut is already used for screenshots."
                : !available ? "That shortcut is in use or unavailable on this PC." : "Ready to save this shortcut.";
            dialog.IsPrimaryButtonEnabled = !conflict && available;
            args.Handled = true;
        };
        dialog.Opened += (_, _) => entry.Focus(FocusState.Programmatic);
        if (await dialog.ShowAsync() != ContentDialogResult.Primary || captured is null) return;
        if (!HotkeyCombo.Items.OfType<ComboBoxItem>().Any(item => Equals(item.Tag, captured)))
            AddOptions(HotkeyCombo, (captured, captured.Replace("+", " + ", StringComparison.Ordinal)));
        SelectTag(HotkeyCombo, captured);
        UpdateDraftAndDirtyState();
    }

    private async void OnSaveSettingsClick(object sender, RoutedEventArgs e)
    {
        await SaveSettingsAsync();
    }

    private async Task<bool> SaveSettingsAsync()
    {
        if (!_ready || _applying) return false;
        UpdateDraftAndDirtyState();
        var validationError = _draft.Validate();
        if (validationError is not null)
        {
            SettingsStatus.Text = validationError;
            SettingsStatus.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(ColorHelper.FromArgb(255, 237, 142, 143));
            return false;
        }
        var persisted = false;
        try
        {
            Directory.CreateDirectory(_draft.ClipFolder);
            _draft.SaveAtomicJson();
            persisted = true;
            _savedSnapshot = _draft.Clone();
            _applying = true;
            SettingsNavigation.IsEnabled = false;
            SaveSettingsButton.IsEnabled = false;
            await _mainWindow.ApplySettingsAsync(_draft);
            ClipFolderBox.Text = _draft.ClipFolder;
            SettingsStatus.Text = "Settings saved · some recording changes apply to the next recording.";
            SettingsStatus.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(ColorHelper.FromArgb(255, 174, 182, 188));
            return true;
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
        return false;
    }

    internal int GetUnsavedOptionCount()
    {
        UpdateDraftAndDirtyState();
        return CountUnsavedOptions();
    }

    private int CountUnsavedOptions()
    {
        var count = 0;
        foreach (var property in typeof(ClipprSettings).GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            if (property.Name is nameof(ClipprSettings.VoiceCommandPhrases) or nameof(ClipprSettings.CustomGameMappings)) continue;
            if (!Equals(property.GetValue(_draft), property.GetValue(_savedSnapshot))) count++;
        }
        var phraseKeys = _draft.VoiceCommandPhrases.Keys.Union(_savedSnapshot.VoiceCommandPhrases.Keys, StringComparer.Ordinal);
        foreach (var key in phraseKeys)
        {
            _draft.VoiceCommandPhrases.TryGetValue(key, out var current);
            _savedSnapshot.VoiceCommandPhrases.TryGetValue(key, out var saved);
            if (!string.Equals(current, saved, StringComparison.Ordinal)) count++;
        }
        var gameKeys = _draft.CustomGameMappings.Keys.Union(_savedSnapshot.CustomGameMappings.Keys,
            StringComparer.OrdinalIgnoreCase);
        foreach (var key in gameKeys)
        {
            _draft.CustomGameMappings.TryGetValue(key, out var current);
            _savedSnapshot.CustomGameMappings.TryGetValue(key, out var saved);
            if (current?.DisplayName != saved?.DisplayName || current?.FolderName != saved?.FolderName) count++;
        }
        return count;
    }

    internal async Task<bool> ConfirmLeaveAsync(bool exiting)
    {
        var count = GetUnsavedOptionCount();
        if (count == 0) return true;
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = exiting ? "Are you sure you want to exit?" : "Leave settings without saving?",
            Content = $"You have {count} unsaved {(count == 1 ? "option" : "options")}. If you continue, these changes will be discarded.",
            PrimaryButtonText = "Save changes",
            SecondaryButtonText = "Discard",
            CloseButtonText = "Keep editing",
            DefaultButton = ContentDialogButton.Primary
        };
        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
            return await SaveSettingsAsync() && GetUnsavedOptionCount() == 0;
        return result == ContentDialogResult.Secondary;
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

    private async void OnOpenClipsFolderClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var folder = Path.GetFullPath(_savedSnapshot.ClipFolder);
            Directory.CreateDirectory(folder);
            WindowsShellFileActions.OpenWithDefaultApplication(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.ComponentModel.Win32Exception or COMException)
        {
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "Could not open Clips Folder",
                Content = ex.Message,
                CloseButtonText = "Close"
            };
            await dialog.ShowAsync();
        }
    }

    private async void OnOpenOutputFolderClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var folder = Path.GetFullPath(ClipFolderBox.Text.Trim());
            if (!Directory.Exists(folder)) throw new DirectoryNotFoundException("This folder does not exist yet. Save changes first to create it.");
            WindowsShellFileActions.OpenWithDefaultApplication(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.ComponentModel.Win32Exception or COMException)
        {
            var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = "Could not open folder",
                Content = ex.Message, CloseButtonText = "Close" };
            await dialog.ShowAsync();
        }
    }

    private async void OnOpenClipLibraryClick(object sender, RoutedEventArgs e)
    {
        await _mainWindow.OpenLibraryAsync();
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
