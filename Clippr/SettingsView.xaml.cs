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
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
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
    private ClipprSettings _savedSnapshot;
    private bool _ready;
    private bool _applying;
    private bool _transitioning;
    private bool? _captureSourceCompact;
    private FrameworkElement? _currentPage;
    private FrameworkElement? _pendingPage;
    private string _feedbackUrl = "";
    private readonly Dictionary<string, Border> _voiceCommandRows = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Border> _voiceCommandGroups = new(StringComparer.Ordinal);
    private string _voiceCommandCategory = "All";

    public SettingsView(MainWindow mainWindow, ClipprSettings settings)
    {
        InitializeComponent();
        _mainWindow = mainWindow;
        _draft = settings.Clone();
        _savedSnapshot = settings.Clone();
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
        CapturePage.SizeChanged += (_, _) => UpdateCaptureSourceLayout();
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
        UpdateVoiceListeningVisual(_mainWindow.CurrentVoiceStatus);
        VoiceTranscriptText.Text = _mainWindow.CurrentVoiceTranscript;
        VoiceTranscriptResultText.Text = _mainWindow.CurrentVoiceTranscriptResult;
        PopulateVoiceCommandRows();
        UpdateVoiceFilterButtons();
        ScreenshotJpegQualityBox.Value = _draft.ScreenshotJpegQuality;
        ScreenshotJpegQualityRow.Visibility = _draft.ScreenshotFormat == "jpg" ? Visibility.Visible : Visibility.Collapsed;
        CopyScreenshotCheck.IsOn = _draft.CopyScreenshotToClipboard;
        ScreenshotSoundCheck.IsOn = _draft.ScreenshotNotificationSound;
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

        PopulateDisplays();
        var applications = _mainWindow.EnumerateSettingsApplications();
        PopulateApplications(PreferredApplicationCombo, _draft.PreferredApplicationProcessId, applications);
        PopulateAudioDevices(renderDevices: true, OutputDeviceCombo, _draft.AudioOutputId);
        PopulateAudioDevices(renderDevices: false, InputDeviceCombo, _draft.AudioInputId);
        PopulateApplications(GameAudioCombo, _draft.GameAudioProcessId, applications);
        PopulateApplications(DiscordAudioCombo, _draft.DiscordAudioProcessId, applications);
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

    private static void PopulateApplications(ComboBox combo, uint selectedPid, NativeCapture.Window[] applications)
    {
        combo.Items.Clear();
        combo.Items.Add(new ComboBoxItem { Content = "None", Tag = "0" });
        foreach (var app in applications)
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
        AdvancedPreviewDiagnosticsCard.Visibility = visibility;
        AdvancedEncoderRow.Visibility = visibility;
        AdvancedApplicationAudioCard.Visibility = visibility;
        AdvancedReplayMemoryRow.Visibility = visibility;
        AdvancedNotificationRow.Visibility = visibility;
        AdvancedHotkeyRow.Visibility = visibility;
        AdvancedFeedbackActions.Visibility = visibility;
        if (_ready && !_applying) UpdateDraftAndDirtyState();
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
            SettingsStatus.Text = "Settings saved · recording changes apply to the next recording.";
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
        var previousStatus = SettingsStatus.Text;
        var previousBrush = SettingsStatus.Foreground;
        UpdateDraftAndDirtyState();
        var count = 0;
        foreach (var property in typeof(ClipprSettings).GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            if (property.Name == nameof(ClipprSettings.VoiceCommandPhrases)) continue;
            if (!Equals(property.GetValue(_draft), property.GetValue(_savedSnapshot))) count++;
        }
        var phraseKeys = _draft.VoiceCommandPhrases.Keys.Union(_savedSnapshot.VoiceCommandPhrases.Keys, StringComparer.Ordinal);
        foreach (var key in phraseKeys)
        {
            _draft.VoiceCommandPhrases.TryGetValue(key, out var current);
            _savedSnapshot.VoiceCommandPhrases.TryGetValue(key, out var saved);
            if (!string.Equals(current, saved, StringComparison.Ordinal)) count++;
        }
        if (count == 0)
        {
            SettingsStatus.Text = previousStatus;
            SettingsStatus.Foreground = previousBrush;
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
