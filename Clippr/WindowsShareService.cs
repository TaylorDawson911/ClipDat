using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Storage;
using WinRT;

namespace Clippr;

public static class WindowsShareService
{
    [ComImport, Guid("3A3DCD6C-3EAB-43DC-BCDE-45671CE800C8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDataTransferManagerInterop
    {
        IntPtr GetForWindow(IntPtr window, [In] ref Guid iid);
        void ShowShareUIForWindow(IntPtr window);
    }

    public static async Task ShowFileAsync(IntPtr window, string path)
    {
        var file = await StorageFile.GetFileFromPathAsync(path);
        var interop = DataTransferManager.As<IDataTransferManagerInterop>();
        var iid = new Guid("A5CAEE9B-8708-49D1-8D36-67D25A8DA00C");
        var manager = MarshalInterface<DataTransferManager>.FromAbi(interop.GetForWindow(window, ref iid));
        TypedEventHandler<DataTransferManager, DataRequestedEventArgs>? handler = null;
        handler = (_, args) =>
        {
            args.Request.Data.Properties.Title = "Share ClipDat clip";
            args.Request.Data.SetStorageItems(new[] { file });
            args.Request.Data.RequestedOperation = DataPackageOperation.Copy;
            manager.DataRequested -= handler;
        };
        manager.DataRequested += handler;
        interop.ShowShareUIForWindow(window);
    }
}
