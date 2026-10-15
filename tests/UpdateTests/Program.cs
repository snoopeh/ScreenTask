using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Diagnostics;
using System.Threading;
using ScreenTask;

internal static class Program
{
    private static int checks;
    private static void Assert(bool result, string name) { if (!result) throw new Exception(name); checks++; }
    private static void Reject(Action action, string name)
    {
        try { action(); } catch (InvalidDataException) { checks++; return; }
        throw new Exception("Accepted " + name);
    }
    private static string Json(string url = null, string digest = null, string version = "v1.10.0")
    {
        return "{\"tag_name\":\"" + version + "\",\"assets\":[{\"name\":\"ScreenTask-win-x64.zip\",\"size\":123,\"browser_download_url\":\"" +
            (url ?? ReleaseUpdateClient.Repository + "/releases/download/" + version + "/ScreenTask-win-x64.zip") + "\",\"digest\":\"" + (digest ?? "sha256:" + new string('a', 64)) + "\"}]}";
    }

    private static int Main(string[] args)
    {
        if (File.Exists(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "update-process-fixture")))
        {
            var directory = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');
            if (args.Length == 0) { File.WriteAllText(Path.Combine(directory, "restarted.txt"), "Updated app restarted"); return 0; }
            var eventName = "Local\\ScreenTask-test-" + Guid.NewGuid().ToString("N");
            using (var ready = new EventWaitHandle(false, EventResetMode.ManualReset, eventName))
            using (var helper = Process.Start(new ProcessStartInfo(args[0]) {
                Arguments = "--apply \"" + directory + "\" \"" + args[1] + "\" " + Process.GetCurrentProcess().Id + " " + eventName,
                UseShellExecute = false, CreateNoWindow = true
            }))
            {
                if (!ready.WaitOne(15000)) return 2;
            }
            return 0;
        }
        var root = Path.Combine(Path.GetTempPath(), "ScreenTask-update-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Assert(ReleaseUpdateClient.ParseRelease(Json()).Version == new Version(1, 10, 0, 0), "Semver normalization");
            Assert(ReleaseUpdateClient.ParseRelease(Json(version: "v1.11.2.3")).Version == new Version(1, 11, 2, 3), "Four-part version");
            Reject(() => ReleaseUpdateClient.ParseRelease(Json(url: "https://example.org/payload.zip")), "foreign download");
            Reject(() => ReleaseUpdateClient.ParseRelease(Json(digest: "sha256:bad")), "invalid digest");
            Reject(() => ReleaseUpdateClient.ParseRelease(Json(version: "v1.11.0-beta")), "unstable version");
            Reject(() => ReleaseUpdateClient.ParseRelease(Json().Replace("\"assets\"", "\"prerelease\":true,\"assets\"")), "prerelease");
            Reject(() => ReleaseUpdateClient.ParseRelease("{\"tag_name\":\"v1.10.0\"}"), "missing Windows build");
            foreach (var name in new[] { "../ScreenTask.exe", "/ScreenTask.exe", "C:/ScreenTask.exe", "WebServer/../../appsettings.xml", "appsettings.xml", ".git/config", "WebServer/x.js:payload", "WebServer/CON.js", "WebServer/x.js.", "WebServer/x.js " })
                Reject(() => UpdateInstaller.Relative(name), name);
            var zip = Path.Combine(root, "release.zip");
            using (var package = ZipFile.Open(zip, ZipArchiveMode.Create))
                foreach (var file in new[] { "ScreenTask.exe", "ScreenTask.Updater.exe", "ScreenTask.AudioCaptureHelper.exe", "ffmpeg.exe", "WebServer/index.html" })
                    using (var writer = new StreamWriter(package.CreateEntry(file).Open())) writer.Write("new " + file);
            var stage = Path.Combine(root, "stage");
            UpdateInstaller.Extract(zip, stage);
            Assert(File.ReadAllText(Path.Combine(stage, "ScreenTask.exe")) == "new ScreenTask.exe", "Extracted package");
            var install = Path.Combine(root, "install");
            Directory.CreateDirectory(install);
            File.WriteAllText(Path.Combine(install, "ScreenTask.exe"), "previous");
            File.WriteAllText(Path.Combine(install, "appsettings.xml"), "private preferences");
            File.WriteAllText(Path.Combine(install, "my-friends-file.txt"), "user data");
            try { UpdateInstaller.Apply(stage, install, count => { if (count == 2) throw new IOException("Injected interrupted installation"); }); }
            catch (IOException) { checks++; }
            Assert(File.ReadAllText(Path.Combine(install, "ScreenTask.exe")) == "previous", "Rollback restores old binary");
            Assert(Directory.GetFiles(install, "*.exe").Length == 1, "Rollback removes new files");
            UpdateInstaller.Apply(stage, install);
            Assert(File.ReadAllText(Path.Combine(install, "ScreenTask.exe")) == "new ScreenTask.exe", "Installed update");
            Assert(File.ReadAllText(Path.Combine(install, "appsettings.xml")) == "private preferences", "Settings preserved");
            Assert(File.ReadAllText(Path.Combine(install, "my-friends-file.txt")) == "user data", "Shared files preserved");
            Assert(Directory.GetDirectories(install, ".screentask-backup-*").Length == 2, "Recovery copies retained");
            var helperPath = Path.GetFullPath("ScreenTask.Updater/bin/Release/net48/ScreenTask.Updater.exe");
            if (File.Exists(helperPath))
            {
                var fixture = Path.Combine(root, "process-test");
                Directory.CreateDirectory(fixture);
                File.Copy(System.Reflection.Assembly.GetExecutingAssembly().Location, Path.Combine(fixture, "ScreenTask.exe"));
                File.Copy(System.Reflection.Assembly.GetExecutingAssembly().Location, Path.Combine(stage, "ScreenTask.exe"), true);
                File.WriteAllText(Path.Combine(fixture, "update-process-fixture"), "");
                using (var parent = Process.Start(new ProcessStartInfo(Path.Combine(fixture, "ScreenTask.exe")) {
                    Arguments = "\"" + helperPath + "\" \"" + stage + "\"", UseShellExecute = false, CreateNoWindow = true
                }))
                {
                    Assert(parent.WaitForExit(20000) && parent.ExitCode == 0, "Updater process handshake");
                }
                for (var i = 0; i < 200 && !File.Exists(Path.Combine(fixture, "restarted.txt")); i++) Thread.Sleep(100);
                Assert(File.Exists(Path.Combine(fixture, "restarted.txt")), "Real helper installs and restarts app");
                Assert(!Directory.Exists(stage), "Successful installer removes staged files");
            }
            foreach (var name in new[] { "../escape.exe", "appsettings.xml", "WebServer/NUL.js", "ScreenTask.exe" })
            {
                var malicious = Path.Combine(root, Guid.NewGuid() + ".zip");
                using (var package = ZipFile.Open(malicious, ZipArchiveMode.Create))
                {
                    package.CreateEntry(name);
                    if (name == "ScreenTask.exe") package.CreateEntry(name); // Duplicate destination.
                }
                Reject(() => UpdateInstaller.Extract(malicious, Path.Combine(root, Guid.NewGuid().ToString())), "malicious archive " + name);
            }
            if (args.Length > 0 && args[0] != "--github")
            {
                UpdateInstaller.Extract(Path.GetFullPath(args[0]), Path.Combine(root, "actual-release"));
                Assert(System.Reflection.AssemblyName.GetAssemblyName(Path.Combine(root, "actual-release", "ScreenTask.exe")).Version == new Version(1, 10, 0, 0), "Real packaged application version");
            }
            if (args.Contains("--github"))
            {
                using (var client = new ReleaseUpdateClient())
                {
                    var release = client.CheckAsync(CancellationToken.None).GetAwaiter().GetResult();
                    Assert(release.Version >= new Version(1, 10, 0, 0), "Live GitHub release discovery");
                    var source = Path.Combine(root, "source.zip");
                    client.DownloadAsync(ReleaseUpdateClient.SourceUrl, source, 0, null, null, CancellationToken.None).GetAwaiter().GetResult();
                    using (var package = ZipFile.OpenRead(source)) Assert(package.Entries.Any(e => e.FullName.EndsWith("/ScreenTask/frmMain.Updates.cs")), "Live latest source download");
                    var invalid = Path.Combine(root, "invalid.zip");
                    Reject(() => client.DownloadAsync(ReleaseUpdateClient.SourceUrl, invalid, 0, "sha256:" + new string('0', 64), null, CancellationToken.None).GetAwaiter().GetResult(), "checksum mismatch");
                    Assert(!File.Exists(invalid), "Failed download removed");
                }
            }
            Console.WriteLine("PASS: " + checks + " update checks (release metadata, archive paths, rollback, settings and user files).");
            return 0;
        }
        finally { Directory.Delete(root, true); }
    }
}
