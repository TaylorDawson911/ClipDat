using System;
using System.Runtime.InteropServices;
using System.Text;

namespace Clippr;

internal static class NativeCapture
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct Display
    {
        public IntPtr Monitor;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string FriendlyName;
        public int Width;
        public int Height;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct Window
    {
        public IntPtr Handle;
        public uint ProcessId;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Title;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string ApplicationName;
        public int Minimized;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Status
    {
        public int State;
        public int ErrorCode;
        public ulong FramesPresented;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct EncodingStatus
    {
        public int State;
        public int ErrorCode;
        public ulong FramesEncoded;
        public ulong FramesDropped;
        public ulong FramesRateLimited;
        public int ErrorStage;
        public ulong LastWriteLatencyUs;
        public ulong AverageWriteLatencyUs;
        public ulong MaxWriteLatencyUs;
        public int ActiveEncoder;
        public ulong AverageQueueLatencyUs;
        public ulong MaxQueueLatencyUs;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ReplayStatus
    {
        public int State;
        public int ErrorCode;
        public ulong Packets;
        public ulong RamBytes;
        public ulong DiskBytes;
        public ulong FramesDropped;
        public ulong SavesCompleted;
        public ulong SavesFailed;
        public int LastSaveError;
        public int SavesActive;
        public int LastSaveShorter;
        public ulong FramesEncoded;
        public ulong LastSaveDurationMs;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct AudioDevice
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Id;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Name;
        public int IsDefault;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct AudioStatus
    {
        public int DesktopRunning, MicrophoneRunning, GameRunning, ChatRunning;
        public int DesktopError, MicrophoneError, GameError, ChatError;
        public int MusicRunning, MusicError;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct AudioLevels
    {
        public float Desktop, Microphone, Game, Chat, Music;
    }

    [DllImport("ClipprCapture.dll", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int ClipprCreateCapture(IntPtr panel, int width, int height, out IntPtr capture);

    [DllImport("ClipprCapture.dll", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void ClipprDestroyCapture(IntPtr capture);

    [DllImport("ClipprCapture.dll", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int ClipprEnumerateDisplays([Out] Display[] displays, int capacity, out int count);

    [DllImport("ClipprCapture.dll", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int ClipprEnumerateWindows([Out] Window[] windows, int capacity, out int count);

    [DllImport("ClipprCapture.dll", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int ClipprCopyWindowIcon(IntPtr window, [Out] byte[] pixels, int capacity);

    [DllImport("ClipprCapture.dll", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int ClipprStartCapture(IntPtr capture, IntPtr monitor);

    [DllImport("ClipprCapture.dll", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int ClipprSwitchWindow(IntPtr capture, IntPtr window);

    [DllImport("ClipprCapture.dll", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void ClipprStopCapture(IntPtr capture);

    [DllImport("ClipprCapture.dll", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int ClipprSwitchDisplay(IntPtr capture, IntPtr monitor);

    [DllImport("ClipprCapture.dll", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int ClipprResizePreview(IntPtr capture, int width, int height);

    [DllImport("ClipprCapture.dll", CallingConvention = CallingConvention.Cdecl)]
    internal static extern Status ClipprGetCaptureStatus(IntPtr capture);

    [DllImport("ClipprCapture.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    internal static extern int ClipprStartEncoding(IntPtr capture, string path, uint fps, uint bitrate,
                                                    uint width, uint height);

    [DllImport("ClipprCapture.dll", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int ClipprStopEncoding(IntPtr capture);

    [DllImport("ClipprCapture.dll", CallingConvention = CallingConvention.Cdecl)]
    internal static extern EncodingStatus ClipprGetEncodingStatus(IntPtr capture);

    [DllImport("ClipprCapture.dll", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int ClipprConfigureReplay(IntPtr capture, uint durationSeconds,
        ulong ramBytes, ulong diskBytes, ulong minimumFreeDiskBytes);

    [DllImport("ClipprCapture.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    internal static extern int ClipprConfigureReplayOutput(IntPtr capture, string folder, string extension);

    [DllImport("ClipprCapture.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    internal static extern int ClipprSaveReplay(IntPtr capture, uint durationSeconds, string path);

    [DllImport("ClipprCapture.dll", CallingConvention = CallingConvention.Cdecl)]
    internal static extern ReplayStatus ClipprGetReplayStatus(IntPtr capture);

    [DllImport("ClipprCapture.dll", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int ClipprSetReplayEnabled(IntPtr capture, int enabled);

    [DllImport("ClipprCapture.dll", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int ClipprEnumerateAudioDevices(int renderDevices, [Out] AudioDevice[] devices,
                                                            int capacity, out int count);

    [DllImport("ClipprCapture.dll", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int ClipprConfigureAudio(IntPtr capture, int desktopEnabled, int microphoneEnabled);

    [DllImport("ClipprCapture.dll", CallingConvention = CallingConvention.Cdecl)]
    internal static extern AudioStatus ClipprGetAudioStatus(IntPtr capture);

    [DllImport("ClipprCapture.dll", CallingConvention = CallingConvention.Cdecl)]
    internal static extern AudioLevels ClipprTakeAudioLevels(IntPtr capture);

    [DllImport("ClipprCapture.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    internal static extern int ClipprSetAudioDevices(IntPtr capture, string renderId, string microphoneId);

    [DllImport("ClipprCapture.dll", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int ClipprConfigureProcessAudio(IntPtr capture, uint gameProcessId, uint chatProcessId);

    [DllImport("ClipprCapture.dll", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int ClipprConfigureMusicAudio(IntPtr capture, uint musicProcessId);

    [DllImport("ClipprCapture.dll", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int ClipprSetAudioGain(IntPtr capture, int streamKind, float gain);

    [DllImport("ClipprCapture.dll", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int ClipprConfigureHotkey(IntPtr capture, uint modifiers, uint virtualKey);

    [DllImport("ClipprCapture.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    internal static extern int ClipprSaveScreenshot(IntPtr capture, string path, int jpeg, uint quality);

    [DllImport("ClipprCapture.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    internal static extern int ClipprConfigureScreenshot(IntPtr capture, string folder, int jpeg, uint quality,
                                                          uint modifiers, uint virtualKey);

    [DllImport("ClipprCapture.dll", CallingConvention = CallingConvention.Cdecl)]
    internal static extern ulong ClipprGetScreenshotCount(IntPtr capture);

    [DllImport("ClipprCapture.dll", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int ClipprGetScreenshotError(IntPtr capture);

    [DllImport("ClipprCapture.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    internal static extern int ClipprGetLastScreenshotPath(IntPtr capture, StringBuilder path, int capacity);

    [DllImport("ClipprCapture.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    internal static extern int ClipprGetLastReplayPath(IntPtr capture, StringBuilder path, int capacity);

    [DllImport("ClipprCapture.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    internal static extern int ClipprLoadSettingsJson(out IntPtr json, out int state);

    [DllImport("ClipprCapture.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    internal static extern int ClipprSaveSettingsJson(string json);
}
