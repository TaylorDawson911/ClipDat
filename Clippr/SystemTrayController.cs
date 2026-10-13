using System;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;

namespace Clippr;

internal enum TrayCommand
{
    Open,
    SaveClip,
    TakeScreenshot,
    PauseResumeCapture,
    OpenClipsFolder,
    Settings,
    Exit
}

/// <summary>Owns the notification-area icon and the window message hook used by it.</summary>
internal sealed class SystemTrayController : IDisposable
{
    private const uint WmClose = 0x0010;
    private const uint WmQueryEndSession = 0x0011;
    private const uint WmEndSession = 0x0016;
    private const uint WmDisplayChange = 0x007E;
    private const uint WmPowerBroadcast = 0x0218;
    private const uint WmNull = 0x0000;
    private const uint WmContextMenu = 0x007B;
    private const uint WmLButtonDoubleClick = 0x0203;
    private const uint WmRButtonUp = 0x0205;
    private const uint CallbackMessage = 0x8000 + 0x43;
    private const uint IconId = 1;
    private const uint NimAdd = 0;
    private const uint NimDelete = 2;
    private const uint NimSetVersion = 4;
    private const uint NifMessage = 1;
    private const uint NifIcon = 2;
    private const uint NifTip = 4;
    private const uint NifShowTip = 0x80;
    private const uint NotifyIconVersion4 = 4;
    private const uint ImageIcon = 1;
    private const uint LrLoadFromFile = 0x0010;
    private const int SwHide = 0;
    private const uint MfString = 0;
    private const uint MfGray = 1;
    private const uint MfSeparator = 0x0800;
    private const uint TpmRightButton = 0x0002;
    private const uint TpmReturnCommand = 0x0100;
    private const int SwRestore = 9;

    private const uint CommandOpen = 1001;
    private const uint CommandSaveClip = 1002;
    private const uint CommandScreenshot = 1003;
    private const uint CommandPauseResume = 1004;
    private const uint CommandOpenFolder = 1005;
    private const uint CommandSettings = 1006;
    private const uint CommandExit = 1007;

    private readonly IntPtr _window;
    private readonly DispatcherQueue _dispatcher;
    private readonly Func<bool> _minimizeToTray;
    private readonly Func<bool> _capturePaused;
    private readonly Func<bool> _canSaveClip;
    private readonly Func<bool> _canTakeScreenshot;
    private readonly Action<TrayCommand> _commandHandler;
    private readonly Action<bool> _powerHandler;
    private readonly Action _displayChangeHandler;
    private readonly SubclassProc _subclassProc;
    private readonly IntPtr _icon;
    private bool _disposed;
    private bool _systemEnding;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint Size;
        public IntPtr Window;
        public uint Id;
        public uint Flags;
        public uint CallbackMessage;
        public IntPtr Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public uint State;
        public uint StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint TimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
        public uint InfoFlags;
        public Guid ItemGuid;
        public IntPtr BalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point { public int X; public int Y; }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate IntPtr SubclassProc(IntPtr window, uint message, UIntPtr wParam, IntPtr lParam,
        UIntPtr subclassId, UIntPtr referenceData);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "Shell_NotifyIconW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShellNotifyIcon(uint message, ref NotifyIconData data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "LoadImageW", SetLastError = true)]
    private static extern IntPtr LoadImage(IntPtr instance, string name, uint type, int width, int height, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr icon);

