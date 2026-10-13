using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml.Shapes;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
using Windows.Foundation;
using Windows.Foundation.Collections;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace Clippr
{
    /// <summary>
    /// Provides application-specific behavior to supplement the default Application class.
    /// </summary>
    public partial class App : Application
    {
        private Window? _window;
        private static Mutex? _instanceMutex;

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate bool EnumWindowsProc(IntPtr window, IntPtr state);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr state);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool PostMessage(IntPtr window, uint message, UIntPtr wParam, IntPtr lParam);

        /// <summary>
        /// Initializes the singleton application object.  This is the first line of authored code
        /// executed, and as such is the logical equivalent of main() or WinMain().
        /// </summary>
        public App()
        {
            InitializeComponent();
        }

        /// <summary>
        /// Invoked when the application is launched.
        /// </summary>
        /// <param name="args">Details about the launch request and process.</param>
        protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
        {
            _instanceMutex = new Mutex(initiallyOwned: true, @"Local\Clippr.SingleInstance", out var createdNew);
            if (!createdNew)
            {
                RestoreExistingInstance();
                Exit();
                return;
            }

            _window = new MainWindow();
            _window.Activate();
        }

        private static void RestoreExistingInstance()
        {
            var message = SystemTrayController.ActivationMessage;
            var matchingProcessIds = Process.GetProcessesByName("Clippr")
                .Where(process => process.Id != Environment.ProcessId)
                .Select(process =>
                {
                    using (process) return (uint)process.Id;
                })
                .ToHashSet();
            if (matchingProcessIds.Count == 0 || message == 0) return;

            EnumWindows((window, state) =>
            {
                _ = GetWindowThreadProcessId(window, out var processId);
                if (!matchingProcessIds.Contains(processId)) return true;
                _ = PostMessage(window, message, UIntPtr.Zero, IntPtr.Zero);
                return false;
            }, IntPtr.Zero);
        }
    }
}
