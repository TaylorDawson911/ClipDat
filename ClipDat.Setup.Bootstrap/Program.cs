using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Windows.Forms;

namespace ClipDat.Setup.Bootstrap;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var directory = Path.Combine(Path.GetTempPath(), "ClipDat-Setup-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(directory);
            using var archive = Assembly.GetExecutingAssembly().GetManifestResourceStream("ClipDat.Setup.Bundle")
                ?? throw new InvalidOperationException("The ClipDat setup files are missing.");
            ZipFile.ExtractToDirectory(archive, directory);
            var wizard = Path.Combine(directory, "ClipDat.Setup.exe");
            if (!File.Exists(wizard)) throw new InvalidDataException("The ClipDat setup wizard is missing.");
            var start = new ProcessStartInfo(wizard) { UseShellExecute = false, WorkingDirectory = directory };
            foreach (var argument in args) start.ArgumentList.Add(argument);
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not open the setup wizard.");
            process.WaitForExit();
            return process.ExitCode;
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "ClipDat Setup", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