    [DllImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowSubclass(IntPtr window, SubclassProc callback, UIntPtr subclassId, UIntPtr referenceData);

    [DllImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveWindowSubclass(IntPtr window, SubclassProc callback, UIntPtr subclassId);

    [DllImport("comctl32.dll")]
    private static extern IntPtr DefSubclassProc(IntPtr window, uint message, UIntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "CreatePopupMenu", SetLastError = true)]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "AppendMenuW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AppendMenu(IntPtr menu, uint flags, UIntPtr id, string? text);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyMenu(IntPtr menu);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y, IntPtr window, IntPtr parameters);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out Point point);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr window, int command);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr window, uint message, UIntPtr wParam, IntPtr lParam);

    public SystemTrayController(IntPtr window, DispatcherQueue dispatcher, string iconPath,
        Func<bool> minimizeToTray, Func<bool> capturePaused, Func<bool> canSaveClip,
        Func<bool> canTakeScreenshot, Action<TrayCommand> commandHandler,
        Action<bool> powerHandler, Action displayChangeHandler)
    {
        _window = window;
        _dispatcher = dispatcher;
        _minimizeToTray = minimizeToTray;
        _capturePaused = capturePaused;
        _canSaveClip = canSaveClip;
        _canTakeScreenshot = canTakeScreenshot;
        _commandHandler = commandHandler;
        _powerHandler = powerHandler;
        _displayChangeHandler = displayChangeHandler;
        _subclassProc = WindowProc;

        _icon = LoadImage(IntPtr.Zero, iconPath, ImageIcon, 0, 0, LrLoadFromFile);
        if (_icon == IntPtr.Zero)
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Could not load the Clippr tray icon.");

        if (!SetWindowSubclass(_window, _subclassProc, (UIntPtr)IconId, UIntPtr.Zero))
        {
            DestroyIcon(_icon);
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Could not register the Clippr tray window hook.");
        }

        var data = CreateNotifyIconData();
        if (!ShellNotifyIcon(NimAdd, ref data))
        {
            RemoveWindowSubclass(_window, _subclassProc, (UIntPtr)IconId);
            DestroyIcon(_icon);
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Could not add Clippr to the notification area.");
        }
        data.TimeoutOrVersion = NotifyIconVersion4;
        _ = ShellNotifyIcon(NimSetVersion, ref data);
    }

    public static uint ActivationMessage { get; } = RegisterWindowMessage("Clippr.RestoreMainWindow");

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "RegisterWindowMessageW", SetLastError = true)]
    private static extern uint RegisterWindowMessage(string name);

    public void Restore()
    {
        ShowWindow(_window, SwRestore);
        _ = SetForegroundWindow(_window);
    }

    public void Hide() => ShowWindow(_window, SwHide);

    private NotifyIconData CreateNotifyIconData() => new()
    {
        Size = (uint)Marshal.SizeOf<NotifyIconData>(),
        Window = _window,
        Id = IconId,
        Flags = NifMessage | NifIcon | NifTip | NifShowTip,
        CallbackMessage = CallbackMessage,
        Icon = _icon,
        Tip = "ClipDat · instant replay",
        Info = string.Empty,
        InfoTitle = string.Empty
    };

    private IntPtr WindowProc(IntPtr window, uint message, UIntPtr wParam, IntPtr lParam,
        UIntPtr subclassId, UIntPtr referenceData)
    {
        if (message == WmQueryEndSession)
        {
            _systemEnding = true;
            return new IntPtr(1);
        }

        if (message == WmEndSession && wParam != UIntPtr.Zero) _systemEnding = true;

        if (message == WmClose && !_systemEnding && _minimizeToTray())
        {
            ShowWindow(window, SwHide);
            return IntPtr.Zero;
        }

        if (message == WmPowerBroadcast)
        {
            var powerEvent = unchecked((uint)wParam.ToUInt64());
            if (powerEvent == 0x0004) _dispatcher.TryEnqueue(() => _powerHandler(false)); // suspend
            else if (powerEvent is 0x0007 or 0x0012) _dispatcher.TryEnqueue(() => _powerHandler(true)); // resume
            return DefSubclassProc(window, message, wParam, lParam);
        }

        if (message == WmDisplayChange)
        {
            _dispatcher.TryEnqueue(() => _displayChangeHandler());
            return DefSubclassProc(window, message, wParam, lParam);
        }

        if (ActivationMessage != 0 && message == ActivationMessage)
        {
            Restore();
            return IntPtr.Zero;
        }

        if (message == CallbackMessage)
        {
            var notification = unchecked((uint)lParam.ToInt64()) & 0xFFFF;
            if (notification == WmLButtonDoubleClick) Enqueue(TrayCommand.Open);
            else if (notification is WmRButtonUp or WmContextMenu) ShowContextMenu();
            return IntPtr.Zero;
        }

        return DefSubclassProc(window, message, wParam, lParam);
    }

    private void ShowContextMenu()
    {
        var menu = CreatePopupMenu();
        if (menu == IntPtr.Zero) return;
        try
        {
            AppendMenu(menu, MfString, (UIntPtr)CommandOpen, "Open ClipDat");
            AppendMenu(menu, _canSaveClip() ? MfString : MfString | MfGray, (UIntPtr)CommandSaveClip, "Save Clip");
            AppendMenu(menu, _canTakeScreenshot() ? MfString : MfString | MfGray, (UIntPtr)CommandScreenshot, "Take Screenshot");
            AppendMenu(menu, MfSeparator, UIntPtr.Zero, null);
            AppendMenu(menu, MfString, (UIntPtr)CommandPauseResume,
                _capturePaused() ? "Resume Capture" : "Pause Capture");
            AppendMenu(menu, MfString, (UIntPtr)CommandOpenFolder, "Open Clips Folder");
            AppendMenu(menu, MfString, (UIntPtr)CommandSettings, "Settings");
            AppendMenu(menu, MfSeparator, UIntPtr.Zero, null);
            AppendMenu(menu, MfString, (UIntPtr)CommandExit, "Exit ClipDat");

            _ = GetCursorPos(out var point);
            _ = SetForegroundWindow(_window);
            var selected = TrackPopupMenuEx(menu, TpmRightButton | TpmReturnCommand, point.X, point.Y, _window, IntPtr.Zero);
            _ = PostMessage(_window, WmNull, UIntPtr.Zero, IntPtr.Zero);
            DispatchCommand(selected);
        }
        finally { DestroyMenu(menu); }
    }

    private void DispatchCommand(uint id)
    {
        var command = id switch
        {
            CommandOpen => TrayCommand.Open,
            CommandSaveClip => TrayCommand.SaveClip,
            CommandScreenshot => TrayCommand.TakeScreenshot,
            CommandPauseResume => TrayCommand.PauseResumeCapture,
            CommandOpenFolder => TrayCommand.OpenClipsFolder,
            CommandSettings => TrayCommand.Settings,
            CommandExit => TrayCommand.Exit,
            _ => (TrayCommand?)null
        };
        if (command.HasValue) Enqueue(command.Value);
    }

    private void Enqueue(TrayCommand command) => _dispatcher.TryEnqueue(() => _commandHandler(command));

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        var data = CreateNotifyIconData();
        _ = ShellNotifyIcon(NimDelete, ref data);
        _ = RemoveWindowSubclass(_window, _subclassProc, (UIntPtr)IconId);
        _ = DestroyIcon(_icon);
    }
}
