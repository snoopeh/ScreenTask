using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using ScreenTask;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var install = "";
        var stopped = false;
        try
        {
            if (args.Length != 5 || args[0] != "--apply") throw new ArgumentException("Run updates from the ScreenTask Updates tab.");
            install = Path.GetFullPath(args[1]);
            var stage = Path.GetFullPath(args[2]);
            var executable = Path.Combine(install, "ScreenTask.exe");
            UpdateInstaller.NoLinks(install);
            using (var parent = Process.GetProcessById(Int32.Parse(args[3])))
            {
                if (!String.Equals(parent.MainModule.FileName, executable, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("The application requesting this update does not match the installation.");
                using (var ready = EventWaitHandle.OpenExisting(args[4])) ready.Set();
                if (!parent.WaitForExit(60000)) throw new IOException("ScreenTask did not close. Close it and try the update again.");
            }
            stopped = true;
            UpdateInstaller.Apply(stage, install);
            Start(executable, install);
            try
            {
                // Only these known staging paths are removed; the running helper stays until exit.
                Directory.Delete(stage, true);
                File.Delete(Path.Combine(Path.GetDirectoryName(stage), "ScreenTask-win-x64.zip"));
            }
            catch { }
            return 0;
        }
        catch (Exception ex)
        {
            var log = Path.Combine(Path.GetTempPath(), "ScreenTask-update-error-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss") + ".txt");
            try { File.WriteAllText(log, ex.ToString()); } catch { }
            MessageBox.Show("Update could not be completed.\r\n" + ex.Message + "\r\n\r\nDetails: " + log, "ScreenTask update", MessageBoxButtons.OK, MessageBoxIcon.Error);
            if (stopped && File.Exists(Path.Combine(install, "ScreenTask.exe")))
                try { Start(Path.Combine(install, "ScreenTask.exe"), install); } catch { }
            return 1;
        }
    }

    private static void Start(string executable, string directory)
    {
        Process.Start(new ProcessStartInfo(executable) { WorkingDirectory = directory, UseShellExecute = true });
    }
}
