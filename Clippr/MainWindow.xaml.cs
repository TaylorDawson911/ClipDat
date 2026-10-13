using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI;
using Windows.Data.Xml.Dom;
using Windows.Foundation;
using Windows.Foundation.Collections;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;
using Windows.UI.Notifications;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace Clippr
{
    /// <summary>
    /// An empty window that can be used on its own or navigated to within a Frame.
    /// </summary>
    public sealed partial class MainWindow : Window
    {
        private enum CaptureSource { Screen, Application }

        private readonly SemaphoreSlim _captureGate = new(1, 1);
        private readonly DispatcherTimer _captureTimer = new() { Interval = TimeSpan.FromSeconds(1) };
        private readonly DispatcherTimer _audioMeterTimer = new() { Interval = TimeSpan.FromMilliseconds(60) };
        private readonly DispatcherTimer _reliabilityTimer = new() { Interval = TimeSpan.FromSeconds(5) };
        private readonly double[] _displayedAudioLevels = new double[5];
        private ClipprSettings _settings;
        private NativeCapture.Display[] _displays = Array.Empty<NativeCapture.Display>();
        private NativeCapture.Window[] _windows = Array.Empty<NativeCapture.Window>();
        private IntPtr _capture;
        private IntPtr _selectedMonitor;
        private IntPtr _selectedWindow;
        private IntPtr _activeTarget;
        private CaptureSource _selectedSource = CaptureSource.Screen;
        private CaptureSource? _activeSource;
        private int _windowRefreshGeneration;
        private bool _closing;
        private bool _exitRequested;
        private bool _capturePaused;
        private bool _recovering;
        private bool _reconnecting;
        private bool _recording;
        private uint _recordingFps = 30;
        private uint _recordingBitrate = 8_000_000;
        private bool _recordingFullHd;
        private uint _replaySeconds = 30;
        private ulong _replayRamMb = 128;
        private ulong _replayCompletedSeen;
        private ulong _replayFailedSeen;
        private bool _replayReadyNotified;
        private ClipNotificationWindow? _notificationWindow;
        private long _diagnosticSampleTimestamp;
        private ulong _diagnosticCaptureFrames;
        private ulong _diagnosticReplayFrames;
        private readonly Queue<DateTime> _notificationHistory = new();
        private readonly Queue<DateTime> _captureRecoveryAttempts = new();
        private readonly Queue<DateTime> _audioRecoveryAttempts = new();
        private readonly Dictionary<string, DateTime> _notificationLastShown = new(StringComparer.Ordinal);
        private string? _lastReplayPath;
        private string? _lastScreenshotPath;
        private string? _lastSavedFilePath;
        private ulong _screenshotsCompletedSeen;
        private int _screenshotErrorSeen;
        private readonly Queue<string> _pendingReplayPaths = new();
        private int _shareExpirySelection;
        private DateTime _replayMessageUntil;
        private DateTime _infoErrorUntil;
        private string? _infoError;
        private string _clipFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "ClipDat");
        private string _clipFormat = "mp4";
        private int _clipSequence;
        private SettingsView? _settingsView;
        private VoiceCommandService? _voiceCommandService;
        private string _voiceStatus = "Voice commands are off.";
        private Microsoft.UI.Dispatching.DispatcherQueue? _dispatcherQueue;
        private SystemTrayController? _tray;
        private string _preferredDisplayName = "";
        private string _audioOutputId = "";
        private string _audioInputId = "";
        private uint _gameAudioPid;
        private uint _chatAudioPid;
        private uint _musicAudioPid;
        private readonly DispatcherTimer _audioSaveTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
        private bool _startCaptureAutomatically = true;
        private bool _clippingEnabled = true;
        private bool _audioUiReady;
        private bool _audioApplying;
        private bool _additionalAudioExpanded;
        private Microsoft.UI.Dispatching.DispatcherQueueTimer? _additionalAudioAnimation;
        private DateTime _lastStorageSampleUtc;
        private DateTime _lastAudioRecoveryUtc;
        private DateTime _lastCaptureRecoveryUtc;
        private bool _storageWarningActive;
        private bool _suspended;
        private bool _resumeCaptureAfterSleep;
        private int _lastCaptureError;
        private string _lastAudioErrorKey = "";

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "GetDiskFreeSpaceExW")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetDiskFreeSpaceEx(string directory, out ulong availableBytes,
            out ulong totalBytes, out ulong freeBytes);

        [DllImport("user32.dll", ExactSpelling = true)]
        private static extern bool MessageBeep(uint type);

        [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetWindowDisplayAffinity(IntPtr window, uint affinity);

        public MainWindow()
        {
            InitializeComponent();
            _dispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
#if DEBUG
            DiagnosticsMessage.Visibility = Visibility.Visible;
#endif
            // The monitor source includes our own window. Excluding it avoids a
            // recursive preview that becomes unstable when the window is maximized.
            _ = SetWindowDisplayAffinity(WinRT.Interop.WindowNative.GetWindowHandle(this), 0x11);
            var workArea = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(
                AppWindow.Id, Microsoft.UI.Windowing.DisplayAreaFallback.Primary).WorkArea;
            var windowWidth = Math.Min(1260, Math.Max(1, workArea.Width - 32));
            var windowHeight = Math.Min(1120, Math.Max(1, workArea.Height - 32));
            AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(
                workArea.X + (workArea.Width - windowWidth) / 2,
                workArea.Y + (workArea.Height - windowHeight) / 2,
                windowWidth, windowHeight));
            _settings = ClipprSettings.Load();
            HotkeyLabel.Text = $"Clip {_settings.Hotkey.Replace("+", " + ", StringComparison.Ordinal)} · Screenshot {_settings.ScreenshotHotkey.Replace("+", " + ", StringComparison.Ordinal)}";
            _replaySeconds = (uint)_settings.ClipLengthSeconds;
            _replayRamMb = (ulong)_settings.ReplayMemoryLimitMb;
            _clipFolder = _settings.ClipFolder;
            _clipFormat = _settings.VideoFormat;
            _audioOutputId = _settings.AudioOutputId;
            _audioInputId = _settings.AudioInputId;
            _gameAudioPid = _settings.GameAudioProcessId;
            _chatAudioPid = _settings.DiscordAudioProcessId;
            _musicAudioPid = _settings.MusicAudioProcessId;
            _preferredDisplayName = _settings.PreferredDisplay;
            _recordingFps = _settings.FrameRate;
            _recordingBitrate = _settings.QualityPreset == "High" ? 16_000_000u : 8_000_000u;
            _recordingFullHd = _settings.Resolution == "1920x1080";
            _startCaptureAutomatically = _settings.StartCaptureAutomatically;
            _capturePaused = !_startCaptureAutomatically;
            _selectedSource = _settings.CaptureMode == "Application" ? CaptureSource.Application : CaptureSource.Screen;
            try
            {
                _tray = new SystemTrayController(WinRT.Interop.WindowNative.GetWindowHandle(this),
                    Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread()!,
                    Path.Combine(AppContext.BaseDirectory, "Assets", "ClipprTray.ico"),
                    () => !_exitRequested && _settings.MinimizeToTrayOnClose,
                    () => _capturePaused,
                    () => _capture != IntPtr.Zero && _clippingEnabled,
                    () => _capture != IntPtr.Zero && !_capturePaused && _activeSource == _selectedSource,
                    OnTrayCommand, OnSystemPowerChanged, OnDisplayConfigurationChanged);
            }
            catch (Exception ex)
            {
                ReplayMessage.Text = $"System tray unavailable: {ex.Message}";
            }
            PopulateAudioControls();
            UpdateClippingButton(false);
            ExtendsContentIntoTitleBar = true;
            SetTitleBar(TitleBar);
            AppWindow.Changed += (_, _) => UpdateSettingsPosition();
            SettingsButton.Loaded += (_, _) => UpdateSettingsPosition();
            PreviewPanel.Loaded += OnPreviewLoaded;
            DisplayFlyout.Opening += (_, _) =>
            {
                if (_selectedSource == CaptureSource.Screen) RefreshDisplays();
                else _ = RefreshWindowsAsync(resumeSelectedWindow: false);
            };
            MusicAudioPicker.DropDownOpened += (_, _) => AddDetectedMusicApplications();
            _captureTimer.Tick += OnCaptureTick;
            _reliabilityTimer.Tick += OnReliabilityTick;
            _reliabilityTimer.Start();
            _audioMeterTimer.Tick += OnAudioMeterTick;
            foreach (var meter in new[] { DesktopMeter, MicrophoneMeter, GameMeter, ChatMeter, MusicMeter })
                ConfigureAudioMeter(meter);
            _audioMeterTimer.Start();
            SettingsHost.KeyDown += OnSettingsHostKeyDown;
            _audioSaveTimer.Tick += (_, _) =>
            {
                _audioSaveTimer.Stop();
                try { _settings.SaveAtomicJson(); }
                catch (Exception ex) { ReplayMessage.Text = $"Could not save audio levels: {ex.Message}"; }
            };
            Closed += OnWindowClosed;
            if (_settings.VoiceCommandsEnabled) _ = ConfigureVoiceCommandsAsync(_settings);
            UpdateSettingsPosition();
        }

        internal string CurrentVoiceStatus => _voiceStatus;

        private async Task ConfigureVoiceCommandsAsync(ClipprSettings settings)
        {
            var old = _voiceCommandService;
            _voiceCommandService = null;
            if (old is not null) await old.DisposeAsync();
            if (_closing || !settings.VoiceCommandsEnabled)
            {
                SetVoiceStatus("Voice commands are off.");
                return;
            }

            SetVoiceStatus("Checking Windows offline speech support…");
            try
            {
                _voiceCommandService = await VoiceCommandService.StartAsync(
                    settings.VoiceWakePhrase, settings.VoiceConfidenceThreshold,
                    settings.VoiceCooldownSeconds, OnVoiceCommandRecognized, SetVoiceStatus);
            }
            catch (VoiceRecognitionException ex)
            {
                SetVoiceStatus(ex.Message);
                LocalReliabilityLog.Error("voice-recognition", ex.HResult, ex.Message);
            }
        }

        private void SetVoiceStatus(string status)
        {
            _voiceStatus = status;
            void Update()
            {
                VoiceStatusText.Text = status;
                VoiceStatusText.Visibility = _settings.VoiceCommandsEnabled ? Visibility.Visible : Visibility.Collapsed;
                _settingsView?.RefreshVoiceStatus(status);
            }
            if (_dispatcherQueue?.HasThreadAccess == true) Update();
            else _ = _dispatcherQueue?.TryEnqueue(Update);
        }

        private void OnVoiceCommandRecognized(string action)
        {
            SetVoiceStatus(action switch
            {
                "save-configured" => "Saving the configured replay duration",
                "save-30" => "Saving the last 30 seconds",
                "save-60" => "Saving the last 60 seconds",
                "screenshot" => "Taking a screenshot",
                "start" => "Starting clipping",
                "stop" => "Stopping clipping",
                _ => "Voice command received"
            });
            _ = _dispatcherQueue?.TryEnqueue(() => _ = ExecuteVoiceCommandAsync(action));
        }

        private static uint GetReplayRetentionSeconds(ClipprSettings settings) =>
            (uint)Math.Max(settings.ClipLengthSeconds, settings.VoiceCommandsEnabled ? 60 : settings.ClipLengthSeconds);

        private async Task ExecuteVoiceCommandAsync(string action)
        {
            if (_closing) return;
            switch (action)
            {
                case "save-configured": await SaveReplayAsync(); break;
                case "save-30": await SaveReplayAsync(30); break;
                case "save-60": await SaveReplayAsync(60); break;
                case "screenshot": await TakeScreenshotAsync(); break;
                case "start": await SetClippingAsync(true); break;
                case "stop": await SetClippingAsync(false); break;
            }
        }

        private void OnSettingsClick(object sender, RoutedEventArgs e)
        {
            if (_settingsView is not null) return;
            _settingsView = new SettingsView(this, _settings);
            SettingsHost.Children.Add(_settingsView);
            CaptureToolbar.Visibility = Visibility.Collapsed;
            CaptureContent.Visibility = Visibility.Collapsed;
            SettingsHost.Visibility = Visibility.Visible;
            BackToCaptureButton.Visibility = Visibility.Visible;
            SettingsButton.Visibility = Visibility.Collapsed;
            WindowTitleText.Text = "Settings";
            WindowTitleText.Margin = new Thickness(8, 0, 0, 0);
        }

        internal void RestoreFromTray()
        {
            _tray?.Restore();
            Activate();
        }

        private void OnTrayCommand(TrayCommand command)
        {
            switch (command)
            {
                case TrayCommand.Open:
                    RestoreFromTray();
                    break;
                case TrayCommand.SaveClip:
                    OnSaveReplayClick(SaveReplayButton, new RoutedEventArgs());
                    break;
                case TrayCommand.TakeScreenshot:
                    OnScreenshotClick(ScreenshotButton, new RoutedEventArgs());
                    break;
                case TrayCommand.PauseResumeCapture:
                    _ = ToggleCapturePauseAsync();
                    break;
                case TrayCommand.OpenClipsFolder:
                    OnOpenClipsClick(OpenClipsButton, new RoutedEventArgs());
                    break;
                case TrayCommand.Settings:
                    RestoreFromTray();
                    OnSettingsClick(SettingsButton, new RoutedEventArgs());
                    break;
                case TrayCommand.Exit:
                    _exitRequested = true;
                    _tray?.Dispose();
                    _tray = null;
                    Close();
                    break;
            }
        }

        private async Task ToggleCapturePauseAsync()
        {
            if (_capture == IntPtr.Zero || _closing) return;
            if (!_capturePaused)
            {
                _capturePaused = true;
                await StopCaptureAsync();
                ShowPreviewMessage("Capture paused · choose Resume Capture from the tray to continue");
                ReplayMessage.Text = "Capture paused";
                return;
            }

            _capturePaused = false;
            if (_selectedSource == CaptureSource.Screen)
            {
                if (_selectedMonitor == IntPtr.Zero) RefreshDisplays();
                if (_selectedMonitor != IntPtr.Zero) await BeginScreenCaptureAsync(_selectedMonitor);
            }
            else if (_selectedWindow != IntPtr.Zero)
            {
                await BeginWindowCaptureAsync(_selectedWindow);
            }
            else
            {
                await RefreshWindowsAsync(resumeSelectedWindow: true);
            }

            if (_activeSource is null)
            {
                _capturePaused = true;
                ShowPreviewMessage("Capture could not resume · select an available source");
                ReplayMessage.Text = "Capture remains paused";
            }
            else
            {
                ReplayMessage.Text = "Capture resumed";
            }
        }

        private void OnBackToCaptureClick(object sender, RoutedEventArgs e)
        {
            SettingsHost.Visibility = Visibility.Collapsed;
            SettingsHost.Children.Clear();
            _settingsView = null;
            CaptureToolbar.Visibility = Visibility.Visible;
            CaptureContent.Visibility = Visibility.Visible;
            BackToCaptureButton.Visibility = Visibility.Collapsed;
            SettingsButton.Visibility = Visibility.Visible;
            WindowTitleText.Text = "ClipDat";
            WindowTitleText.Margin = new Thickness(16, 0, 0, 0);
        }

        private void OnSettingsHostKeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key != VirtualKey.Escape || _settingsView is null) return;
            OnBackToCaptureClick(sender, new RoutedEventArgs());
            e.Handled = true;
        }

        internal async Task ApplySettingsAsync(ClipprSettings settings)
        {
            if (settings.Validate() is string validationError) throw new InvalidOperationException(validationError);
            if (settings.GameAudioProcessId != 0 && settings.GameAudioProcessId == settings.DiscordAudioProcessId)
                throw new InvalidOperationException("Game audio and Discord audio must use different applications.");

            var oldSource = _selectedSource;
            var oldDisplayName = _preferredDisplayName;
            var oldApplicationProcessId = _settings.PreferredApplicationProcessId;
            var replayChanged = _replaySeconds != (uint)settings.ClipLengthSeconds ||
                                _replayRamMb != (ulong)settings.ReplayMemoryLimitMb ||
                                _settings.VoiceCommandsEnabled != settings.VoiceCommandsEnabled ||
                                _settings.MinimumFreeDiskSpaceGb != settings.MinimumFreeDiskSpaceGb;
            var formatChanged = _clipFormat != settings.VideoFormat || _clipFolder != settings.ClipFolder;
            var wasScreenCapturing = _activeSource == CaptureSource.Screen;
            await ApplyStartupPreferenceAsync(settings.StartWithWindows);
            _settings = settings;
            HotkeyLabel.Text = $"Clip {settings.Hotkey.Replace("+", " + ", StringComparison.Ordinal)} · Screenshot {settings.ScreenshotHotkey.Replace("+", " + ", StringComparison.Ordinal)}";
            _clipFormat = settings.VideoFormat;
            _clipFolder = settings.ClipFolder;
            _audioOutputId = settings.AudioOutputId;
            _audioInputId = settings.AudioInputId;
            _gameAudioPid = settings.GameAudioProcessId;
            _chatAudioPid = settings.DiscordAudioProcessId;
            _musicAudioPid = settings.MusicAudioProcessId;
            PopulateAudioControls();
            _recordingFps = settings.FrameRate;
            _recordingBitrate = settings.QualityPreset == "High" ? 16_000_000u : 8_000_000u;
            _recordingFullHd = settings.Resolution == "1920x1080";
            _startCaptureAutomatically = settings.StartCaptureAutomatically;

            await ConfigureVoiceCommandsAsync(settings);

            if (_recording)
                EncodingMessage.Text = "Recording settings saved · stop and start recording to apply them";
            else ShowRecordingOptions();

            if (_capture == IntPtr.Zero || _closing) return;
            var capture = _capture;
            var result = await Task.Run(() => NativeCapture.ClipprSetAudioDevices(capture, _audioOutputId, _audioInputId));
            if (result >= 0) result = await Task.Run(() => NativeCapture.ClipprConfigureProcessAudio(capture, _gameAudioPid, _chatAudioPid));
            if (result >= 0) result = await Task.Run(() => NativeCapture.ClipprConfigureMusicAudio(capture, _musicAudioPid));
            if (result >= 0) result = await Task.Run(() => NativeCapture.ClipprConfigureAudio(capture,
                settings.DesktopAudioEnabled ? 1 : 0, settings.MicrophoneAudioEnabled ? 1 : 0));
            if (result < 0) Marshal.ThrowExceptionForHR(result);
            ApplyAudioLevels();

            ConfigureNativeHotkey();
            if (replayChanged)
            {
                var replayResult = await Task.Run(() => NativeCapture.ClipprConfigureReplay(
                    capture, GetReplayRetentionSeconds(settings), (ulong)settings.ReplayMemoryLimitMb * 1024 * 1024,
                    8UL * 1024 * 1024 * 1024, (ulong)settings.MinimumFreeDiskSpaceGb * 1024 * 1024 * 1024));
                if (replayResult < 0) Marshal.ThrowExceptionForHR(replayResult);
                _replaySeconds = (uint)settings.ClipLengthSeconds;
                _replayRamMb = (ulong)settings.ReplayMemoryLimitMb;
                ReplayMessage.Text = $"Replay reset · {_replaySeconds}s · {_replayRamMb} MB RAM";
            }
            if (formatChanged)
            {
                var outputResult = await Task.Run(() => NativeCapture.ClipprConfigureReplayOutput(capture, _clipFolder, _clipFormat));
                if (outputResult < 0) Marshal.ThrowExceptionForHR(outputResult);
            }

            var requestedSource = settings.CaptureMode == "Application" ? CaptureSource.Application : CaptureSource.Screen;
            if (requestedSource == CaptureSource.Application && oldApplicationProcessId != settings.PreferredApplicationProcessId)
                _selectedWindow = IntPtr.Zero;
            if (requestedSource != oldSource)
                await ApplyCaptureModeAsync(requestedSource);
            else if (requestedSource == CaptureSource.Application && oldApplicationProcessId != settings.PreferredApplicationProcessId)
                await RefreshWindowsAsync(resumeSelectedWindow: true);
            else if (requestedSource == CaptureSource.Screen && oldDisplayName != settings.PreferredDisplay)
            {
                RefreshDisplays();
                if ((wasScreenCapturing || settings.StartCaptureAutomatically) && _selectedMonitor != IntPtr.Zero)
                    await BeginScreenCaptureAsync(_selectedMonitor);
            }
            _preferredDisplayName = settings.PreferredDisplay;
        }

        private static async Task ApplyStartupPreferenceAsync(bool enabled)
        {
            var startupTask = await Windows.ApplicationModel.StartupTask.GetAsync("ClipprStartupTask");
            if (enabled)
            {
                if (startupTask.State == Windows.ApplicationModel.StartupTaskState.Disabled)
                {
                    var state = await startupTask.RequestEnableAsync();
                    if (state != Windows.ApplicationModel.StartupTaskState.Enabled)
                        throw new InvalidOperationException("Windows did not enable startup for ClipDat. Check Settings > Apps > Startup.");
                }
                else if (startupTask.State != Windows.ApplicationModel.StartupTaskState.Enabled)
                    throw new InvalidOperationException("Windows has blocked startup for ClipDat. Check Settings > Apps > Startup.");
            }
            else if (startupTask.State == Windows.ApplicationModel.StartupTaskState.Enabled)
                startupTask.Disable();
        }

        private void ConfigureNativeHotkey()
        {
            if (_capture == IntPtr.Zero) return;
            var (modifiers, key) = _settings.Hotkey switch
            {
                "Ctrl+Alt+F10" => (2u | 1u, 0x79u),
                "Ctrl+Shift+F9" => (2u | 4u, 0x78u),
                _ => (2u | 4u, 0x79u)
            };
            var result = NativeCapture.ClipprConfigureHotkey(_capture, modifiers, key);
            if (result < 0) ReplayMessage.Text = $"Could not update clipping hotkey · 0x{result:X8}";
            var (screenshotModifiers, screenshotKey) = _settings.ScreenshotHotkey switch
            {
                "Ctrl+Alt+F9" => (2u | 1u, 0x78u),
                "Ctrl+Shift+F8" => (2u | 4u, 0x77u),
                _ => (2u | 1u, 0x79u)
            };
            var screenshotResult = NativeCapture.ClipprConfigureScreenshot(_capture,
                Path.Combine(_clipFolder, "Screenshots"), _settings.ScreenshotFormat == "jpg" ? 1 : 0,
                (uint)_settings.ScreenshotJpegQuality, screenshotModifiers, screenshotKey);
            if (screenshotResult < 0) ReplayMessage.Text = $"Could not update screenshot hotkey · 0x{screenshotResult:X8}";
            _screenshotsCompletedSeen = NativeCapture.ClipprGetScreenshotCount(_capture);
        }

        internal NativeCapture.Display[] EnumerateSettingsDisplays()
        {
            Marshal.ThrowExceptionForHR(NativeCapture.ClipprEnumerateDisplays(Array.Empty<NativeCapture.Display>(), 0, out var capacity));
            var found = new NativeCapture.Display[capacity];
            Marshal.ThrowExceptionForHR(NativeCapture.ClipprEnumerateDisplays(found, found.Length, out var count));
            return found.Take(Math.Min(found.Length, count)).ToArray();
        }

        internal NativeCapture.AudioDevice[] EnumerateSettingsAudioDevices(bool renderDevices) => EnumerateAudioDevices(renderDevices);
        internal NativeCapture.Window[] EnumerateSettingsApplications() => EnumerateAudioApplications();

        internal string BuildDiagnosticInformation(string version)
        {
            var capture = _capture == IntPtr.Zero ? default : NativeCapture.ClipprGetCaptureStatus(_capture);
            var replay = _capture == IntPtr.Zero ? default : NativeCapture.ClipprGetReplayStatus(_capture);
            var audio = _capture == IntPtr.Zero ? default : NativeCapture.ClipprGetAudioStatus(_capture);
            return $"ClipDat {version}\nWindows {Environment.OSVersion.Version}\n" +
                   $"Capture state: {capture.State}; error: 0x{capture.ErrorCode:X8}\n" +
                   $"Replay state: {replay.State}; buffered packets: {replay.Packets}; error: 0x{replay.ErrorCode:X8}\n" +
                   $"Audio enabled: desktop={_settings.DesktopAudioEnabled}, microphone={_settings.MicrophoneAudioEnabled}, " +
                   $"game={(_settings.GameAudioProcessId != 0)}, Discord={(_settings.DiscordAudioProcessId != 0)}, " +
                   $"music={(_settings.MusicAudioProcessId != 0)}\n" +
                   $"Audio status: desktop=0x{audio.DesktopError:X8}, microphone=0x{audio.MicrophoneError:X8}, " +
                   $"game=0x{audio.GameError:X8}, Discord=0x{audio.ChatError:X8}, music=0x{audio.MusicError:X8}";
        }

        private void OnPreviewLoaded(object sender, RoutedEventArgs e)
        {
            if (_capture != IntPtr.Zero) return;
            var stage = "WinUI panel interop";
            try
            {
                var scale = PreviewPanel.XamlRoot?.RasterizationScale ?? 1;
                var width = Math.Max(1, (int)Math.Round(PreviewPanel.ActualWidth * scale));
                var height = Math.Max(1, (int)Math.Round(PreviewPanel.ActualHeight * scale));
                var panel = ((WinRT.IWinRTObject)PreviewPanel).NativeObject.ThisPtr;
                stage = "native graphics initialization";
                Marshal.ThrowExceptionForHR(NativeCapture.ClipprCreateCapture(panel, width, height, out _capture));
                Marshal.ThrowExceptionForHR(NativeCapture.ClipprConfigureReplay(
                    _capture, GetReplayRetentionSeconds(_settings), _replayRamMb * 1024 * 1024, 8UL * 1024 * 1024 * 1024,
                    (ulong)_settings.MinimumFreeDiskSpaceGb * 1024 * 1024 * 1024));
                if (!_clippingEnabled)
                    Marshal.ThrowExceptionForHR(NativeCapture.ClipprSetReplayEnabled(_capture, 0));
                Marshal.ThrowExceptionForHR(NativeCapture.ClipprConfigureReplayOutput(_capture, _clipFolder, _clipFormat));
                Marshal.ThrowExceptionForHR(NativeCapture.ClipprSetAudioDevices(_capture, _audioOutputId, _audioInputId));
                Marshal.ThrowExceptionForHR(NativeCapture.ClipprConfigureProcessAudio(_capture, _gameAudioPid, _chatAudioPid));
                Marshal.ThrowExceptionForHR(NativeCapture.ClipprConfigureMusicAudio(_capture, _musicAudioPid));
                Marshal.ThrowExceptionForHR(NativeCapture.ClipprConfigureAudio(_capture,
                    _settings.DesktopAudioEnabled ? 1 : 0, _settings.MicrophoneAudioEnabled ? 1 : 0));
                ApplyAudioLevels();
                ConfigureNativeHotkey();
                if (!_startCaptureAutomatically)
                {
                    ShowPreviewMessage("Capture paused · choose a source to start");
                }
                else if (_selectedSource == CaptureSource.Screen)
                {
                    RefreshDisplays();
                    if (_selectedMonitor != IntPtr.Zero) _ = BeginScreenCaptureAsync(_selectedMonitor);
                    else ShowPreviewMessage("No displays are available");
                }
                else _ = RefreshWindowsAsync(resumeSelectedWindow: true);
                if (_selectedSource == CaptureSource.Application)
                    AnimatePill(SourceSelectionIndicator, applicationSelected: true);
                _captureTimer.Start();
            }
            catch (Exception ex)
            {
                LocalReliabilityLog.Error("capture-initialize", ex.HResult);
                ShowPreviewMessage($"Screen capture unavailable ({stage}, 0x{ex.HResult:X8}): {ex.Message}");
            }
        }

        private void RefreshDisplays()
        {
            if (_selectedSource != CaptureSource.Screen) return;
            try
            {
                Marshal.ThrowExceptionForHR(NativeCapture.ClipprEnumerateDisplays(
                    Array.Empty<NativeCapture.Display>(), 0, out var capacity));
                var found = new NativeCapture.Display[capacity];
                Marshal.ThrowExceptionForHR(NativeCapture.ClipprEnumerateDisplays(found, found.Length, out var count));
                _displays = found.Take(Math.Min(count, found.Length)).ToArray();
                if (!string.IsNullOrWhiteSpace(_settings.PreferredDisplay))
                    _selectedMonitor = _displays.FirstOrDefault(d => d.DeviceName == _settings.PreferredDisplay).Monitor;
                if (_selectedMonitor == IntPtr.Zero || !_displays.Any(d => d.Monitor == _selectedMonitor))
                    _selectedMonitor = _displays.FirstOrDefault().Monitor;
                var currentDisplay = _displays.FirstOrDefault(d => d.Monitor == _selectedMonitor);
                if (!string.IsNullOrWhiteSpace(currentDisplay.DeviceName))
                {
                    _settings.PreferredDisplay = currentDisplay.DeviceName;
                    _preferredDisplayName = currentDisplay.DeviceName;
                }

                DisplayFlyout.Items.Clear();
                for (var i = 0; i < _displays.Length; i++)
                {
                    var display = _displays[i];
                    var name = $"{display.FriendlyName} ({display.Width} × {display.Height})";
                    var item = new MenuFlyoutItem { Text = name, MinWidth = 210 };
                    item.Click += (_, _) => SelectDisplay(display.Monitor);
                    DisplayFlyout.Items.Add(item);
                }
                DisplayLabel.Text = _displays.FirstOrDefault(d => d.Monitor == _selectedMonitor).FriendlyName
                                    ?? "No displays";
            }
            catch (Exception ex)
            {
                ShowPreviewMessage($"Cannot enumerate displays: {ex.Message}");
            }
        }

        private void SelectDisplay(IntPtr monitor)
        {
            _selectedMonitor = monitor;
            var display = _displays.FirstOrDefault(d => d.Monitor == monitor);
            DisplayLabel.Text = display.FriendlyName ?? "Display";
            if (!string.IsNullOrWhiteSpace(display.DeviceName))
            {
                _settings.PreferredDisplay = display.DeviceName;
                _preferredDisplayName = display.DeviceName;
            }
            if (_selectedSource == CaptureSource.Screen) _ = BeginScreenCaptureAsync(monitor);
        }

        private async Task RefreshWindowsAsync(bool resumeSelectedWindow)
        {
            if (_selectedSource != CaptureSource.Application) return;
            var generation = ++_windowRefreshGeneration;
            DisplayFlyout.Items.Clear();
            DisplayFlyout.Items.Add(new MenuFlyoutItem { Text = "Finding applications…", IsEnabled = false });
            try
            {
                var result = await Task.Run(() =>
                {
                    Marshal.ThrowExceptionForHR(NativeCapture.ClipprEnumerateWindows(
                        Array.Empty<NativeCapture.Window>(), 0, out var capacity));
                    var found = new NativeCapture.Window[capacity];
                    Marshal.ThrowExceptionForHR(NativeCapture.ClipprEnumerateWindows(found, found.Length, out var count));
                    var windows = found.Take(Math.Min(count, found.Length)).ToArray();
                    var icons = new byte[]?[windows.Length];
                    for (var i = 0; i < windows.Length; i++)
                    {
                        var pixels = new byte[32 * 32 * 4];
                        if (NativeCapture.ClipprCopyWindowIcon(windows[i].Handle, pixels, pixels.Length) == 0)
                            icons[i] = pixels;
                    }
                    return (windows, icons);
                });
                if (_closing || _selectedSource != CaptureSource.Application || generation != _windowRefreshGeneration) return;
                _windows = result.windows;
                if (_selectedWindow == IntPtr.Zero && _settings.PreferredApplicationProcessId != 0)
                    _selectedWindow = _windows.FirstOrDefault(w => w.ProcessId == _settings.PreferredApplicationProcessId).Handle;
                if (!_windows.Any(w => w.Handle == _selectedWindow)) _selectedWindow = IntPtr.Zero;
                DisplayFlyout.Items.Clear();
                for (var i = 0; i < _windows.Length; i++)
                {
                    var window = _windows[i];
                    var title = window.Title.Length > 48 ? window.Title[..48] + "…" : window.Title;
                    var item = new MenuFlyoutItem
                    {
                        Text = $"{window.ApplicationName} — {title}{(window.Minimized != 0 ? " (minimized)" : "")}",
                        MinWidth = 270
                    };
                    if (result.icons[i] is byte[] pixels)
                    {
                        var bitmap = new WriteableBitmap(32, 32);
                        using (var stream = bitmap.PixelBuffer.AsStream()) stream.Write(pixels, 0, pixels.Length);
                        bitmap.Invalidate();
                        item.Icon = new ImageIcon { Source = bitmap };
                    }
                    item.Click += (_, _) => SelectWindow(window);
                    DisplayFlyout.Items.Add(item);
                }
                if (_windows.Length == 0)
                    DisplayFlyout.Items.Add(new MenuFlyoutItem { Text = "No available application windows", IsEnabled = false });
                DisplayLabel.Text = _windows.FirstOrDefault(w => w.Handle == _selectedWindow).ApplicationName
                                    ?? "Select application";
                if (resumeSelectedWindow)
                {
                    if (_selectedWindow != IntPtr.Zero) _ = BeginWindowCaptureAsync(_selectedWindow);
                    else ShowPreviewMessage("Choose an application window from the menu");
                }
            }
            catch (Exception ex)
            {
                if (generation == _windowRefreshGeneration)
                    ShowPreviewMessage($"Cannot list application windows: {ex.Message}");
            }
        }

        private void SelectWindow(NativeCapture.Window window)
        {
            _selectedWindow = window.Handle;
            _settings.PreferredApplicationProcessId = window.ProcessId;
            DisplayLabel.Text = window.ApplicationName;
            if (window.Minimized != 0)
            {
                _activeSource = null;
                _ = StopCaptureAsync();
                ShowPreviewMessage($"Restore {window.ApplicationName} before capturing it");
            }
            else _ = BeginWindowCaptureAsync(window.Handle);
        }

        private async Task BeginScreenCaptureAsync(IntPtr monitor)
        {
            await _captureGate.WaitAsync();
            try
            {
                if (_closing || _capture == IntPtr.Zero || monitor == IntPtr.Zero ||
                    _selectedSource != CaptureSource.Screen || monitor != _selectedMonitor) return;
                _activeSource = null;
                ShowPreviewMessage("Waiting for desktop frames");
                var capture = _capture;
                var result = await Task.Run(() => NativeCapture.ClipprSwitchDisplay(capture, monitor));
                if (result < 0)
                {
                    LocalReliabilityLog.Error("display-capture", result);
                    ShowPreviewMessage($"Capture failed: 0x{result:X8}");
                }
                else if (_selectedSource == CaptureSource.Screen && _selectedMonitor == monitor)
                {
                    _activeSource = CaptureSource.Screen;
                    _activeTarget = monitor;
                    _capturePaused = false;
                }
            }
            catch (Exception ex)
            {
                ShowPreviewMessage($"Capture failed: {ex.Message}");
            }
            finally
            {
                _captureGate.Release();
            }
        }

        private async Task BeginWindowCaptureAsync(IntPtr window)
        {
            await _captureGate.WaitAsync();
            try
            {
                if (_closing || _capture == IntPtr.Zero || window == IntPtr.Zero ||
                    _selectedSource != CaptureSource.Application || window != _selectedWindow) return;
                _activeSource = null;
                ShowPreviewMessage("Waiting for application frames");
                var capture = _capture;
                var result = await Task.Run(() => NativeCapture.ClipprSwitchWindow(capture, window));
                if (result < 0)
                {
                    LocalReliabilityLog.Error("application-capture", result);
                    ShowPreviewMessage(result switch
                    {
                        unchecked((int)0x80070005) => "Windows denied capture access to this application",
                        unchecked((int)0x80070015) => "This application is minimized. Restore it to capture",
                        unchecked((int)0x80070006) or unchecked((int)0x80070578) =>
                            "This application window is no longer available",
                        unchecked((int)0x80004002) => "Application capture is unavailable on this Windows version",
                        _ => $"Cannot capture this application (0x{result:X8})"
                    });
                }
                else if (_selectedSource == CaptureSource.Application && _selectedWindow == window)
                {
                    _activeSource = CaptureSource.Application;
                    _activeTarget = window;
                    _capturePaused = false;
                }
            }
            catch (Exception ex)
            {
                ShowPreviewMessage($"Cannot capture this application: {ex.Message}");
            }
            finally
            {
                _captureGate.Release();
            }
        }

        private async Task StopCaptureAsync()
        {
            await _captureGate.WaitAsync();
            try
            {
                _activeSource = null;
                _activeTarget = IntPtr.Zero;
                if (_capture != IntPtr.Zero)
                {
                    var capture = _capture;
                    await Task.Run(() => NativeCapture.ClipprStopCapture(capture));
                }
            }
            finally
            {
                _captureGate.Release();
            }
        }

        private void OnCaptureTick(object? sender, object e)
        {
            if (_capture != IntPtr.Zero)
            {
                UpdateReplayStatus();
                UpdateScreenshotStatus();
                UpdateStorageHealth();
                CheckAudioCaptureHealth();
#if DEBUG
                UpdateDiagnostics();
#endif
                UpdateClippingButton(_clippingEnabled &&
                    (NativeCapture.ClipprGetReplayStatus(_capture).State is 1 or 2));
            }
            if (_capture == IntPtr.Zero || _closing || _activeSource != _selectedSource ||
                _activeTarget != (_selectedSource == CaptureSource.Screen ? _selectedMonitor : _selectedWindow)) return;
            var status = NativeCapture.ClipprGetCaptureStatus(_capture);
            if (status.State == 1 && status.FramesPresented > 0)
            {
                PreviewOverlay.Visibility = Visibility.Collapsed;
            }
            else if (status.State == 2 && !_reconnecting)
            {
                if (_lastCaptureError != unchecked((int)0x8007048F))
                {
                    _lastCaptureError = unchecked((int)0x8007048F);
                    LocalReliabilityLog.Error("capture-target", _lastCaptureError, "Selected target disconnected");
                }
                if (_selectedSource == CaptureSource.Screen)
                {
                    ShowPreviewMessage("Display disconnected. Reconnecting to an available display…");
                    _ = ReconnectDisplayAsync();
                }
                else
                {
                    _activeSource = null;
                    _selectedWindow = IntPtr.Zero;
                    DisplayLabel.Text = "Select application";
                    ShowPreviewMessage("Application window closed or became unavailable. Choose another window");
                    _ = StopCaptureAsync();
                    _ = RefreshWindowsAsync(resumeSelectedWindow: false);
                }
            }
            else if (status.State == 5)
            {
                ShowPreviewMessage("Application window is minimized. Restore it to resume the preview");
            }
            else if (status.State == 3 && !_recovering)
            {
                ShowPreviewMessage($"Capture error: 0x{status.ErrorCode:X8}");
                if (_lastCaptureError != status.ErrorCode)
                {
                    _lastCaptureError = status.ErrorCode;
                    LocalReliabilityLog.Error("capture", status.ErrorCode);
                }
                if (status.ErrorCode == unchecked((int)0x887A0005) ||
                    status.ErrorCode == unchecked((int)0x887A0007) ||
                    status.ErrorCode == unchecked((int)0x8899000C))
                    _ = RecoverDeviceAsync();
            }
            else if (status.State == 1) _lastCaptureError = 0;
        }

        private void UpdateScreenshotStatus()
        {
            if (_capture == IntPtr.Zero || _closing) return;
            var count = NativeCapture.ClipprGetScreenshotCount(_capture);
            if (count > _screenshotsCompletedSeen)
            {
                _screenshotsCompletedSeen = count;
                var buffer = new System.Text.StringBuilder(32768);
                if (NativeCapture.ClipprGetLastScreenshotPath(_capture, buffer, buffer.Capacity) >= 0)
                {
                    _lastScreenshotPath = buffer.ToString();
                    OnScreenshotSaved(_lastScreenshotPath);
                    _ = CopyScreenshotIfEnabledAsync(_lastScreenshotPath);
                }
            }
            var error = NativeCapture.ClipprGetScreenshotError(_capture);
            if (error < 0 && error != _screenshotErrorSeen)
            {
                _screenshotErrorSeen = error;
                LocalReliabilityLog.Error("screenshot", error);
                ShowInfoError(FormatScreenshotError(error));
            }
            else if (error >= 0) _screenshotErrorSeen = 0;
        }

        private async void OnScreenshotClick(object sender, RoutedEventArgs e) => await TakeScreenshotAsync();

        private async Task TakeScreenshotAsync()
        {
            ScreenshotButton.IsEnabled = false;
            await _captureGate.WaitAsync();
            try
            {
                if (_capture == IntPtr.Zero || _closing || _activeSource != _selectedSource ||
                    _activeTarget != (_selectedSource == CaptureSource.Screen ? _selectedMonitor : _selectedWindow))
                {
                    ShowInfoError("Start screen or application capture before taking a screenshot.");
                    return;
                }
                var folder = Path.Combine(_clipFolder, "Screenshots");
                Directory.CreateDirectory(folder);
                if (!HasOutputSpace())
                {
                    ShowInfoError($"Screenshot not saved. Keep at least {_settings.MinimumFreeDiskSpaceGb} GB free on the screenshot drive.");
                    return;
                }
                var extension = _settings.ScreenshotFormat == "jpg" ? "jpg" : "png";
                var path = Path.Combine(folder, $"ClipDat-Screenshot-{DateTime.Now:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid():N}.{extension}");
                var capture = _capture;
                var result = await Task.Run(() => NativeCapture.ClipprSaveScreenshot(capture, path,
                    _settings.ScreenshotFormat == "jpg" ? 1 : 0, (uint)_settings.ScreenshotJpegQuality));
                if (result < 0) { ShowInfoError(FormatScreenshotError(result)); return; }
                _lastScreenshotPath = path;
                OnScreenshotSaved(path);
                await CopyScreenshotIfEnabledAsync(path);
            }
            catch (Exception ex) { LocalReliabilityLog.Error("screenshot", ex.HResult); ShowInfoError($"Could not save screenshot: {ex.Message}"); }
            finally { _captureGate.Release(); ScreenshotButton.IsEnabled = true; }
        }

        private void OnScreenshotSaved(string path)
        {
            try
            {
                var image = new FileInfo(path);
                if (!image.Exists || image.Length == 0) { ShowInfoError("Screenshot was not finalized successfully."); return; }
            }
            catch (Exception ex) { ShowInfoError($"Screenshot was not finalized successfully: {ex.Message}"); return; }
            SetLastSavedFile(path);
            ReplayMessage.Text = "Screenshot saved";
            ShowNotification(ClipNotificationKind.Saved, "Screenshot saved", "Your screenshot is ready in the Screenshots folder.");
            if (_settings.ScreenshotNotificationSound) _ = MessageBeep(0x40);
        }

        private async Task CopyScreenshotIfEnabledAsync(string path)
        {
            if (!_settings.CopyScreenshotToClipboard) return;
            try
            {
                var file = await StorageFile.GetFileFromPathAsync(path);
                var data = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
                data.SetBitmap(Windows.Storage.Streams.RandomAccessStreamReference.CreateFromFile(file));
                Clipboard.SetContent(data);
                Clipboard.Flush();
            }
            catch (Exception ex) { ShowInfoError($"Screenshot saved but clipboard copy failed: {ex.Message}"); }
        }

        private void SetLastSavedFile(string path)
        {
            _lastSavedFilePath = Path.GetFullPath(path);
            SavedFileActionsButton.Visibility = Visibility.Visible;
            ShareClipMenuItem.Visibility = string.Equals(_lastSavedFilePath, _lastReplayPath, StringComparison.OrdinalIgnoreCase)
                ? Visibility.Visible : Visibility.Collapsed;
        }

        private string GetVerifiedSavedFile() => WindowsShellFileActions.RequireExistingFile(_lastSavedFilePath);

        private void OnOpenSavedFileClick(object sender, RoutedEventArgs e)
        {
            try
            {
                var path = GetVerifiedSavedFile();
                WindowsShellFileActions.OpenWithDefaultApplication(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or COMException or System.ComponentModel.Win32Exception)
            { ShowInfoError($"Could not open saved file: {ex.Message}"); }
        }

        private void OnShowSavedFileClick(object sender, RoutedEventArgs e)
        {
            try { WindowsShellFileActions.ShowFileInFolder(GetVerifiedSavedFile()); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or COMException)
            { ShowInfoError($"Could not show saved file in Explorer: {ex.Message}"); }
        }

        private void OnCopySavedPathClick(object sender, RoutedEventArgs e)
        {
            try
            {
                var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
                package.SetText(GetVerifiedSavedFile());
                Clipboard.SetContent(package);
                Clipboard.Flush();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or COMException)
            { ShowInfoError($"Could not copy saved file path: {ex.Message}"); }
        }

        private void OnCopySavedFileClick(object sender, RoutedEventArgs e)
        {
            try { WindowsShellFileActions.CopyFileToClipboard(GetVerifiedSavedFile()); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or COMException)
            { ShowInfoError($"Could not copy saved file: {ex.Message}"); }
        }


        private async void OnShareClipClick(object sender, RoutedEventArgs e)
        {
            string clipPath;
            try
            {
                if (_lastReplayPath is null || !string.Equals(_lastReplayPath, _lastSavedFilePath, StringComparison.OrdinalIgnoreCase))
                    throw new FileNotFoundException("Save or select a clip before opening Share Clip.");
                clipPath = WindowsShellFileActions.RequireExistingFile(_lastReplayPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                ShowInfoError(ex.Message);
                return;
            }

            var expiration = new ComboBox { MinWidth = 220 };
            foreach (var option in new[] { "15 minutes", "1 hour", "6 hours", "24 hours", "Until ClipDat closes" })
                expiration.Items.Add(option);
            expiration.SelectedIndex = Math.Clamp(_shareExpirySelection, 0, 4);
            expiration.SelectionChanged += (_, _) => _shareExpirySelection = Math.Max(0, expiration.SelectedIndex);

            var status = new StackPanel { Spacing = 8 };
            status.Children.Add(new TextBlock
            {
                Text = "Direct sharing unavailable",
                FontSize = 16,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
            });
            status.Children.Add(new TextBlock
            {
                Text = "This build can serve clips only from 127.0.0.1 on this PC. It has no verified public HTTPS route, so other people cannot reach a link. No link was created and the sharing server remains stopped.",
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 440
            });
            status.Children.Add(new TextBlock
            {
                Text = $"Clip: {Path.GetFileName(clipPath)}\nSharing status: Not shared\nExternal reachability: Unavailable (loopback only)",
                TextWrapping = TextWrapping.Wrap
            });
            status.Children.Add(new TextBlock { Text = "Link expiration preference" });
            status.Children.Add(expiration);
            status.Children.Add(new TextBlock
            {
                Text = "This preference will apply when verified direct sharing is supported.",
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12
            });

            var dialog = new ContentDialog
            {
                Title = "Share Clip",
                Content = status,
                PrimaryButtonText = "Copy Link",
                IsPrimaryButtonEnabled = false,
                SecondaryButtonText = "Stop Sharing",
                IsSecondaryButtonEnabled = false,
                CloseButtonText = "Close",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = ((FrameworkElement)Content).XamlRoot
            };
            await dialog.ShowAsync();
        }

        private void OnOpenClipsClick(object sender, RoutedEventArgs e)
        {
            try
            {
                var folder = Path.GetFullPath(_clipFolder);
                Directory.CreateDirectory(folder);
                _ = File.GetAttributes(folder);
                WindowsShellFileActions.OpenWithDefaultApplication(folder);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or COMException or System.ComponentModel.Win32Exception)
            { ShowInfoError($"Could not open clips folder: {ex.Message}"); }
        }

        private static string FormatScreenshotError(int error) => error switch
        {
            unchecked((int)0x80070015) => "Start screen or application capture before taking a screenshot.",
            unchecked((int)0x80070070) => "Screenshot could not be saved because the disk is full.",
            unchecked((int)0x80070005) => "Screenshot folder access was denied.",
            unchecked((int)0x800705B4) => "Screenshot capture timed out waiting for a frame.",
            unchecked((int)0x800700AA) => "A screenshot is already being saved. Wait for it to finish and try again.",
            unchecked((int)0x800705AA) => "Not enough memory is available to save this screenshot.",
            _ => $"Screenshot failed · 0x{error:X8}"
        };

#if DEBUG
        private void UpdateDiagnostics()
        {
            if (_capture == IntPtr.Zero || _closing) return;
            var capture = NativeCapture.ClipprGetCaptureStatus(_capture);
            var encoding = NativeCapture.ClipprGetEncodingStatus(_capture);
            var replay = NativeCapture.ClipprGetReplayStatus(_capture);
            var now = Stopwatch.GetTimestamp();
            var captureFps = 0d;
            var encodedFps = 0d;
            if (_diagnosticSampleTimestamp != 0)
            {
                var elapsed = Stopwatch.GetElapsedTime(_diagnosticSampleTimestamp, now).TotalSeconds;
                if (elapsed > 0)
                {
                    captureFps = capture.FramesPresented >= _diagnosticCaptureFrames
                        ? (capture.FramesPresented - _diagnosticCaptureFrames) / elapsed : 0;
                    encodedFps = replay.FramesEncoded >= _diagnosticReplayFrames
                        ? (replay.FramesEncoded - _diagnosticReplayFrames) / elapsed : 0;
                }
            }
            _diagnosticSampleTimestamp = now;
            _diagnosticCaptureFrames = capture.FramesPresented;
            _diagnosticReplayFrames = replay.FramesEncoded;
            var drops = encoding.FramesDropped + replay.FramesDropped;
            var backend = encoding.ActiveEncoder != 0 ? "Media Foundation hardware H.264" : "Idle";
            var saveDuration = replay.SavesCompleted > 0 ? $"{replay.LastSaveDurationMs} ms" : "n/a";
            DiagnosticsMessage.Text =
                $"Capture {captureFps:0.0} fps · Encoded {encodedFps:0.0} fps · Dropped {drops} · " +
                $"Replay {replay.RamBytes / 1048576.0:0.0} MB RAM · " +
                $"Queue {encoding.AverageQueueLatencyUs / 1000.0:0.0}/{encoding.MaxQueueLatencyUs / 1000.0:0.0} ms avg/max · " +
                $"Encode {encoding.AverageWriteLatencyUs / 1000.0:0.0}/{encoding.MaxWriteLatencyUs / 1000.0:0.0} ms avg/max · " +
                $"{backend} · Save {saveDuration}";
        }
#endif

        private void UpdateEncodingStatus()
        {
            var status = NativeCapture.ClipprGetEncodingStatus(_capture);
            if (status.State == 1) EncodingMessage.Text = "Starting hardware encoder…";
            else if (status.State == 2)
                EncodingMessage.Text = $"Recording · {status.FramesEncoded} frames" +
                                       (status.FramesDropped > 0 ? $" · {status.FramesDropped} queue drops" : "");
            else if (status.State == 4) EncodingMessage.Text = "Finishing recording…";
            else if (status.State == 3)
            {
                _recording = false;
                EncodingMessage.Text = status.ErrorCode switch
                {
                    unchecked((int)0xC00D5212) => "No compatible hardware H.264 encoder",
                    unchecked((int)0xC00D36B4) => "This frame size or format cannot be encoded",
                    unchecked((int)0x887A0005) or unchecked((int)0x887A0007) =>
                        "Graphics device lost. Restart capture and try again",
                    _ => $"Encoder error: 0x{status.ErrorCode:X8}"
                };
            }
            else if (_recording)
            {
                _recording = false;
                EncodingMessage.Text = "Recording stopped";
            }
        }

        private void UpdateReplayStatus()
        {
            var status = NativeCapture.ClipprGetReplayStatus(_capture);
            var ready = _clippingEnabled && status.State == 2 && status.Packets > 0;
            if (ready && !_replayReadyNotified)
            {
                _replayReadyNotified = true;
                ShowNotification(ClipNotificationKind.Ready, "Ready to clip",
                    $"ClipDat is buffering the last {_replaySeconds} seconds. Press {_settings.Hotkey.Replace("+", " + ", StringComparison.Ordinal)} to save.");
            }
            else if (!ready) _replayReadyNotified = false;
            if (status.SavesFailed > _replayFailedSeen)
            {
                var failedCount = status.SavesFailed - _replayFailedSeen;
                _replayFailedSeen = status.SavesFailed;
                LocalReliabilityLog.Error("replay-export", status.LastSaveError);
                while (failedCount-- > 0 && _pendingReplayPaths.Count > 0) _pendingReplayPaths.Dequeue();
                ShowInfoError(FormatExportError(status.LastSaveError));
                ReplayMessage.Text = "Replay ready";
            }
            else if (status.SavesCompleted > _replayCompletedSeen)
            {
                var completedCount = status.SavesCompleted - _replayCompletedSeen;
                _replayCompletedSeen = status.SavesCompleted;
                ReplayMessage.Text = $"Replay ready · {_replaySeconds}s · {status.RamBytes / 1048576} MB RAM";
                var savedAny = false;
                while (completedCount-- > 0)
                {
                    var completedPath = _pendingReplayPaths.Count > 0 ? _pendingReplayPaths.Dequeue() : null;
                    try
                    {
                        if (!IsFinalizedSavedFile(completedPath))
                        {
                            var pathBuffer = new System.Text.StringBuilder(32768);
                            if (NativeCapture.ClipprGetLastReplayPath(_capture, pathBuffer, pathBuffer.Capacity) >= 0 &&
                                IsFinalizedSavedFile(pathBuffer.ToString()))
                                completedPath = pathBuffer.ToString();
                        }
                        if (IsFinalizedSavedFile(completedPath))
                        {
                            _lastReplayPath = completedPath;
                            SetLastSavedFile(completedPath);
                            savedAny = true;
                        }
                    }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
                if (savedAny)
                {
                    ShowClipSavedNotification(status.LastSaveShorter != 0);
                    if (_settings.SaveNotificationSound) _ = MessageBeep(0x40);
                }
                else ShowInfoError("Clip export completed, but the saved file could not be verified.");
            }
            else if (status.SavesActive > 0) ReplayMessage.Text = "Saving replay…";
            else if (status.State == 3) ReplayMessage.Text = $"Replay unavailable: 0x{status.ErrorCode:X8}";
            else if (status.State == 2 && status.Packets == 0) ReplayMessage.Text = "Waiting for replay keyframe…";
            else if (status.State == 2 && DateTime.Now >= _replayMessageUntil)
                ReplayMessage.Text = $"Replay ready · {_replaySeconds}s · {status.RamBytes / 1048576} MB RAM";
            else if (status.State == 1) ReplayMessage.Text = "Starting replay encoder…";
            else if (status.State == 0) ReplayMessage.Text = _clippingEnabled
                ? "Replay paused · select a source" : "Clipping stopped · preview remains live";

            UpdateInfoBanner();
        }

        private void ShowInfoError(string message)
        {
            _infoError = message;
            _infoErrorUntil = DateTime.Now.AddSeconds(8);
            UpdateInfoBanner();
        }

        private static bool IsFinalizedSavedFile([NotNullWhen(true)] string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            try { var file = new FileInfo(path); return file.Exists && file.Length > 0; }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
            catch (ArgumentException) { return false; }
        }

        private void UpdateInfoBanner()
        {
            if (_capture == IntPtr.Zero)
            {
                InfoBanner.Visibility = _infoErrorUntil > DateTime.Now ? Visibility.Visible : Visibility.Collapsed;
                if (InfoBanner.Visibility == Visibility.Visible) InfoMessage.Text = _infoError ?? "";
                return;
            }
            var audio = NativeCapture.ClipprGetAudioStatus(_capture);
            var audioErrors = new List<string>();
            var processAudioSelected = _gameAudioPid != 0 || _chatAudioPid != 0 || _musicAudioPid != 0;
            if (!processAudioSelected && _settings.DesktopAudioEnabled && audio.DesktopError < 0)
                audioErrors.Add(FormatAudioCaptureError("Desktop audio", audio.DesktopError));
            if (_settings.MicrophoneAudioEnabled && audio.MicrophoneError < 0)
                audioErrors.Add(FormatAudioCaptureError("Microphone", audio.MicrophoneError));
            if (_gameAudioPid != 0 && audio.GameError < 0)
                audioErrors.Add(FormatAudioCaptureError("Game audio", audio.GameError));
            if (_chatAudioPid != 0 && audio.ChatError < 0)
                audioErrors.Add(FormatAudioCaptureError("App audio", audio.ChatError));
            if (_musicAudioPid != 0 && audio.MusicError < 0)
                audioErrors.Add(FormatAudioCaptureError("Music audio", audio.MusicError));
            if (_infoErrorUntil > DateTime.Now && _infoError is not null) audioErrors.Insert(0, _infoError);
            InfoBanner.Visibility = audioErrors.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            if (audioErrors.Count > 0) InfoMessage.Text = string.Join(" · ", audioErrors);
            FixNowButton.Visibility = _settings.MicrophoneAudioEnabled &&
                audio.MicrophoneError == unchecked((int)0x80070005) ? Visibility.Visible : Visibility.Collapsed;
        }

        private void ShowClipSavedNotification(bool shorter)
        {
            ShowNotification(ClipNotificationKind.Saved, "Clip saved", shorter
                ? "Saved the available replay, which was shorter than the requested duration."
                : "Your clip is ready in the selected clip folder.");
        }

        internal void PreviewNotification(string style)
        {
            ShowNotification(ClipNotificationKind.Ready, "Ready to clip",
                "ClipDat is ready to capture your last moments.", style);
        }

        private void ShowNotification(ClipNotificationKind kind, string title, string message, string? style = null)
        {
            try
            {
                var now = DateTime.UtcNow;
                while (_notificationHistory.Count > 0 && now - _notificationHistory.Peek() >= TimeSpan.FromSeconds(10))
                    _notificationHistory.Dequeue();
                foreach (var key in _notificationLastShown.Where(pair => now - pair.Value >= TimeSpan.FromSeconds(10))
                             .Select(pair => pair.Key).ToArray())
                    _notificationLastShown.Remove(key);

                var notificationKey = $"{kind}\u001f{title}\u001f{message}";
                if ((_notificationLastShown.TryGetValue(notificationKey, out var lastShown) &&
                     now - lastShown < TimeSpan.FromSeconds(2)) || _notificationHistory.Count >= 4)
                    return;

                _notificationHistory.Enqueue(now);
                _notificationLastShown[notificationKey] = now;
                if ((style ?? _settings.NotificationStyle) == "ClipDat")
                {
                    if (_notificationWindow is not null)
                    {
                        _notificationWindow.Update(kind, title, message);
                        return;
                    }
                    _notificationWindow = new ClipNotificationWindow(this, kind, title, message,
                        window => { if (ReferenceEquals(_notificationWindow, window)) _notificationWindow = null; });
                }
                else
                {
                    var xml = new XmlDocument();
                    xml.LoadXml("<toast><visual><binding template=\"ToastGeneric\">" +
                        "<text>ClipDat · " + System.Security.SecurityElement.Escape(title) + "</text><text>" +
                        System.Security.SecurityElement.Escape(message) +
                        "</text></binding></visual></toast>");
                    ToastNotificationManager.CreateToastNotifier().Show(new ToastNotification(xml));
                }
            }
            catch (Exception ex) { ShowInfoError($"Could not show notification: {ex.Message}"); }
        }

        private async void OnFixNowClick(object sender, RoutedEventArgs e)
        {
            if (!await Launcher.LaunchUriAsync(new Uri("ms-settings:privacy-microphone")))
                InfoMessage.Text = "Open Windows Settings > Privacy & security > Microphone to allow ClipDat access.";
        }

        private static string FormatAudioCaptureError(string source, int error)
        {
            if (error == unchecked((int)0x80070005))
                return source == "Microphone"
                    ? "Microphone access denied by Windows · 0x80070005"
                    : $"{source} access denied by Windows · 0x80070005";
            if ((error == unchecked((int)0x80070032) || error == unchecked((int)0x80004001)) &&
                (source == "Game audio" || source == "App audio"))
                return $"{source} needs process loopback support (Windows build 20348 or later) · 0x{error:X8}";
            return $"{source} unavailable · 0x{error:X8}";
        }

        private static NativeCapture.AudioDevice[] EnumerateAudioDevices(bool renderDevices)
        {
            Marshal.ThrowExceptionForHR(NativeCapture.ClipprEnumerateAudioDevices(renderDevices ? 1 : 0,
                Array.Empty<NativeCapture.AudioDevice>(), 0, out var capacity));
            var devices = new NativeCapture.AudioDevice[capacity];
            Marshal.ThrowExceptionForHR(NativeCapture.ClipprEnumerateAudioDevices(renderDevices ? 1 : 0,
                devices, devices.Length, out var count));
            return devices.Take(Math.Min(count, devices.Length)).ToArray();
        }

        private static NativeCapture.Window[] EnumerateAudioApplications()
        {
            Marshal.ThrowExceptionForHR(NativeCapture.ClipprEnumerateWindows(Array.Empty<NativeCapture.Window>(), 0, out var capacity));
            var windows = new NativeCapture.Window[capacity];
            Marshal.ThrowExceptionForHR(NativeCapture.ClipprEnumerateWindows(windows, windows.Length, out var count));
            return windows.Take(Math.Min(count, windows.Length)).Where(w => w.ProcessId != 0)
                .GroupBy(w => w.ProcessId).Select(group => group.First()).OrderBy(w => w.ApplicationName).ToArray();
        }

        private async void OnSaveReplayClick(object sender, RoutedEventArgs e) => await SaveReplayAsync();

        private async Task SaveReplayAsync(uint? durationSeconds = null)
        {
            if (_capture == IntPtr.Zero) return;
            if (NativeCapture.ClipprGetReplayStatus(_capture).SavesActive > 0) return;
            string path;
            try
            {
                Directory.CreateDirectory(_clipFolder);
                if (!HasOutputSpace())
                {
                    ShowInfoError($"Clip not saved. Keep at least {_settings.MinimumFreeDiskSpaceGb} GB free on the clips drive.");
                    return;
                }
                var stem = $"ClipDat-{DateTime.Now:yyyyMMdd-HHmmss-fff}";
                do
                {
                    var sequence = Interlocked.Increment(ref _clipSequence);
                    path = Path.Combine(_clipFolder, $"{stem}-{sequence:D3}-{Guid.NewGuid():N}.{_clipFormat}");
                } while (File.Exists(path));
            }
            catch (Exception ex)
            {
                ShowInfoError($"Cannot create clip folder: {ex.Message}");
                return;
            }
            await _captureGate.WaitAsync();
            try
            {
                if (_capture == IntPtr.Zero || _closing) return;
                var capture = _capture;
                _pendingReplayPaths.Enqueue(path);
                var duration = durationSeconds ?? _replaySeconds;
                var result = await Task.Run(() => NativeCapture.ClipprSaveReplay(capture, duration, path));
                if (result < 0)
                {
                    if (_pendingReplayPaths.Count > 0) _pendingReplayPaths.Dequeue();
                    LocalReliabilityLog.Error("clip-export", result);
                    ShowInfoError(result == unchecked((int)0x80070015)
                        ? "Replay is waiting for a keyframe" : FormatExportError(result));
                }
                else ReplayMessage.Text = "Saving replay…";
            }
            finally { _captureGate.Release(); }
        }

        private static string FormatExportError(int error) => error switch
        {
            unchecked((int)0x80070070) => "Clip export failed: disk is full",
            unchecked((int)0x80070005) => "Clip export failed: folder access was denied",
            unchecked((int)0x80070003) or unchecked((int)0x800700A1) => "Clip export failed: folder path is unavailable",
            unchecked((int)0x80070050) or unchecked((int)0x800700B7) => "Clip export failed: filename already exists",
            _ => $"Clip export failed: 0x{error:X8}"
        };

        private async void OnClipFormatClick(object sender, RoutedEventArgs e)
        {
            if (sender is not MenuFlyoutItem item || item.Tag is not string format ||
                (format != "mp4" && format != "mkv")) return;
            var old = _clipFormat;
            _clipFormat = format;
            if (await ApplyReplayOutputSettingsAsync())
            {
                _settings.VideoFormat = format;
                ReplayMessage.Text = $"Clip format · {format.ToUpperInvariant()}";
            }
            else _clipFormat = old;
        }

        private async void OnChooseClipFolderClick(object sender, RoutedEventArgs e)
        {
            try
            {
                var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.VideosLibrary };
                picker.FileTypeFilter.Add("*");
                WinRT.Interop.InitializeWithWindow.Initialize(picker,
                    new IntPtr(unchecked((long)AppWindow.Id.Value)));
                var selected = await picker.PickSingleFolderAsync();
                if (selected is null) return;
                var old = _clipFolder;
                _clipFolder = selected.Path;
                if (await ApplyReplayOutputSettingsAsync())
                {
                    _settings.ClipFolder = _clipFolder;
                    ReplayMessage.Text = $"Clip folder · {_clipFolder}";
                }
                else _clipFolder = old;
            }
            catch (Exception ex) { ReplayMessage.Text = $"Cannot choose clip folder: {ex.Message}"; }
        }

        private async Task<bool> ApplyReplayOutputSettingsAsync()
        {
            await _captureGate.WaitAsync();
            try
            {
                if (_capture == IntPtr.Zero) return true;
                var capture = _capture;
                var result = await Task.Run(() => NativeCapture.ClipprConfigureReplayOutput(
                    capture, _clipFolder, _clipFormat));
                if (result >= 0) return true;
                ReplayMessage.Text = FormatExportError(result);
                return false;
            }
            finally { _captureGate.Release(); }
        }

        private void OnReplayDurationClick(object sender, RoutedEventArgs e)
        {
            if (sender is MenuFlyoutItem item && uint.TryParse(item.Tag?.ToString(), out var seconds))
                _ = ChangeReplaySettingsAsync(seconds, _replayRamMb);
        }

        private void OnReplayRamClick(object sender, RoutedEventArgs e)
        {
            if (sender is MenuFlyoutItem item && ulong.TryParse(item.Tag?.ToString(), out var megabytes))
                _ = ChangeReplaySettingsAsync(_replaySeconds, megabytes);
        }

        private async void OnCustomReplayDurationClick(object sender, RoutedEventArgs e)
        {
            var input = new NumberBox { Minimum = 1, Maximum = 3600, Value = _replaySeconds,
                                        SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline };
            var dialog = new ContentDialog { Title = "Replay length in seconds", Content = input,
                                             PrimaryButtonText = "Apply", CloseButtonText = "Cancel",
                                             XamlRoot = ((FrameworkElement)Content).XamlRoot };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary &&
                !double.IsNaN(input.Value) && input.Value is >= 1 and <= 3600)
                await ChangeReplaySettingsAsync((uint)input.Value, _replayRamMb);
        }

        private async Task ChangeReplaySettingsAsync(uint seconds, ulong ramMb)
        {
            await _captureGate.WaitAsync();
            try
            {
                if (_capture == IntPtr.Zero || _closing) return;
                ReplayMessage.Text = "Applying replay settings…";
                var capture = _capture;
                var result = await Task.Run(() => NativeCapture.ClipprConfigureReplay(
                    capture, (uint)Math.Max(seconds, _settings.VoiceCommandsEnabled ? 60 : (int)seconds), ramMb * 1024 * 1024, 8UL * 1024 * 1024 * 1024,
                    (ulong)_settings.MinimumFreeDiskSpaceGb * 1024 * 1024 * 1024));
                if (result < 0) ReplayMessage.Text = $"Replay settings failed: 0x{result:X8}";
                else
                {
                    _replaySeconds = seconds;
                    _replayRamMb = ramMb;
                    _settings.ClipLengthSeconds = (int)seconds;
                    _settings.ReplayMemoryLimitMb = (int)ramMb;
                    ReplayMessage.Text = $"Replay reset · {seconds}s · {ramMb} MB RAM";
                }
            }
            finally { _captureGate.Release(); }
        }

        private void UpdateClippingButton(bool active)
        {
            ClippingButtonLabel.Text = active ? "Stop clipping" : "Start clipping";
            ClippingButtonIcon.Glyph = active ? "\uE71A" : "\uE768";
            ClippingButton.Background = new SolidColorBrush(active
                ? Microsoft.UI.ColorHelper.FromArgb(255, 169, 67, 73)
                : Microsoft.UI.ColorHelper.FromArgb(255, 53, 121, 82));
            ClippingButton.BorderBrush = new Microsoft.UI.Xaml.Media.SolidColorBrush(active
                ? Microsoft.UI.ColorHelper.FromArgb(255, 190, 85, 91)
                : Microsoft.UI.ColorHelper.FromArgb(255, 69, 142, 99));
            SaveReplayButton.IsEnabled = active;
        }

        private void OnAdditionalSourcesClick(object sender, RoutedEventArgs e)
        {
            var expanded = !_additionalAudioExpanded;
            _additionalAudioExpanded = expanded;
            _additionalAudioAnimation?.Stop();
            const double expandedHeight = 140;
            var startHeight = AdditionalAudioContainer.Height;
            var targetHeight = expanded ? expandedHeight : 0;
            var started = DateTime.UtcNow;
            var timer = DispatcherQueue.CreateTimer();
            timer.Interval = TimeSpan.FromMilliseconds(16);
            timer.Tick += (_, _) =>
            {
                var progress = Math.Clamp((DateTime.UtcNow - started).TotalMilliseconds / 340, 0, 1);
                var eased = progress * progress * (3 - 2 * progress);
                var height = startHeight + (targetHeight - startHeight) * eased;
                AdditionalAudioContainer.Height = height;
                AdditionalAudioSources.Opacity = Math.Clamp(height / expandedHeight, 0, 1);
                if (progress >= 1) timer.Stop();
            };
            _additionalAudioAnimation = timer;
            timer.Start();
            AdditionalSourcesChevron.Glyph = expanded ? "\uE70E" : "\uE70D";
            AdditionalSourcesLabel.Text = expanded ? "Hide additional audio sources" : "Show additional audio sources";
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(AdditionalSourcesButton, AdditionalSourcesLabel.Text);
        }

        private async void OnClippingClick(object sender, RoutedEventArgs e)
        {
            var enable = _capture == IntPtr.Zero || !_clippingEnabled || NativeCapture.ClipprGetReplayStatus(_capture).State is 0 or 3;
            await SetClippingAsync(enable);
        }

        private async Task SetClippingAsync(bool enable)
        {
            if (_capture == IntPtr.Zero || _closing) return;
            if (enable == _clippingEnabled && NativeCapture.ClipprGetReplayStatus(_capture).State is 1 or 2) return;
            ClippingButton.IsEnabled = false;
            try
            {
                await _captureGate.WaitAsync();
                try
                {
                    var result = await Task.Run(() => NativeCapture.ClipprSetReplayEnabled(_capture, enable ? 1 : 0));
                    if (result < 0) Marshal.ThrowExceptionForHR(result);
                    _clippingEnabled = enable;
                }
                finally { _captureGate.Release(); }
                if (enable && _activeSource != _selectedSource)
                {
                    if (_selectedSource == CaptureSource.Screen)
                    {
                        RefreshDisplays();
                        if (_selectedMonitor != IntPtr.Zero) await BeginScreenCaptureAsync(_selectedMonitor);
                    }
                    else if (_selectedWindow != IntPtr.Zero) await BeginWindowCaptureAsync(_selectedWindow);
                    else await RefreshWindowsAsync(resumeSelectedWindow: true);
                }
                ReplayMessage.Text = enable ? "Starting clipping…" : "Clipping stopped · preview remains live";
                UpdateClippingButton(enable);
            }
            catch (Exception ex) { ReplayMessage.Text = $"Could not change clipping: {ex.Message}"; }
            finally { ClippingButton.IsEnabled = true; }
        }

        private void PopulateAudioControls()
        {
            _audioUiReady = false;
            GameAudioPicker.Items.Clear();
            ChatAudioPicker.Items.Clear();
            MusicAudioPicker.Items.Clear();
            GameAudioPicker.Items.Add(new ComboBoxItem { Content = "Game · Off", Tag = "0" });
            ChatAudioPicker.Items.Add(new ComboBoxItem { Content = "App / chat · Off", Tag = "0" });
            MusicAudioPicker.Items.Add(new ComboBoxItem { Content = "Music · Off", Tag = "0" });
            try
            {
                var apps = EnumerateAudioApplications().GroupBy(window => window.ProcessId)
                    .ToDictionary(group => group.Key, group => group.First().ApplicationName);
                foreach (var process in Process.GetProcesses())
                {
                    using (process)
                    {
                        try
                        {
                            if (IsMusicApplication(process.ProcessName))
                                apps.TryAdd((uint)process.Id, process.ProcessName);
                        }
                        catch { }
                    }
                }
                foreach (var app in apps.OrderByDescending(pair => IsMusicApplication(pair.Value)).ThenBy(pair => pair.Value))
                {
                    GameAudioPicker.Items.Add(new ComboBoxItem { Content = app.Value, Tag = app.Key.ToString() });
                    ChatAudioPicker.Items.Add(new ComboBoxItem { Content = app.Value, Tag = app.Key.ToString() });
                    MusicAudioPicker.Items.Add(new ComboBoxItem
                    {
                        Content = IsMusicApplication(app.Value) ? $"{app.Value} · detected music" : app.Value,
                        Tag = app.Key.ToString()
                    });
                }
            }
            catch (Exception ex) { ReplayMessage.Text = $"Cannot list audio applications: {ex.Message}"; }
            SelectAudioApplication(GameAudioPicker, _settings.GameAudioProcessId);
            SelectAudioApplication(ChatAudioPicker, _settings.DiscordAudioProcessId);
            SelectAudioApplication(MusicAudioPicker, _settings.MusicAudioProcessId);
            DesktopVolumeSlider.Value = _settings.DesktopVolume;
            MicrophoneVolumeSlider.Value = _settings.MicrophoneVolume;
            GameVolumeSlider.Value = _settings.GameVolume;
            ChatVolumeSlider.Value = _settings.ChatVolume;
            MusicVolumeSlider.Value = _settings.MusicVolume;
            RefreshAudioLevelUi();
            _audioUiReady = true;
        }

        private static bool IsMusicApplication(string name) =>
            name.Contains("Spotify", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("TIDAL", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("MusicBee", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("iTunes", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Apple Music", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Deezer", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Amazon Music", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("YouTube Music", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("foobar2000", StringComparison.OrdinalIgnoreCase);

        private void AddDetectedMusicApplications()
        {
            foreach (var process in Process.GetProcesses())
            {
                using (process)
                {
                    try
                    {
                        if (!IsMusicApplication(process.ProcessName)) continue;
                        var id = process.Id.ToString();
                        if (MusicAudioPicker.Items.OfType<ComboBoxItem>().Any(item => item.Tag?.ToString() == id)) continue;
                        MusicAudioPicker.Items.Add(new ComboBoxItem
                        {
                            Content = $"{process.ProcessName} · detected music", Tag = id
                        });
                    }
                    catch { }
                }
            }
        }

        private static void SelectAudioApplication(ComboBox picker, uint processId)
        {
            var choice = picker.Items.OfType<ComboBoxItem>()
                .FirstOrDefault(item => item.Tag?.ToString() == processId.ToString());
            if (choice is null && processId != 0)
            {
                choice = new ComboBoxItem { Content = $"Unavailable ({processId})", Tag = processId.ToString() };
                picker.Items.Add(choice);
            }
            picker.SelectedItem = choice ?? picker.Items[0];
        }

        private static uint SelectedAudioProcess(ComboBox picker) =>
            uint.TryParse((picker.SelectedItem as ComboBoxItem)?.Tag?.ToString(), out var pid) ? pid : 0;

        private async void OnAudioApplicationChanged(object sender, SelectionChangedEventArgs e) => await ApplyAudioControlsAsync(sender);

        private void RefreshAudioLevelUi()
        {
            SetMuteButton(DesktopMuteButton, _settings.DesktopMuted, "desktop");
            SetMuteButton(MicrophoneMuteButton, _settings.MicrophoneMuted, "microphone");
            SetMuteButton(GameMuteButton, _settings.GameMuted, "game");
            SetMuteButton(ChatMuteButton, _settings.ChatMuted, "chat");
            SetMuteButton(MusicMuteButton, _settings.MusicMuted, "music");
            var processTrackSelected = _settings.GameAudioProcessId != 0 ||
                _settings.DiscordAudioProcessId != 0 || _settings.MusicAudioProcessId != 0;
            DesktopMuteButton.IsEnabled = _settings.DesktopAudioEnabled || !processTrackSelected;
            DesktopVolumeSlider.IsEnabled = _settings.DesktopAudioEnabled;
            MicrophoneVolumeSlider.IsEnabled = _settings.MicrophoneAudioEnabled;
            GameMuteButton.IsEnabled = GameVolumeSlider.IsEnabled = _settings.GameAudioProcessId != 0;
            ChatMuteButton.IsEnabled = ChatVolumeSlider.IsEnabled = _settings.DiscordAudioProcessId != 0;
            MusicMuteButton.IsEnabled = MusicVolumeSlider.IsEnabled = _settings.MusicAudioProcessId != 0;
            SetMutedAppearance(DesktopVolumeSlider, DesktopVolumeLabel, _settings.DesktopMuted);
            SetMutedAppearance(MicrophoneVolumeSlider, MicrophoneVolumeLabel, _settings.MicrophoneMuted);
            SetMutedAppearance(GameVolumeSlider, GameVolumeLabel, _settings.GameMuted);
            SetMutedAppearance(ChatVolumeSlider, ChatVolumeLabel, _settings.ChatMuted);
            SetMutedAppearance(MusicVolumeSlider, MusicVolumeLabel, _settings.MusicMuted);
            DesktopVolumeLabel.Text = $"{_settings.DesktopVolume}%";
            MicrophoneVolumeLabel.Text = $"{_settings.MicrophoneVolume}%";
            GameVolumeLabel.Text = $"{_settings.GameVolume}%";
            ChatVolumeLabel.Text = $"{_settings.ChatVolume}%";
            MusicVolumeLabel.Text = $"{_settings.MusicVolume}%";
        }

        private void OnAudioMeterTick(object? sender, object e)
        {
            var levels = _capture != IntPtr.Zero && !_closing
                ? NativeCapture.ClipprTakeAudioLevels(_capture) : default;
            UpdateAudioMeter(0, DesktopMeterTrack, DesktopMeter, levels.Desktop);
            UpdateAudioMeter(1, MicrophoneMeterTrack, MicrophoneMeter, levels.Microphone);
            UpdateAudioMeter(2, GameMeterTrack, GameMeter, levels.Game);
            UpdateAudioMeter(3, ChatMeterTrack, ChatMeter, levels.Chat);
            UpdateAudioMeter(4, MusicMeterTrack, MusicMeter, levels.Music);
        }

        private void UpdateAudioMeter(int index, Grid track, Border meter, float samplePeak)
        {
            // Show captured signal in dB so quieter voices remain visible without
            // changing the volume slider's scale or the recorded audio.
            var target = samplePeak > 0.004f
                ? Math.Clamp((20 * Math.Log10(samplePeak) + 48) / 48, 0, 1) : 0;
            _displayedAudioLevels[index] = Math.Max(target, _displayedAudioLevels[index] * 0.84);
            if (_displayedAudioLevels[index] < 0.01) _displayedAudioLevels[index] = 0;
            if (meter.Clip is RectangleGeometry clip)
                clip.Rect = new Rect(0, 0, Math.Max(0, track.ActualWidth - 24) * _displayedAudioLevels[index], 3);
        }

        private static void ConfigureAudioMeter(Border meter)
        {
            var gradient = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(1, 0)
            };
            gradient.GradientStops.Add(new GradientStop { Color = ColorHelper.FromArgb(255, 63, 190, 107), Offset = 0 });
            gradient.GradientStops.Add(new GradientStop { Color = ColorHelper.FromArgb(255, 103, 204, 104), Offset = 0.55 });
            gradient.GradientStops.Add(new GradientStop { Color = ColorHelper.FromArgb(255, 248, 194, 71), Offset = 0.78 });
            gradient.GradientStops.Add(new GradientStop { Color = ColorHelper.FromArgb(255, 234, 79, 75), Offset = 1 });
            meter.Background = gradient;
            meter.Opacity = 0.82;
            meter.Clip = new RectangleGeometry { Rect = new Rect(0, 0, 0, 3) };
        }

        private static void SetMuteButton(Button button, bool muted, string source)
        {
            if (button.Content is FontIcon icon) icon.Glyph = muted ? "\uE74F" : "\uE767";
            var action = muted ? "Unmute" : "Mute";
            var label = $"{action} {source} audio";
            ToolTipService.SetToolTip(button, label);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, label);
        }

        private static void SetMutedAppearance(Slider slider, TextBlock label, bool muted)
        {
            slider.Opacity = muted ? 0.45 : 1;
            label.Opacity = muted ? 0.55 : 1;
        }

        private void ApplyAudioLevels()
        {
            if (_capture == IntPtr.Zero) return;
            foreach (var (stream, volume, muted) in new[]
            {
                (1, _settings.DesktopVolume, _settings.DesktopMuted),
                (2, _settings.MicrophoneVolume, _settings.MicrophoneMuted),
                (3, _settings.GameVolume, _settings.GameMuted),
                (4, _settings.ChatVolume, _settings.ChatMuted),
                (5, _settings.MusicVolume, _settings.MusicMuted)
            })
            {
                var result = NativeCapture.ClipprSetAudioGain(_capture, stream, muted ? 0 : volume / 100f);
                if (result < 0) Marshal.ThrowExceptionForHR(result);
            }
        }

        private void ScheduleAudioSave()
        {
            _audioSaveTimer.Stop();
            _audioSaveTimer.Start();
        }

        private void OnAudioVolumeChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            if (!_audioUiReady || sender is not FrameworkElement element) return;
            var value = (int)Math.Round(e.NewValue);
            switch (element.Name)
            {
                case "DesktopVolumeSlider": _settings.DesktopVolume = value; break;
                case "MicrophoneVolumeSlider": _settings.MicrophoneVolume = value; break;
                case "GameVolumeSlider": _settings.GameVolume = value; break;
                case "ChatVolumeSlider": _settings.ChatVolume = value; break;
                case "MusicVolumeSlider": _settings.MusicVolume = value; break;
                default: return;
            }
            RefreshAudioLevelUi();
            try { ApplyAudioLevels(); ScheduleAudioSave(); }
            catch (Exception ex) { ReplayMessage.Text = $"Audio level failed: {ex.Message}"; }
        }

        private async void OnAudioMuteClick(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement element || _audioApplying) return;
            var enableSource = false;
            switch (element.Name)
            {
                case "DesktopMuteButton":
                    if (!_settings.DesktopAudioEnabled && _gameAudioPid == 0 && _chatAudioPid == 0 && _musicAudioPid == 0)
                    { _settings.DesktopAudioEnabled = true; enableSource = true; _settings.DesktopMuted = false; }
                    else _settings.DesktopMuted = !_settings.DesktopMuted;
                    break;
                case "MicrophoneMuteButton":
                    if (!_settings.MicrophoneAudioEnabled)
                    { _settings.MicrophoneAudioEnabled = true; enableSource = true; _settings.MicrophoneMuted = false; }
                    else _settings.MicrophoneMuted = !_settings.MicrophoneMuted;
                    break;
                case "GameMuteButton": _settings.GameMuted = !_settings.GameMuted; break;
                case "ChatMuteButton": _settings.ChatMuted = !_settings.ChatMuted; break;
                case "MusicMuteButton": _settings.MusicMuted = !_settings.MusicMuted; break;
                default: return;
            }
            RefreshAudioLevelUi();
            try
            {
                if (enableSource && _capture != IntPtr.Zero)
                {
                    var result = await Task.Run(() => NativeCapture.ClipprConfigureAudio(_capture,
                        _settings.DesktopAudioEnabled ? 1 : 0, _settings.MicrophoneAudioEnabled ? 1 : 0));
                    if (result < 0) Marshal.ThrowExceptionForHR(result);
                }
                ApplyAudioLevels();
                ScheduleAudioSave();
            }
            catch (Exception ex) { ReplayMessage.Text = $"Could not mute audio: {ex.Message}"; }
        }

        private async Task ApplyAudioControlsAsync(object sender)
        {
            if (!_audioUiReady || _audioApplying || _closing) return;
            _audioApplying = true;
            try
            {
                var draft = _settings.Clone();
                draft.GameAudioProcessId = SelectedAudioProcess(GameAudioPicker);
                draft.DiscordAudioProcessId = SelectedAudioProcess(ChatAudioPicker);
                draft.MusicAudioProcessId = SelectedAudioProcess(MusicAudioPicker);
                var changedPicker = (sender as FrameworkElement)?.Name;
                var selected = changedPicker switch
                {
                    "GameAudioPicker" => draft.GameAudioProcessId,
                    "ChatAudioPicker" => draft.DiscordAudioProcessId,
                    _ => draft.MusicAudioProcessId
                };
                if (selected != 0)
                {
                    if (changedPicker != "GameAudioPicker" && draft.GameAudioProcessId == selected) draft.GameAudioProcessId = 0;
                    if (changedPicker != "ChatAudioPicker" && draft.DiscordAudioProcessId == selected) draft.DiscordAudioProcessId = 0;
                    if (changedPicker != "MusicAudioPicker" && draft.MusicAudioProcessId == selected) draft.MusicAudioProcessId = 0;
                }
                draft.DesktopAudioEnabled = draft.GameAudioProcessId == 0 && draft.DiscordAudioProcessId == 0 && draft.MusicAudioProcessId == 0;
                if (_capture != IntPtr.Zero)
                {
                    await _captureGate.WaitAsync();
                    try
                    {
                        var capture = _capture;
                        var result = await Task.Run(() => NativeCapture.ClipprConfigureMusicAudio(capture, 0));
                        if (result >= 0) result = await Task.Run(() => NativeCapture.ClipprConfigureProcessAudio(
                            capture, draft.GameAudioProcessId, draft.DiscordAudioProcessId));
                        if (result >= 0) result = await Task.Run(() => NativeCapture.ClipprConfigureMusicAudio(capture, draft.MusicAudioProcessId));
                        if (result >= 0) result = await Task.Run(() => NativeCapture.ClipprConfigureAudio(capture,
                            draft.DesktopAudioEnabled ? 1 : 0, draft.MicrophoneAudioEnabled ? 1 : 0));
                        if (result < 0) Marshal.ThrowExceptionForHR(result);
                    }
                    finally { _captureGate.Release(); }
                }
                draft.SaveAtomicJson();
                _settings = draft;
                _gameAudioPid = draft.GameAudioProcessId;
                _chatAudioPid = draft.DiscordAudioProcessId;
                _musicAudioPid = draft.MusicAudioProcessId;
                PopulateAudioControls();
                ReplayMessage.Text = "Audio tracks updated";
                _replayMessageUntil = DateTime.Now.AddSeconds(3);
            }
            catch (Exception ex)
            {
                PopulateAudioControls();
                ReplayMessage.Text = $"Could not update audio tracks: {ex.Message}";
                _replayMessageUntil = DateTime.Now.AddSeconds(8);
            }
            finally { _audioApplying = false; }
        }

        private void ShowRecordingOptions()
        {
            if (_recording) return;
            EncodingMessage.Text = $"{_recordingFps} FPS · {_recordingBitrate / 1_000_000} Mbps · " +
                                   (_recordingFullHd ? "1080p" : "source resolution");
        }

        private async Task ReconnectDisplayAsync()
        {
            if (!CanAttemptCaptureRecovery()) return;
            _reconnecting = true;
            try
            {
                RefreshDisplays();
                if (_selectedMonitor != IntPtr.Zero) await BeginScreenCaptureAsync(_selectedMonitor);
                else await StopCaptureAsync();
            }
            catch (Exception ex) { LocalReliabilityLog.Error("display-reconnect", ex.HResult); }
            finally
            {
                _reconnecting = false;
            }
        }

        private async Task RecoverDeviceAsync()
        {
            if (!CanAttemptCaptureRecovery()) return;
            _recovering = true;
            await _captureGate.WaitAsync();
            try
            {
                if (_closing || _capture == IntPtr.Zero) return;
                var capture = _capture;
                await Task.Run(() => NativeCapture.ClipprStopCapture(capture));
                NativeCapture.ClipprDestroyCapture(capture);
                _capture = IntPtr.Zero;
                _activeSource = null;
                _activeTarget = IntPtr.Zero;
                OnPreviewLoaded(PreviewPanel, new RoutedEventArgs());
            }
            catch (Exception ex)
            {
                LocalReliabilityLog.Error("capture-recovery", ex.HResult);
                ShowInfoError("Capture could not recover from the graphics device reset. Select a source to retry.");
            }
            finally
            {
                _captureGate.Release();
                _recovering = false;
            }
        }

        private async void OnSystemPowerChanged(bool resumed)
        {
            if (_closing) return;
            if (!resumed)
            {
                if (_suspended) return;
                _suspended = true;
                _resumeCaptureAfterSleep = _activeSource.HasValue;
                _captureTimer.Stop();
                _audioMeterTimer.Stop();
                if (_recording)
                {
                    _recording = false;
                    LocalReliabilityLog.Error("recording", unchecked((int)0x800704C7), "Recording stopped for Windows sleep");
                    ShowInfoError("Recording stopped because Windows is going to sleep. Replay capture will resume when Windows wakes.");
                }
                await StopCaptureAsync();
                return;
            }

            if (!_suspended) return;
            _suspended = false;
            _captureTimer.Start();
            _audioMeterTimer.Start();
            if (!_resumeCaptureAfterSleep) return;
            _resumeCaptureAfterSleep = false;
            await Task.Delay(500);
            if (_closing || _capture == IntPtr.Zero) return;
            if (_selectedSource == CaptureSource.Screen && _selectedMonitor != IntPtr.Zero)
                await BeginScreenCaptureAsync(_selectedMonitor);
            else if (_selectedSource == CaptureSource.Application && _selectedWindow != IntPtr.Zero &&
                     NativeCapture.ClipprGetCaptureStatus(_capture).State != 2)
                await BeginWindowCaptureAsync(_selectedWindow);
        }

        private void OnDisplayConfigurationChanged()
        {
            if (_closing || _suspended || _selectedSource != CaptureSource.Screen) return;
            var wasCapturing = _activeSource == CaptureSource.Screen || _startCaptureAutomatically;
            RefreshDisplays();
            if (wasCapturing && _selectedMonitor != IntPtr.Zero)
                _ = ReconnectDisplayAsync();
        }

        private void OnPreviewSizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (e.NewSize.Width < 1 || e.NewSize.Height < 1) return;
            var previewVisual = ElementCompositionPreview.GetElementVisual(PreviewPanel);
            var rounded = previewVisual.Compositor.CreateRoundedRectangleGeometry();
            rounded.Size = new Vector2((float)e.NewSize.Width, (float)e.NewSize.Height);
            rounded.CornerRadius = new Vector2(11, 11);
            previewVisual.Clip = previewVisual.Compositor.CreateGeometricClip(rounded);
            if (_capture == IntPtr.Zero) return;
            var scale = PreviewPanel.XamlRoot?.RasterizationScale ?? 1;
            var width = Math.Max(1, (int)Math.Round(e.NewSize.Width * scale));
            var height = Math.Max(1, (int)Math.Round(e.NewSize.Height * scale));
            var result = NativeCapture.ClipprResizePreview(_capture, width, height);
            if (result < 0) ShowPreviewMessage($"Preview resize failed: 0x{result:X8}");
        }

        private void OnReliabilityTick(object? sender, object e)
        {
            UpdateStorageHealth();
            CheckAudioCaptureHealth();
        }

        private void UpdateStorageHealth()
        {
            if (DateTime.UtcNow - _lastStorageSampleUtc < TimeSpan.FromSeconds(5)) return;
            _lastStorageSampleUtc = DateTime.UtcNow;
            var threshold = (ulong)_settings.MinimumFreeDiskSpaceGb * 1024 * 1024 * 1024;
            var clipFree = GetAvailableBytes(_clipFolder);
            var spoolFree = GetAvailableBytes(Path.GetTempPath());
            var low = clipFree < threshold || spoolFree < threshold;
            if (low && !_storageWarningActive)
            {
                _storageWarningActive = true;
                LocalReliabilityLog.Error("storage", unchecked((int)0x80070070), "Free space reached configured reserve");
                ShowInfoError($"Low storage. Keep at least {_settings.MinimumFreeDiskSpaceGb} GB free on the clip and replay drives.");
            }
            else if (!low && _storageWarningActive)
            {
                _storageWarningActive = false;
                ShowInfoError("Storage space is back above the configured reserve.");
            }

            var captureText = _capture == IntPtr.Zero ? "Capture not initialized" :
                NativeCapture.ClipprGetCaptureStatus(_capture).State switch
                {
                    1 => "Capture running", 2 => "Capture target disconnected", 3 => "Capture error",
                    5 => "Application capture minimized", _ => "Capture stopped"
                };
            var replayText = _capture != IntPtr.Zero && NativeCapture.ClipprGetReplayStatus(_capture).State == 2
                ? "replay buffering" : "replay not buffering";
            var health = $"{captureText} · {replayText}\nClip drive: {FormatFreeSpace(clipFree)} free · Replay spool: {FormatFreeSpace(spoolFree)} free · Reserve: {_settings.MinimumFreeDiskSpaceGb} GB";
            _settingsView?.RefreshReliabilityDiagnostics(health, LocalReliabilityLog.ReadRecent());
        }

        private static ulong GetAvailableBytes(string directory)
        {
            try
            {
                var existing = Path.GetFullPath(directory);
                while (!Directory.Exists(existing))
                {
                    var parent = Directory.GetParent(existing)?.FullName;
                    if (string.IsNullOrEmpty(parent) || parent == existing) return 0;
                    existing = parent;
                }
                return GetDiskFreeSpaceEx(existing, out var available, out _, out _) ? available : 0;
            }
            catch { return 0; }
        }

        private static string FormatFreeSpace(ulong bytes) => bytes == 0 ? "Unavailable" : $"{bytes / 1073741824d:0.0} GB";

        private bool HasOutputSpace() => GetAvailableBytes(_clipFolder) >=
            (ulong)_settings.MinimumFreeDiskSpaceGb * 1024 * 1024 * 1024;

        private void CheckAudioCaptureHealth()
        {
            if (_capture == IntPtr.Zero || _closing || _suspended) return;
            var audio = NativeCapture.ClipprGetAudioStatus(_capture);
            var errors = new List<(string Name, int Code)>();
            var appAudioEnabled = _gameAudioPid != 0 || _chatAudioPid != 0 || _musicAudioPid != 0;
            if (_settings.DesktopAudioEnabled && !appAudioEnabled && audio.DesktopError < 0) errors.Add(("desktop audio", audio.DesktopError));
            if (_settings.MicrophoneAudioEnabled && audio.MicrophoneError < 0) errors.Add(("microphone", audio.MicrophoneError));
            if (_gameAudioPid != 0 && audio.GameError < 0) errors.Add(("game audio", audio.GameError));
            if (_chatAudioPid != 0 && audio.ChatError < 0) errors.Add(("application audio", audio.ChatError));
            if (_musicAudioPid != 0 && audio.MusicError < 0) errors.Add(("music audio", audio.MusicError));
            if (errors.Count == 0) { _lastAudioErrorKey = ""; return; }
            var key = string.Join(";", errors.Select(item => $"{item.Name}:0x{item.Code:X8}"));
            if (!string.Equals(key, _lastAudioErrorKey, StringComparison.Ordinal))
            {
                _lastAudioErrorKey = key;
                foreach (var item in errors) LocalReliabilityLog.Error("audio-" + item.Name.Replace(' ', '-'), item.Code);
                ShowInfoError($"Audio source unavailable: {string.Join(", ", errors.Select(item => item.Name))}. Trying to reconnect.");
            }
            if (DateTime.UtcNow - _lastAudioRecoveryUtc >= TimeSpan.FromSeconds(30)) _ = RecoverAudioAsync();
        }

        private async Task RecoverAudioAsync()
        {
            var now = DateTime.UtcNow;
            while (_audioRecoveryAttempts.Count > 0 && now - _audioRecoveryAttempts.Peek() > TimeSpan.FromMinutes(5))
                _audioRecoveryAttempts.Dequeue();
            if (_audioRecoveryAttempts.Count >= 3 || _capture == IntPtr.Zero || _closing || _suspended) return;
            _audioRecoveryAttempts.Enqueue(now);
            _lastAudioRecoveryUtc = now;
            await _captureGate.WaitAsync();
            try
            {
                if (_capture == IntPtr.Zero || _closing || _suspended) return;
                var capture = _capture;
                var result = await Task.Run(() => NativeCapture.ClipprSetAudioDevices(capture, _audioOutputId, _audioInputId));
                if (result >= 0) result = await Task.Run(() => NativeCapture.ClipprConfigureAudio(capture,
                    _settings.DesktopAudioEnabled ? 1 : 0, _settings.MicrophoneAudioEnabled ? 1 : 0));
                if (result < 0) LocalReliabilityLog.Error("audio-recovery", result);
            }
            catch (Exception ex) { LocalReliabilityLog.Error("audio-recovery", ex.HResult); }
            finally { _captureGate.Release(); }
        }

        private bool CanAttemptCaptureRecovery()
        {
            var now = DateTime.UtcNow;
            if (now - _lastCaptureRecoveryUtc < TimeSpan.FromSeconds(20)) return false;
            while (_captureRecoveryAttempts.Count > 0 && now - _captureRecoveryAttempts.Peek() > TimeSpan.FromMinutes(5))
                _captureRecoveryAttempts.Dequeue();
            if (_captureRecoveryAttempts.Count >= 3)
            {
                ShowInfoError("Capture recovery paused after repeated failures. Select the source again or restart ClipDat.");
                return false;
            }
            _captureRecoveryAttempts.Enqueue(now);
            _lastCaptureRecoveryUtc = now;
            return true;
        }

        private async void OnWindowClosed(object sender, WindowEventArgs e)
        {
            _closing = true;
            if (_voiceCommandService is not null)
            {
                await _voiceCommandService.DisposeAsync();
                _voiceCommandService = null;
            }
            VoiceStatusText.Visibility = Visibility.Collapsed;
            _tray?.Dispose();
            _tray = null;
            _audioMeterTimer.Stop();
            _reliabilityTimer.Stop();
            _notificationWindow?.Close();
            _captureTimer.Stop();
            await _captureGate.WaitAsync();
            try
            {
                if (_capture != IntPtr.Zero)
                {
                    NativeCapture.ClipprDestroyCapture(_capture);
                    _capture = IntPtr.Zero;
                }
            }
            finally
            {
                _captureGate.Release();
            }
        }

        private void ShowPreviewMessage(string message)
        {
            PreviewOverlay.Visibility = Visibility.Visible;
            PreviewMessage.Text = message;
        }

        private void UpdateSettingsPosition()
        {
            var scale = SettingsButton.XamlRoot?.RasterizationScale ?? 1;
            SettingsButton.Margin = new Thickness(0, 0, AppWindow.TitleBar.RightInset / scale, 0);
        }

        private void OnScreenSourceClick(object sender, RoutedEventArgs e)
        {
            _ = ApplyCaptureModeAsync(CaptureSource.Screen);
        }

        private async void OnApplicationSourceClick(object sender, RoutedEventArgs e)
        {
            await ApplyCaptureModeAsync(CaptureSource.Application);
        }

        private async Task ApplyCaptureModeAsync(CaptureSource source)
        {
            _selectedSource = source;
            _settings.CaptureMode = source == CaptureSource.Application ? "Application" : "Screen";
            ++_windowRefreshGeneration;
            var applicationSelected = source == CaptureSource.Application;
            AnimatePill(SourceSelectionIndicator, applicationSelected);
            SourceHoverIndicator.Opacity = 0;
            SourceLabel.Text = applicationSelected ? "Application" : "Display";
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(DisplayPickerButton, applicationSelected ? "Application" : "Display");
            if (applicationSelected)
            {
                ShowPreviewMessage("Choose an application window from the menu");
                await StopCaptureAsync();
                if (_selectedSource == CaptureSource.Application)
                    await RefreshWindowsAsync(resumeSelectedWindow: true);
            }
            else
            {
                RefreshDisplays();
                if (_selectedMonitor != IntPtr.Zero) await BeginScreenCaptureAsync(_selectedMonitor);
                else ShowPreviewMessage("No displays are available");
            }
        }

        private bool _applicationSelected => _selectedSource == CaptureSource.Application;

        private void OnScreenSourcePointerEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            SetSourceHover(applicationSelected: false);
        }

        private void OnApplicationSourcePointerEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            SetSourceHover(applicationSelected: true);
        }

        private void OnSourcePointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            SourceHoverIndicator.Opacity = 0;
        }

        private void SetSourceHover(bool applicationSelected)
        {
            if (applicationSelected == _applicationSelected)
            {
                SourceHoverIndicator.Opacity = 0;
                return;
            }

            AnimatePill(SourceHoverIndicator, applicationSelected);
            SourceHoverIndicator.Opacity = 0.55;
        }

        private void AnimatePill(Microsoft.UI.Xaml.FrameworkElement pill, bool applicationSelected)
        {
            ElementCompositionPreview.SetIsTranslationEnabled(pill, true);
            var visual = ElementCompositionPreview.GetElementVisual(pill);
            var compositor = visual.Compositor;
            var animation = compositor.CreateScalarKeyFrameAnimation();
            animation.InsertKeyFrame(
                1f,
                applicationSelected ? (float)(CaptureSourceGroup.ActualWidth / 2 - 1) : 0f,
                compositor.CreateCubicBezierEasingFunction(new Vector2(0.2f, 0.8f), new Vector2(0.25f, 1f)));
            animation.Duration = TimeSpan.FromMilliseconds(280);
            visual.StartAnimation("Translation.X", animation);
        }
    }
}
