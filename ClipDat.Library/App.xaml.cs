using Microsoft.UI.Xaml;
using System;
using System.IO;

namespace ClipDat.Library;

public partial class App : Application
{
    private Window? _window;

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, args) =>
        {
            try
            {
                var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClipDat Library", "error.log");
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                if (File.Exists(path) && new FileInfo(path).Length > 64 * 1024)
                    File.Move(path, path + ".old", true);
                File.AppendAllText(path, $"{DateTimeOffset.Now:o} {args.Message} {args.Exception}\n");
            }
            catch { }
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new LibraryWindow();
        _window.Activate();
    }
}
