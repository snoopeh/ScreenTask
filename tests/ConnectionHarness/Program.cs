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
            ((IDisposable)Get(form, "_fileShares")).Dispose();
            ((System.Windows.Forms.Timer)Get(form, "fileRefreshTimer")).Dispose();
            Application.ExitThread();
        };
        timer.Start();
        Application.Run();
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
        if (((ListView)Get(form, "fileList")).Items.Count != entries.Length) throw new Exception("Sender UI did not list registered files.");
        var address = (TextBox)Get(form, "filePublicAddress"); address.Text = "http://example.test:19777/";
        var origin = typeof(frmMain).GetMethod("GetFileOrigin", Private);
        if ((string)origin.Invoke(form, null) != "http://example.test:19777") throw new Exception("File link lost its external port.");
        address.Text = "file:///C:/private";
        try { origin.Invoke(form, null); throw new Exception("Unsafe file link scheme accepted."); }
        catch (TargetInvocationException ex) when (ex.InnerException is InvalidOperationException) { }
        address.Text = "";
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
