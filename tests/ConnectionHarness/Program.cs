using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using ScreenTask;

// Runs the real application listener without loading/saving user settings,
// changing firewall rules, displaying a window, or using the occupied app port.
class ConnectionHarness
{
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool DeviceIoControl(Microsoft.Win32.SafeHandles.SafeFileHandle file, uint code, IntPtr input, uint inputSize, IntPtr output, uint outputSize, out uint returned, IntPtr overlapped);
    static readonly BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    static void Set(object target, string name, object value) => target.GetType().GetField(name, Private).SetValue(target, value);
    static object Get(object target, string name) => target.GetType().GetField(name, Private).GetValue(target);

    [STAThread]
    static void Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--hdr-profile")
        {
            var type = typeof(frmMain).Assembly.GetType("ScreenTask.HdrDisplayProbe");
            var profile = type.GetMethod("Read", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new object[] { Screen.PrimaryScreen.DeviceName });
            Console.WriteLine(new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(new {
                known = profile.GetType().GetField("Known", Private).GetValue(profile),
                hdr = profile.GetType().GetField("Enabled", Private).GetValue(profile),
                peakNits = profile.GetType().GetField("PeakNits", Private).GetValue(profile)
            }));
            return;
        }
        if (args.Length > 0 && args[0] == "--tone-map-filter")
        {
            var correction = typeof(frmMain).Assembly.GetType("ScreenTask.HdrColorCorrection");
            var filter = correction.GetMethod("ToneMapFilter", BindingFlags.NonPublic | BindingFlags.Static);
            Console.WriteLine(filter.Invoke(null, new object[] { 0.0, 1000, false }));
            return;
        }
        CoreChecks();
        var form = new frmMain();
        var handle = form.Handle;
        if (args.Length > 0 && args[0] == "--updates-ui")
        {
            form.Load -= (EventHandler)Delegate.CreateDelegate(typeof(EventHandler), form, typeof(frmMain).GetMethod("frmMain_Load", Private));
            form.StartPosition = FormStartPosition.Manual;
            form.Location = new System.Drawing.Point(-30000, -30000);
            form.ShowInTaskbar = false;
            form.Show();
            var button = (Button)Get(form, "updateCheck");
            var page = (TabPage)button.Parent.Parent.Parent;
            ((TabControl)page.Parent).SelectedTab = page;
            form.PerformLayout();
            Application.DoEvents();
            if (args.Length > 1)
                using (var bitmap = new System.Drawing.Bitmap(form.Width, form.Height))
                {
                    form.DrawToBitmap(bitmap, new System.Drawing.Rectangle(0, 0, form.Width, form.Height));
                    bitmap.Save(args[1]);
                }
            if (args.Length > 2 && args[2] == "--github")
            {
                SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
                typeof(Button).GetMethod("OnClick", Private).Invoke(button, new object[] { EventArgs.Empty });
                var until = DateTime.UtcNow.AddSeconds(30);
                while (!button.Enabled && DateTime.UtcNow < until) { Application.DoEvents(); Thread.Sleep(50); }
                var status = ((Label)Get(form, "updateStatus")).Text;
                if (!button.Enabled || !status.StartsWith("You are up to date")) throw new Exception("Update UI check failed: " + status);
                Console.WriteLine("PASS: Updates tab checks live GitHub release: " + status);
            }
            form.Dispose();
            Console.WriteLine("PASS: Updates tab renders without loading or changing user settings.");
            return;
        }
        SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
        var port = args.Length > 0 ? int.Parse(args[0]) : 17871;
        Set(form, "_ips", new List<Tuple<string, string>> { Tuple.Create("Test loopback", "127.0.0.1") });
        var ips = (ComboBox)Get(form, "comboIPs"); ips.Items.Add("Test loopback"); ips.SelectedIndex = 0;
        ((NumericUpDown)Get(form, "numPort")).Value = port;
        Set(form, "_currentSettings", new AppSettings { Port = port, ShareSystemAudio = false, OutputWidth = 1280,
            ImageQuality = 75, MaxBitrateMbps = 2, SelectedScreenIndex = 0, FrameRate = 60,
            HdrColorCorrection = args.Length > 2 && args[2] == "--hdr" });
        Set(form, "_requestedFrameRate", 60);
        Set(form, "isWorking", true);
        if (args.Length > 3 && args[2] == "--live")
        {
            Directory.CreateDirectory(args[3]);
            Set(form, "_preferencesPath", Path.Combine(args[3], "preferences.xml"));
            Set(form, "_configureNetwork", new Func<int, Task>(p => Task.CompletedTask));
            ((CheckBox)Get(form, "cbShareSystemAudio")).Checked = false;
            ((ComboBox)Get(form, "hdrMode")).SelectedIndex = 1;
            typeof(frmMain).GetMethod("ReadControlsIntoSettings", Private).Invoke(form, null);
            typeof(frmMain).GetMethod("RememberAppliedSettings", Private).Invoke(form, null);
            Set(form, "_settingsReady", true);
            ReadLiveCommands(form);
        }
        var fileMode = args.Length > 3 && args[2] == "--files";
        if (fileMode)
        {
            Set(form, "_fileTransferOnly", true);
            RegisterFileFixtures(form, args[3]);
        }
        var task = (Task)typeof(frmMain).GetMethod("StartServer", Private).Invoke(form, null);
        task.ContinueWith(t => { if (t.IsFaulted) Console.Error.WriteLine(t.Exception); });
        Console.WriteLine("READY http://localhost:" + port);
        var deadline = DateTime.UtcNow.AddMinutes(10);
        var timer = new System.Windows.Forms.Timer { Interval = 200 };
        timer.Tick += (s, e) => {
            if (DateTime.UtcNow < deadline && !(args.Length > 1 && File.Exists(args[1]))) return;
            Set(form, "isWorking", false);
            typeof(frmMain).GetMethod("StopCaptureProcess", Private).Invoke(form, null);
            ((HttpListener)Get(form, "serv")).Close();
            ((IDisposable)Get(form, "_rtcSignaling")).Dispose();
            ((IDisposable)Get(form, "_audioStream"))?.Dispose();
            ((IDisposable)Get(form, "_fileShares")).Dispose();
            ((IDisposable)Get(form, "_browserFiles")).Dispose();

            Application.ExitThread();
        };
        timer.Start();
        Application.Run();
    }

    static void ReadLiveCommands(frmMain form)
    {
        Task.Run(() => {
            string command;
            while ((command = Console.ReadLine()) != null)
            {
                if (!command.StartsWith("SET ")) continue;
                var arguments = command.Substring(4);
                form.BeginInvoke(new Action(async () => {
                    try
                    {
                        foreach (var pair in arguments.Split(','))
                        {
                            var parts = pair.Split('='); var name = parts[0]; var value = parts[1];
                            if (name == "fps") ((NumericUpDown)Get(form, "numShotEvery")).Value = Int32.Parse(value);
                            else if (name == "width") ((ComboBox)Get(form, "comboResolution")).SelectedIndex = value == "1920" ? 1 : value == "1280" ? 2 : value == "854" ? 3 : 0;
                            else if (name == "bitrate") ((ComboBox)Get(form, "comboBitrate")).SelectedIndex = value == "2" ? 0 : value == "4" ? 1 : value == "8" ? 2 : 5;
                            else if (name == "quality") ((TrackBar)Get(form, "qualitySlider")).Value = Int32.Parse(value);
                            else if (name == "hdr") ((ComboBox)Get(form, "hdrMode")).SelectedIndex = value == "auto" ? 0 : value == "manual" ? 2 : 1;
                            else if (name == "peak") ((NumericUpDown)Get(form, "numHdrPeak")).Value = Int32.Parse(value);
                            else if (name == "exposure") ((NumericUpDown)Get(form, "numHdrExposure")).Value = Decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
                            else if (name == "fileOnly") ((CheckBox)Get(form, "fileOnlyCheck")).Checked = Boolean.Parse(value);
                            else if (name == "audio") ((CheckBox)Get(form, "cbShareSystemAudio")).Checked = Boolean.Parse(value);
                            else if (name == "port") ((NumericUpDown)Get(form, "numPort")).Value = Int32.Parse(value);
                            else if (name == "mouse") ((CheckBox)Get(form, "cbCaptureMouse")).Checked = Boolean.Parse(value);
                        }
                        await (Task)typeof(frmMain).GetMethod("ApplyLiveSettingsAsync", Private).Invoke(form, null);
                        var settings = (AppSettings)Get(form, "_currentSettings");
                        var server = (HttpListener)Get(form, "serv");
                        Console.WriteLine("APPLIED " + new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(new {
                            generation = Get(form, "_serverGeneration"), listener = System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(server),
                            audioGeneration = Get(form, "_audioGeneration"), listening = server.IsListening,
                            port = settings.Port, hdr = settings.HdrColorCorrection, automaticHdr = settings.AutomaticHdr,
                            fileOnly = Get(form, "_fileTransferOnly"), manualVisible = ((ComboBox)Get(form, "hdrMode")).SelectedIndex == 2
                        }));
                    }
                    catch (Exception ex) { Console.WriteLine("COMMAND-ERROR " + ex); }
                }));
            }
        });
    }

    static void RegisterFileFixtures(frmMain form, string directory)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, "sample.bin"), System.Linq.Enumerable.Range(0, 1024 * 1024 + 19).Select(i => (byte)(i % 251)).ToArray());
        File.WriteAllBytes(Path.Combine(directory, "zero.bin"), new byte[0]);
        File.WriteAllText(Path.Combine(directory, "ação & 'friend'.txt"), "Large files, tiny browser memory.", Encoding.UTF8);
        File.WriteAllText(Path.Combine(directory, "changed.txt"), "original");
        File.WriteAllText(Path.Combine(directory, "deleted.txt"), "delete after registration");
        var huge = Path.Combine(directory, "large-6GiB.bin");
        using (var stream = new FileStream(huge, FileMode.Create, FileAccess.ReadWrite, FileShare.Read))
        {
            uint returned;
            if (!DeviceIoControl(stream.SafeFileHandle, 0x000900c4, IntPtr.Zero, 0, IntPtr.Zero, 0, out returned, IntPtr.Zero))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Could not create sparse test file.");
            stream.SetLength(6L * 1024 * 1024 * 1024 + 7);
            foreach (var offset in new[] { 2147483651L, 4294967303L, stream.Length - 16 })
            {
                stream.Position = offset;
                stream.Write(Encoding.ASCII.GetBytes("RANGE-64BIT-PASS"), 0, 16);
            }
        }
        var hub = Get(form, "_fileShares"); var type = hub.GetType();
        var revoke = type.GetMethod("Revoke");
        typeof(frmMain).GetMethod("AddSharedFiles", Private).Invoke(form, new object[] { Directory.GetFiles(directory) });
        var entries = (Array)type.GetMethod("GetFiles").Invoke(hub, null);
        foreach (var file in entries)
        {
            Console.WriteLine("FILE " + file.GetType().GetProperty("Token").GetValue(file) + " " + Convert.ToBase64String(Encoding.UTF8.GetBytes((string)file.GetType().GetProperty("Name").GetValue(file))));
        }
        Task.Run(() => {
            string command;
            while ((command = Console.ReadLine()) != null)
            {
                if (command.StartsWith("REVOKE ", StringComparison.Ordinal)) { revoke.Invoke(hub, new object[] { command.Substring(7) }); Console.WriteLine("REVOKED"); }
                else if (command == "PRIVATE") form.BeginInvoke(new Action(() => { ((AppSettings)Get(form, "_currentSettings")).IsPrivateSession = true; ((TextBox)Get(form, "txtUser")).Text = "test"; ((TextBox)Get(form, "txtPassword")).Text = "transfer"; Console.WriteLine("PRIVATE"); }));
                else if (command == "PUBLIC") form.BeginInvoke(new Action(() => { ((AppSettings)Get(form, "_currentSettings")).IsPrivateSession = false; Console.WriteLine("PUBLIC"); }));
                else if (command == "MEMORY") Console.WriteLine("MEMORY " + System.Diagnostics.Process.GetCurrentProcess().WorkingSet64);
                else if (command == "OFFLINE") { type.GetMethod("CancelDownloads").Invoke(hub, null); Console.WriteLine("OFFLINE"); }
                else if (command == "ONLINE") { type.GetMethod("StartServing").Invoke(hub, null); Console.WriteLine("ONLINE"); }
                else if (command.StartsWith("STATUS ", StringComparison.Ordinal))
                {
                    var files = (Array)type.GetMethod("GetFiles").Invoke(hub, null);
                    var file = files.Cast<object>().FirstOrDefault(item => (string)item.GetType().GetProperty("Token").GetValue(item) == command.Substring(7));
                    if (file != null) Console.WriteLine("STATUS " + file.GetType().GetField("ActiveDownloads").GetValue(file) + " " + file.GetType().GetField("BytesSent").GetValue(file));
                }
            }
        });
    }

    static void CoreChecks()
    {
        var level = typeof(frmMain).GetMethod("GetH264Level", BindingFlags.Static | BindingFlags.NonPublic);
        if ((int)level.Invoke(null, new object[] { 3840, 2160, 1 }) != 51 ||
            (int)level.Invoke(null, new object[] { 3840, 2160, 120 }) != 60)
            throw new Exception("H.264 levels must account for frame size as well as frame rate.");
        var hub = typeof(frmMain).Assembly.GetType("ScreenTask.H264StreamHub");
        var codec = hub.GetMethod("GetCodecString", BindingFlags.Static | BindingFlags.NonPublic);
        var init = new byte[] { 0, 0, 0, 12, 97, 118, 99, 67, 1, 66, 192, 42 };
        if ((string)codec.Invoke(null, new object[] { init }) != "avc1.42C02A") throw new Exception("Actual encoder codec was not read.");
        var readBox = hub.GetMethod("TryReadMp4Box", BindingFlags.Static | BindingFlags.NonPublic);
        var box = new byte[] { 0, 0, 0, 1, 109, 100, 97, 116, 0, 0, 0, 0, 0, 0, 0, 19, 11, 22, 33 };
        var values = new object[] { new MemoryStream(box), null, null };
        if (!(bool)readBox.Invoke(null, values) || !System.Linq.Enumerable.SequenceEqual(box, (byte[])values[1]))
            throw new Exception("Extended MP4 box header/payload was corrupted.");
        Console.WriteLine("PASS: H.264 levels, actual encoder codec, extended MP4 boxes.");
    }
}
