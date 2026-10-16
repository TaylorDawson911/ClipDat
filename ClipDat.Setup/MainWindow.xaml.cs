using System.Diagnostics;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Storage.Pickers;

namespace ClipDat.Setup;

public sealed partial class MainWindow : Window
{
    private enum Step { Welcome, Locations, Features, Recording, Audio, Experience, Permissions, Review, Installing, Complete }
    private static readonly SolidColorBrush Muted = Brush(170, 186, 199);
    private static readonly SolidColorBrush Green = Brush(54, 205, 137);
    private static readonly SolidColorBrush White = Brush(244, 248, 251);
    private readonly Dictionary<Step, StackPanel> _pages = new();
    private readonly Dictionary<SetupMode, Border> _modeCards = new();
    private List<Step> _steps = [];
    private int _stepIndex;
    private SetupMode _mode = SetupMode.Express;
    private readonly bool _preview = Environment.GetCommandLineArgs().Any(a => a.Equals("--preview", StringComparison.OrdinalIgnoreCase));
    private readonly string _defaultInstall = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "ClipDat");
    private readonly string _defaultClips = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "ClipDat");

    private TextBox _installPath = null!;
    private TextBox _clipPath = null!;
    private ToggleSwitch _keepExisting = null!;
    private ToggleSwitch _library = null!;
    private ToggleSwitch _screenshots = null!;
    private ToggleSwitch _voice = null!;
    private ToggleSwitch _quickShare = null!;
    private ToggleSwitch _gameFolders = null!;
    private ToggleSwitch _desktopAudio = null!;
    private ToggleSwitch _microphoneAudio = null!;
    private ToggleSwitch _startup = null!;
    private ToggleSwitch _desktopShortcut = null!;
    private ToggleSwitch _tray = null!;
    private ToggleSwitch _saveSound = null!;
    private ComboBox _notification = null!;
    private ComboBox _length = null!;
    private ComboBox _quality = null!;
    private ComboBox _format = null!;
    private ComboBox _fps = null!;
    private ComboBox _encoder = null!;
    private TextBlock _permissionStatus = null!;
    private TextBlock _reviewText = null!;
    private TextBlock _installStatus = null!;
    private ToggleSwitch _launch = null!;

    public MainWindow()
    {
        InitializeComponent();
        AppWindow.Title = "ClipDat Setup";
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBar);
        var workArea = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        var width = Math.Min(980, Math.Max(600, workArea.Width - 32));
        var height = Math.Min(810, Math.Max(560, workArea.Height - 32));
        AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(
            workArea.X + (workArea.Width - width) / 2,
            workArea.Y + (workArea.Height - height) / 2, width, height));
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "ClipprTray.ico"));
        BuildPages();
        SetSteps();
        ShowStep();
    }

    private static SolidColorBrush Brush(byte r, byte g, byte b) =>
        new(ColorHelper.FromArgb(255, r, g, b));

    private static TextBlock Text(string value, int size = 13, bool muted = false, bool bold = false) =>
        new() { Text = value, FontSize = size, Foreground = muted ? Muted : White,
                FontWeight = bold ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
                TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };

    private static Border Card(UIElement child) => new()
    {
        Background = Brush(32, 42, 52), BorderBrush = Brush(51, 68, 84), BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(12), Padding = new Thickness(17), Child = child
    };

    private static StackPanel Page() => new() { Spacing = 12 };

    private static ToggleSwitch Toggle(StackPanel page, string label, string explanation, bool enabled)
    {
        var row = new Grid { ColumnDefinitions = { new ColumnDefinition(), new ColumnDefinition { Width = GridLength.Auto } },
                             MinHeight = 53 };
        var copy = new StackPanel { Spacing = 3, VerticalAlignment = VerticalAlignment.Center };
        copy.Children.Add(Text(label, 14, bold: true));
        copy.Children.Add(Text(explanation, 11, muted: true));
        row.Children.Add(copy);
        var toggle = new ToggleSwitch { IsOn = enabled, OnContent = "", OffContent = "", Width = 52,
            VerticalAlignment = VerticalAlignment.Center };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(toggle, label);
        Grid.SetColumn(toggle, 1);
        row.Children.Add(toggle);
        page.Children.Add(Card(row));
        return toggle;
    }

    private static ComboBox Choice(StackPanel page, string label, params string[] options)
    {
        var content = new StackPanel { Spacing = 8 };
        content.Children.Add(Text(label, 13, bold: true));
        var combo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var option in options) combo.Items.Add(option);
        combo.SelectedIndex = 0;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(combo, label);
        content.Children.Add(combo);
        page.Children.Add(Card(content));
        return combo;
    }

    private void BuildPages()
    {
        var welcome = Page();
        welcome.Children.Add(Text("Pick a setup. You can change these settings in ClipDat later.", 13, muted: true));
        AddModeCard(welcome, SetupMode.Express, "Express", "Recommended · ClipDat defaults",
            "● Replay clipping and recording", "● Screenshots, game folders and Clip Library", "● Quick Share available when you choose to use it");
        AddModeCard(welcome, SetupMode.BareBones, "BareBones", "Only the essentials",
            "● Replay clipping and recording", "● Desktop audio", "● Library, voice, screenshots and sharing turned off");
        AddModeCard(welcome, SetupMode.Custom, "Custom", "Choose each category",
            "● Pick features one by one", "● Set recording and audio defaults", "● Choose notifications and startup behaviour");
        _pages[Step.Welcome] = welcome;

        var locations = Page();
        locations.Children.Add(Text("Both paths can be changed. Clips should stay outside the app folder so uninstalling never removes them.", 12, muted: true));
        _installPath = AddFolder(locations, "Install ClipDat to", _defaultInstall);
        _clipPath = AddFolder(locations, "Save clips to", SetupSettings.ExistingClipFolder() ?? _defaultClips);
        if (File.Exists(SetupSettings.SettingsPath))
            _keepExisting = Toggle(locations, "Keep current settings", "Optional for upgrades. When on, only the clips folder above changes.", false);
        else _keepExisting = new ToggleSwitch { IsOn = false };
        _pages[Step.Locations] = locations;

        var features = Page();
        _library = Toggle(features, "Clip Library", "Browse, preview and manage saved clips inside ClipDat.", true);
        _screenshots = Toggle(features, "Screenshots", "Take and save screenshots with the app or hotkey.", true);
        _voice = Toggle(features, "Voice Control", "Use installed offline Windows speech recognition.", false);
        _quickShare = Toggle(features, "Quick Share", "Enable manual link sharing; nothing uploads automatically.", true);
        _gameFolders = Toggle(features, "Game folders", "Organise clips into game folders automatically.", true);
        _pages[Step.Features] = features;

        var recording = Page();
        _length = Choice(recording, "Replay clip length", "10 seconds", "20 seconds", "30 seconds", "60 seconds", "2 minutes", "15 minutes");
        _length.SelectedIndex = 2;
        _quality = Choice(recording, "Quality", "Standard", "High");
        _format = Choice(recording, "Replay clip format", "MP4", "MKV");
        _fps = Choice(recording, "Frame rate", "30 FPS", "60 FPS");
        _encoder = Choice(recording, "H.264 encoder", "Hardware", "Automatic", "Software");
        _pages[Step.Recording] = recording;

        var audio = Page();
        _desktopAudio = Toggle(audio, "Desktop audio", "Record system and application sound.", true);
        _microphoneAudio = Toggle(audio, "Microphone audio", "Record your microphone on a separate track.", false);
        audio.Children.Add(Text("Audio device and application track choices remain available in ClipDat settings.", 12, muted: true));
        _pages[Step.Audio] = audio;

        var experience = Page();
        _notification = Choice(experience, "Notifications", "Windows", "ClipDat", "None");
        _saveSound = Toggle(experience, "Clip saved sound", "Play a sound after saving a clip.", false);
        _startup = Toggle(experience, "Start with Windows", "Launch ClipDat when you sign in.", false);
        _tray = Toggle(experience, "Minimize to tray on close", "Keep ClipDat available in the notification area.", true);
        _desktopShortcut = Toggle(experience, "Desktop shortcut", "Put a ClipDat shortcut on the desktop.", false);
        _pages[Step.Experience] = experience;

        var permissions = Page();
        permissions.Children.Add(Card(Text("ClipDat installs for your Windows account. It does not need administrator access in a writable folder.", 13)));
        permissions.Children.Add(Card(Text("Windows may ask to allow screen capture or microphone access when you first use those features. Setup cannot grant those permissions for you.", 13)));
        permissions.Children.Add(Card(Text("When upgrading, finish any active recording and choose Exit ClipDat from its tray menu before installing.", 12, muted: true)));
        var privacy = new Button { Content = "Open microphone privacy settings", HorizontalAlignment = HorizontalAlignment.Left };
        privacy.Click += (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo("ms-settings:privacy-microphone") { UseShellExecute = true }); }
            catch (Exception ex) { _permissionStatus.Text = "Could not open Windows privacy settings: " + ex.Message; }
        };
        permissions.Children.Add(privacy);
        var testAccess = new Button { Content = "Check folder access", HorizontalAlignment = HorizontalAlignment.Left };
        testAccess.Click += (_, _) => CheckFolders();
        permissions.Children.Add(testAccess);
        _permissionStatus = Text("ClipDat will check both folders before installing.", 12, muted: true);
        permissions.Children.Add(_permissionStatus);
        _pages[Step.Permissions] = permissions;

        var review = Page();
        _reviewText = Text("", 13);
        review.Children.Add(Card(_reviewText));
        review.Children.Add(Text("Existing recordings are never removed. Quick Share never uploads automatically.", 12, muted: true));
        _pages[Step.Review] = review;

        var installing = Page();
        installing.Children.Add(new ProgressRing { IsActive = true, Width = 48, Height = 48, HorizontalAlignment = HorizontalAlignment.Left });
        _installStatus = Text("Installing ClipDat…", 16, bold: true);
        installing.Children.Add(_installStatus);
        _pages[Step.Installing] = installing;

        var complete = Page();
        complete.Children.Add(Card(Text("✓  ClipDat is ready.", 19, bold: true)));
        _launch = Toggle(complete, "Launch ClipDat now", "Open the recorder after finishing setup.", true);
        _pages[Step.Complete] = complete;
    }

    private TextBox AddFolder(StackPanel page, string label, string initial)
    {
        var content = new StackPanel { Spacing = 8 };
        content.Children.Add(Text(label, 13, bold: true));
        var row = new Grid { ColumnDefinitions = { new ColumnDefinition(), new ColumnDefinition { Width = GridLength.Auto } }, ColumnSpacing = 8 };
        var box = new TextBox { Text = initial, HorizontalAlignment = HorizontalAlignment.Stretch };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(box, label);
        row.Children.Add(box);
        var browse = new Button { Content = "Browse…" };
        browse.Click += async (_, _) =>
        {
            try
            {
                var picker = new FolderPicker();
                picker.FileTypeFilter.Add("*");
                WinRT.Interop.InitializeWithWindow.Initialize(picker, new IntPtr(unchecked((long)AppWindow.Id.Value)));
                var selected = await picker.PickSingleFolderAsync();
                if (selected is not null) box.Text = selected.Path;
            }
            catch (Exception ex) { FooterStatus.Text = "Could not browse folders: " + ex.Message; }
        };
        Grid.SetColumn(browse, 1);
        row.Children.Add(browse);
        content.Children.Add(row);
        page.Children.Add(Card(content));
        return box;
    }

    private void AddModeCard(StackPanel page, SetupMode mode, string title, string subtitle, params string[] bullets)
    {
        var content = new StackPanel { Spacing = 6 };
        content.Children.Add(Text(title, 18, bold: true));
        content.Children.Add(Text(subtitle, 12, muted: true));
        foreach (var bullet in bullets) content.Children.Add(new TextBlock { Text = bullet, Foreground = Green, FontSize = 12 });
        var card = Card(content);
        card.Padding = new Thickness(18, 13, 18, 13);
        var button = new Button { Content = card, HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch, Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0), Padding = new Thickness(0) };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, title + " install");
        button.Click += (_, _) => { _mode = mode; HighlightMode(); SetSteps(); ShowStep(); };
        _modeCards[mode] = card;
        page.Children.Add(button);
        HighlightMode();
    }

    private void HighlightMode()
    {
        foreach (var (mode, card) in _modeCards)
        {
            card.Background = mode == _mode ? Brush(25, 55, 83) : Brush(32, 42, 52);
            card.BorderBrush = mode == _mode ? Brush(48, 150, 237) : Brush(51, 68, 84);
            card.BorderThickness = new Thickness(mode == _mode ? 2 : 1);
        }
    }

    private void SetSteps()
    {
        var step = _steps.Count > 0 ? _steps[Math.Min(_stepIndex, _steps.Count - 1)] : Step.Welcome;
        _steps = _mode == SetupMode.Custom
            ? [Step.Welcome, Step.Locations, Step.Features, Step.Recording, Step.Audio, Step.Experience, Step.Permissions, Step.Review, Step.Installing, Step.Complete]
            : [Step.Welcome, Step.Locations, Step.Permissions, Step.Review, Step.Installing, Step.Complete];
        _stepIndex = Math.Max(0, _steps.IndexOf(step));
    }

    private void ShowStep()
    {
        var step = _steps[_stepIndex];
        PageTitle.Text = step switch
        {
            Step.Welcome => "Welcome to ClipDat", Step.Locations => "Choose locations", Step.Features => "Choose features",
            Step.Recording => "Recording", Step.Audio => "Audio", Step.Experience => "Experience",
            Step.Permissions => "Permissions", Step.Review => "Review your setup",
            Step.Installing => "Installing", _ => "Installation complete"
        };
        PageSubtitle.Text = step switch
        {
            Step.Welcome => "A recorder built for fast clips. Choose a setup that fits you.",
            Step.Locations => "Choose where the app and your clips live.",
            Step.Features => "Turn optional parts on or off.",
            Step.Recording => "Set replay length, quality and encoding.",
            Step.Audio => "Choose the audio you want in clips.",
            Step.Experience => "Set notifications and startup behaviour.",
            Step.Permissions => "Check folder access and Windows privacy choices.",
            Step.Review => "Check everything before installation.",
            Step.Installing => "Copying files and applying your choices.",
            _ => "Your setup is complete."
        };
        if (step == Step.Review) UpdateReview();
        PageContent.Children.Clear();
        PageContent.Children.Add(_pages[step]);
        StepList.Children.Clear();
        foreach (var candidate in _steps.Where(s => s is not (Step.Installing or Step.Complete)))
        {
            var index = _steps.IndexOf(candidate);
            var label = Text($"{index + 1:00}   {candidate}", 12, muted: index != _stepIndex, bold: index == _stepIndex);
            label.Margin = new Thickness(0, 5, 0, 5);
            StepList.Children.Add(label);
        }
        BackButton.IsEnabled = _stepIndex > 0 && step is not (Step.Installing or Step.Complete);
        NextButton.IsEnabled = step != Step.Installing;
        NextButton.Content = step switch { Step.Review => _preview ? "Preview finish" : "Install ClipDat", Step.Complete => "Finish", _ => "Next" };
        FooterStatus.Text = step switch
        {
            Step.Installing => "Please keep this window open.",
            Step.Complete => _preview ? "Preview finished · no files were changed." : "ClipDat is installed for your Windows account.",
            _ => $"{_mode} setup · step {_stepIndex + 1} of {_steps.Count - 2}"
        };
    }

    private void UpdateReview()
    {
        var choices = ReadChoices();
        _reviewText.Text = $"Setup: {choices.Mode}\n\n" +
            $"App folder: {_installPath.Text}\nClips folder: {_clipPath.Text}\n" +
            $"Current settings: {(choices.KeepExisting ? "Keep choices, update clips folder" : "Apply selected setup")}\n\n" +
            (choices.Mode == SetupMode.Custom
                ? $"Library: {OnOff(choices.Library)} · Screenshots: {OnOff(choices.Screenshots)} · Voice: {OnOff(choices.Voice)}\n" +
                  $"Quick Share: {OnOff(choices.QuickShare)} · Game folders: {OnOff(choices.GameFolders)}\n" +
                  $"Replay: {choices.ClipLengthSeconds}s · {choices.QualityPreset} · {choices.VideoFormat.ToUpperInvariant()} · {choices.FrameRate} FPS\n" +
                  $"Audio: desktop {OnOff(choices.DesktopAudio)}, microphone {OnOff(choices.MicrophoneAudio)}\n" +
                  $"Notifications: {choices.NotificationStyle} · Startup: {OnOff(choices.StartWithWindows)}\n"
                : choices.Mode == SetupMode.Express
                    ? "Replay, recording, screenshots, Clip Library and game folders on. Voice off.\n"
                    : "Replay and recording on. Library, screenshots, voice and sharing off.\n") +
            $"Desktop shortcut: {OnOff(choices.DesktopShortcut)}";
    }

    private static string OnOff(bool value) => value ? "On" : "Off";

    private SetupChoices ReadChoices() => new(
        _mode, _clipPath.Text.Trim(), _keepExisting.IsOn,
        _library.IsOn, _screenshots.IsOn, _voice.IsOn, _quickShare.IsOn, _gameFolders.IsOn,
        _desktopAudio.IsOn, _microphoneAudio.IsOn, _startup.IsOn, _desktopShortcut.IsOn,
        _tray.IsOn, _saveSound.IsOn, _notification.SelectedItem?.ToString() ?? "Windows",
        _length.SelectedIndex switch { 0 => 10, 1 => 20, 3 => 60, 4 => 120, 5 => 900, _ => 30 },
        _quality.SelectedItem?.ToString() ?? "Standard",
        (_format.SelectedItem?.ToString() ?? "MP4").ToLowerInvariant(),
        _fps.SelectedIndex == 1 ? 60u : 30u,
        _encoder.SelectedIndex switch { 1 => "MediaFoundationAuto", 2 => "MediaFoundationSoftware", _ => "MediaFoundationHardware" });

    private static void CheckWritable(string path)
    {
        Directory.CreateDirectory(path);
        var probe = Path.Combine(path, ".clipdat-write-check-" + Guid.NewGuid().ToString("N"));
        try { File.WriteAllText(probe, "ClipDat setup access check"); }
        finally { if (File.Exists(probe)) File.Delete(probe); }
    }

    private (string Install, string Clips) ValidPaths()
    {
        if (!Path.IsPathFullyQualified(_installPath.Text.Trim()) || !Path.IsPathFullyQualified(_clipPath.Text.Trim()))
            throw new InvalidOperationException("Choose full paths for the app and clips folders.");
        var install = Path.GetFullPath(_installPath.Text.Trim()).TrimEnd(Path.DirectorySeparatorChar);
        var clips = Path.GetFullPath(_clipPath.Text.Trim()).TrimEnd(Path.DirectorySeparatorChar);
        if (install.Length < 4 || clips.Length < 4) throw new InvalidOperationException("Choose folders below a drive, not the drive root.");
        if (clips.Equals(install, StringComparison.OrdinalIgnoreCase) ||
            clips.StartsWith(install + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Choose a clips folder outside the app folder.");
        return (install, clips);
    }

    private void CheckFolders()
    {
        try
        {
            var paths = ValidPaths();
            CheckWritable(paths.Install);
            CheckWritable(paths.Clips);
            _permissionStatus.Text = "✓  Both folders are writable for your Windows account.";
            _permissionStatus.Foreground = Green;
        }
        catch (Exception ex)
        {
            _permissionStatus.Text = "Folder access failed: " + ex.Message;
            _permissionStatus.Foreground = Brush(235, 135, 135);
        }
    }

    private void OnBackClick(object sender, RoutedEventArgs e)
    {
        if (_stepIndex > 0) { _stepIndex--; ShowStep(); }
    }

    private async void OnNextClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var step = _steps[_stepIndex];
            if (step == Step.Complete)
            {
                if (!_preview && _launch.IsOn)
                    Process.Start(new ProcessStartInfo(Path.Combine(Path.GetFullPath(_installPath.Text.Trim()), "Clippr.exe")) { UseShellExecute = true });
                Close();
                return;
            }
            if (step == Step.Locations) _ = ValidPaths();
            if (step == Step.Review)
            {
                _stepIndex++;
                ShowStep();
                if (_preview) { _stepIndex++; ShowStep(); return; }
                await InstallAsync();
                return;
            }
            _stepIndex++;
            ShowStep();
        }
        catch (Exception ex)
        {
            FooterStatus.Text = ex.Message;
        }
    }

    private async Task InstallAsync()
    {
        try
        {
            var paths = ValidPaths();
            var choices = ReadChoices();
            var payload = Path.Combine(AppContext.BaseDirectory, "ClipDat-Payload.exe");
            if (!File.Exists(payload)) throw new FileNotFoundException("The ClipDat install payload is missing.", payload);
            using (var running = Process.GetProcessesByName("Clippr").FirstOrDefault())
                if (running is not null)
                    throw new InvalidOperationException("ClipDat is running. Finish any recording, choose Exit ClipDat from its tray menu, then try Install again.");
            await Task.Run(() =>
            {
                CheckWritable(paths.Install);
                CheckWritable(paths.Clips);
                var start = new ProcessStartInfo(payload) { UseShellExecute = false, CreateNoWindow = true };
                foreach (var arg in new[] { "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/CLOSEAPPLICATIONS",
                           "/DIR=" + paths.Install, choices.DesktopShortcut ? "/TASKS=desktopicon" : "/TASKS=!desktopicon" })
                    start.ArgumentList.Add(arg);
                using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start the ClipDat file installer.");
                process.WaitForExit();
                if (process.ExitCode != 0) throw new InvalidOperationException($"File installation failed (code {process.ExitCode}).");
                SetupSettings.Save(choices);
            });
            _stepIndex++;
            ShowStep();
        }
        catch (Exception ex)
        {
            _stepIndex = _steps.IndexOf(Step.Review);
            ShowStep();
            FooterStatus.Text = "Install failed: " + ex.Message;
        }
    }
}
