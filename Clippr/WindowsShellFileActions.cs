using System;
using System.IO;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Clippr;

internal static class WindowsShellFileActions
{
    private static readonly Guid IDataObjectId = new("0000010e-0000-0000-C000-000000000046");

    internal static string RequireExistingFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new FileNotFoundException("No saved file is selected.");
        string fullPath;
        try { fullPath = Path.GetFullPath(path); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        { throw new IOException("The saved file path is invalid.", ex); }
        try
        {
            var attributes = File.GetAttributes(fullPath);
            if ((attributes & FileAttributes.Directory) != 0)
                throw new IOException("The saved file path points to a folder.");
            using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length == 0) throw new IOException("The saved file is empty or was not finalized.");
        }
        catch (FileNotFoundException ex) { throw new FileNotFoundException("The saved file no longer exists.", fullPath, ex); }
        catch (DirectoryNotFoundException ex) { throw new FileNotFoundException("The saved file's folder no longer exists.", fullPath, ex); }
        catch (UnauthorizedAccessException) { throw new UnauthorizedAccessException("Access to the saved file was denied."); }
        return fullPath;
    }

    internal static void OpenWithDefaultApplication(string path)
    {
        var result = ShellExecuteW(IntPtr.Zero, "open", path, null, null, 1);
        if (result.ToInt64() <= 32) throw new Win32Exception(result.ToInt32(), "Windows could not open this location.");
    }

    internal static void ShowFileInFolder(string filePath)
    {
        InitializeOle();
        var pidl = IntPtr.Zero;
        try
        {
            Marshal.ThrowExceptionForHR(SHParseDisplayName(filePath, IntPtr.Zero, out pidl, 0, out _));
            Marshal.ThrowExceptionForHR(SHOpenFolderAndSelectItems(pidl, 0, IntPtr.Zero, 0));
        }
        finally
        {
            if (pidl != IntPtr.Zero) Marshal.FreeCoTaskMem(pidl);
            OleUninitialize();
        }
    }

    internal static void CopyFileToClipboard(string filePath)
    {
        InitializeOle();
        var itemPidl = IntPtr.Zero;
        var parentPidl = IntPtr.Zero;
        var dataObject = IntPtr.Zero;
        try
        {
            Marshal.ThrowExceptionForHR(SHParseDisplayName(filePath, IntPtr.Zero, out itemPidl, 0, out _));
            var folder = Path.GetDirectoryName(filePath) ?? throw new IOException("The saved file has no parent folder.");
            Marshal.ThrowExceptionForHR(SHParseDisplayName(folder, IntPtr.Zero, out parentPidl, 0, out _));
            var childPidl = ILFindLastID(itemPidl);
            if (childPidl == IntPtr.Zero) throw new IOException("Windows could not identify the saved file.");
            var children = new[] { childPidl };
            var iid = IDataObjectId;
            Marshal.ThrowExceptionForHR(SHCreateDataObject(parentPidl, 1, children, IntPtr.Zero, ref iid, out dataObject));
            Marshal.ThrowExceptionForHR(OleSetClipboard(dataObject));
            Marshal.ThrowExceptionForHR(OleFlushClipboard());
        }
        finally
        {
            if (dataObject != IntPtr.Zero) Marshal.Release(dataObject);
            if (parentPidl != IntPtr.Zero) Marshal.FreeCoTaskMem(parentPidl);
            if (itemPidl != IntPtr.Zero) Marshal.FreeCoTaskMem(itemPidl);
            OleUninitialize();
        }
    }

    private static void InitializeOle() => Marshal.ThrowExceptionForHR(OleInitialize(IntPtr.Zero));

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHParseDisplayName(string name, IntPtr bindContext, out IntPtr pidl, uint attributesIn, out uint attributesOut);

    [DllImport("shell32.dll", ExactSpelling = true)]
    private static extern int SHOpenFolderAndSelectItems(IntPtr pidlFolder, uint count, IntPtr childPidls, uint flags);

    [DllImport("shell32.dll", ExactSpelling = true)]
    private static extern IntPtr ILFindLastID(IntPtr pidl);

    [DllImport("shell32.dll", ExactSpelling = true)]
    private static extern int SHCreateDataObject(IntPtr folderPidl, uint count, [In] IntPtr[] childPidls,
                                                  IntPtr innerDataObject, ref Guid interfaceId, out IntPtr dataObject);

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int OleSetClipboard(IntPtr dataObject);

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int OleFlushClipboard();

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int OleInitialize(IntPtr reserved);

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern void OleUninitialize();

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern IntPtr ShellExecuteW(IntPtr window, string operation, string file,
                                                string? parameters, string? directory, int showCommand);
}
