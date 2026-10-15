using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Data;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Xml.Serialization;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;

namespace ScreenTask
{
    public partial class frmMain : Form
    {
        [DllImport("winmm.dll")]
        private static extern uint timeBeginPeriod(uint period);
        [DllImport("winmm.dll")]
        private static extern uint timeEndPeriod(uint period);
        [DllImport("user32.dll")]
        private static extern IntPtr GetDC(IntPtr window);
        [DllImport("user32.dll")]
        private static extern int ReleaseDC(IntPtr window, IntPtr deviceContext);
        [DllImport("gdi32.dll")]
        private static extern int SetStretchBltMode(IntPtr deviceContext, int mode);
        [DllImport("gdi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool StretchBlt(IntPtr destination, int x, int y, int width, int height,
            IntPtr source, int sourceX, int sourceY, int sourceWidth, int sourceHeight, uint rasterOperation);
        private volatile bool isWorking;

        private object locker = new object();
        private ReaderWriterLock rwl = new ReaderWriterLock();
        private List<Tuple<string, string>> _ips;
        HttpListener serv;
        private Version currentVersion;
        private AppSettings _currentSettings = new AppSettings();
        private byte[] _latestFrame;
        private long _frameSequence;
        private readonly object _frameLock = new object();
        private TaskCompletionSource<bool> _frameSignal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        private double _captureFps;
        private double _sourceFps;
        private double _sentFps;
        private double _sentMbps;
        private double _avgFrameKb;
        private double _sendWriteMs;
        private double _bitrateWaitPct;
        private long _skippedFrames;
        private double _copyMs;
        private double _jpegMs;
        private volatile int _requestedFrameRate = 60;
        private readonly H264StreamHub _h264Stream = new H264StreamHub();
        private int _legacyCaptureStarted;
        private int _serverGeneration;
        private int _jpegViewers;
        private int _jpegCaptureStarted;
        private string _captureCodec = "MJPEG";
        private AudioStreamServer _audioStream;
        private readonly RtcSignalingHub _rtcSignaling = new RtcSignalingHub();
        private readonly ConcurrentDictionary<Guid, WebSocket> _chatClients = new ConcurrentDictionary<Guid, WebSocket>();
        private readonly object _chatLock = new object();
        private readonly SemaphoreSlim _chatSendLock = new SemaphoreSlim(1, 1);
        private readonly Queue<string> _chatHistory = new Queue<string>();
        private int _activeViewers;
        private Process _captureProcess;
        private string _captureBackend = "GDI";
        private CheckBox cbHdrCorrection;
        private NumericUpDown numHdrExposure;
        private NumericUpDown numHdrPeak;
        private Process _jpegCompatibilityProcess;
        private ComboBox comboResolution;
        private ComboBox comboBitrate;
        private ComboBox comboAudioDevice;
        private ComboBox comboAudioMode;
        private ComboBox comboAudioApplication;
        private Label audioModeHint;
        private Label lblQualityValue;
        private Button btnDirectHost;
        public frmMain()
        {
            InitializeComponent();
            _rtcSignaling.Log = message => Log("Direct signaling: " + message);
            currentVersion = Assembly.GetExecutingAssembly().GetName().Version;
            serv = new HttpListener();
            serv.IgnoreWriteExceptions = false; // Stop disk reads when a downloader disconnects.
            _h264Stream.ClientSample = (fps, mbps, frameKb, writeMs) =>
            {
                Volatile.Write(ref _sentFps, fps);
                Volatile.Write(ref _sentMbps, mbps);
                Volatile.Write(ref _avgFrameKb, frameKb);
                Volatile.Write(ref _sendWriteMs, writeMs);
                Volatile.Write(ref _bitrateWaitPct, 0);
            };

            foreach (var screen in Screen.AllScreens)
            {
                comboScreens.Items.Add(screen.DeviceName.Replace("\\", "").Replace(".", ""));
            }
            comboScreens.SelectedIndex = 0;
            this.Text = $"Screen Task v{currentVersion.Major}.{currentVersion.Minor}";
            BuildStreamControls();
        }

        private void BuildStreamControls()
        {
            comboResolution = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
            comboResolution.Items.AddRange(new object[] { "Original", "1920 px", "1280 px", "854 px" });
            comboResolution.SelectedIndex = 2;
            comboBitrate = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
            comboBitrate.Items.AddRange(new object[] { "2 Mbps (recommended for remote viewers)", "4 Mbps", "8 Mbps", "12 Mbps", "20 Mbps", "Unlimited" });
            comboBitrate.SelectedIndex = 0;
            comboAudioDevice = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
            comboAudioDevice.Items.Add("Default output");
            try
            {
                var enumerator = new MMDeviceEnumerator();
                foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
                    comboAudioDevice.Items.Add(new AudioDeviceChoice(device.ID, device.FriendlyName));
            }
            catch { }
            comboAudioDevice.SelectedIndex = 0;
            comboAudioMode = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
            comboAudioMode.Items.AddRange(new object[] { "Playback device (all audio)", "Only selected application", "All audio except selected application" });
            comboAudioMode.SelectedIndex = 0;
            comboAudioApplication = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
            comboAudioApplication.DropDown += (sender, args) => RefreshAudioApplications();
            RefreshAudioApplications();
            lblQualityValue = new Label { Text = qualitySlider.Value + "%", AutoSize = true };

            // All controls are reparented into layouts that size themselves at the current DPI.
            Controls.Clear();
            AutoScaleMode = AutoScaleMode.Dpi;
            AutoScroll = false;
            FormBorderStyle = FormBorderStyle.Sizable;
            MaximizeBox = true;
            MinimumSize = new Size(620, 570);
            ClientSize = new Size(760, 690);
            BackColor = Color.FromArgb(31, 38, 48);
            Font = new Font("Segoe UI", 10F);

            var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(14), ColumnCount = 1, RowCount = 5 };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 90));
            Controls.Add(root);

            var heading = new Label { Text = "ScreenTask", ForeColor = Color.White, AutoSize = true,
                Font = new Font("Segoe UI", 18F, FontStyle.Bold), Margin = new Padding(4, 2, 4, 12) };
            root.Controls.Add(heading, 0, 0);

            var tabs = new TabControl { Dock = DockStyle.Fill, MinimumSize = new Size(0, 260) };
            root.Controls.Add(tabs, 0, 1);
            var networkTab = CreateSettingsTab(tabs, "Network");
            var videoTab = CreateSettingsTab(tabs, "Video");
            var audioTab = CreateSettingsTab(tabs, "Audio");
            BuildFileTab(tabs);
            BuildUpdatesTab(tabs);
            var privacyTab = CreateSettingsTab(tabs, "Privacy");

            AddSettingsRow(networkTab, "IP address", comboIPs);
            AddSettingsRow(networkTab, "Port", numPort);
            AddSettingsCheck(networkTab, cbAllowPublicAccess);
            AddSettingsCheck(networkTab, cbAutoStart);
            AddSettingsCheck(networkTab, cbMiniStart);
            var directHint = new Label { Text = "Direct WebRTC needs no ZeroTier or shared LAN. For remote friends, expose the viewer/signaling page (usually a TCP port forward); video uses a separate direct UDP peer path and fails if NAT/firewall rules block it.",
                AutoSize = true, MaximumSize = new Size(520, 0), ForeColor = Color.FromArgb(75, 85, 100), Margin = new Padding(4, 8, 4, 12) };
            networkTab.Controls.Add(directHint, 0, networkTab.RowCount);
            networkTab.SetColumnSpan(directHint, 2);

            numShotEvery.Maximum = 240;
            numShotEvery.ValueChanged += (sender, args) =>
            {
                _requestedFrameRate = (int)numShotEvery.Value;
                _currentSettings.FrameRate = _requestedFrameRate;
            };
            comboResolution.SelectedIndexChanged += (sender, args) => _currentSettings.OutputWidth = GetSelectedWidth();
            comboBitrate.SelectedIndexChanged += (sender, args) => _currentSettings.MaxBitrateMbps = GetSelectedBitrate();
            AddSettingsRow(videoTab, "Screen", comboScreens);
            AddSettingsRow(videoTab, "Target FPS (1–240)", numShotEvery);
            AddSettingsRow(videoTab, "Output width", comboResolution);
            var qualityRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, AutoSize = true };
            qualityRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            qualityRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            qualitySlider.Dock = DockStyle.Fill;
            qualitySlider.AutoSize = false;
            qualitySlider.Height = 32;
            qualityRow.Controls.Add(qualitySlider, 0, 0);
            qualityRow.Controls.Add(lblQualityValue, 1, 0);
            AddSettingsRow(videoTab, "Video quality", qualityRow);
            AddSettingsRow(videoTab, "Bitrate cap", comboBitrate);
            AddSettingsCheck(videoTab, cbCaptureMouse);
            cbHdrCorrection = new CheckBox { Text = "Correct HDR colors for SDR viewers", AutoSize = true };
            numHdrExposure = new NumericUpDown { Minimum = -2, Maximum = 2, DecimalPlaces = 1, Increment = 0.1M };
            numHdrPeak = new NumericUpDown { Minimum = 400, Maximum = 4000, Increment = 100, Value = 1000 };
            AddSettingsCheck(videoTab, cbHdrCorrection);
            AddSettingsRow(videoTab, "HDR exposure (EV)", numHdrExposure);
            AddSettingsRow(videoTab, "HDR peak (nits)", numHdrPeak);
            var hdrHint = new Label { Text = "Enable for an HDR screen with washed-out or clipped colors. Adjust exposure if too dark. Output is SDR. Direct sharing can use the corrected monitor on its host page. HDR changes restart capture.",
                AutoSize = true, MaximumSize = new Size(500, 0), ForeColor = Color.FromArgb(75, 85, 100) };
            videoTab.Controls.Add(hdrHint, 0, videoTab.RowCount); videoTab.SetColumnSpan(hdrHint, 2);
            cbHdrCorrection.CheckedChanged += (sender, args) => UpdateHdrSettings();
            numHdrExposure.ValueChanged += (sender, args) => UpdateHdrSettings();
            numHdrPeak.ValueChanged += (sender, args) => UpdateHdrSettings();
            UpdateHdrSettings();

            cbShareSystemAudio.Text = "Share playback audio";
            AddSettingsCheck(audioTab, cbShareSystemAudio);
            AddSettingsRow(audioTab, "Audio source", comboAudioMode);
            AddSettingsRow(audioTab, "Playback device", comboAudioDevice);
            var applicationRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, AutoSize = true };
            applicationRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            applicationRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            comboAudioApplication.Dock = DockStyle.Fill;
            applicationRow.Controls.Add(comboAudioApplication, 0, 0);
            var refreshAudio = new Button { Text = "Refresh", AutoSize = true };
            refreshAudio.Click += (sender, args) => RefreshAudioApplications();
            applicationRow.Controls.Add(refreshAudio, 1, 0);
            AddSettingsRow(audioTab, "Application", applicationRow);
            audioModeHint = new Label { Text = "Audio from the selected playback device is available after viewers click Enable audio.",
                AutoSize = true, MaximumSize = new Size(500, 0), ForeColor = Color.FromArgb(75, 85, 100) };
            audioTab.Controls.Add(audioModeHint, 0, audioTab.RowCount);
            audioTab.SetColumnSpan(audioModeHint, 2);
            comboAudioMode.SelectedIndexChanged += (sender, args) => UpdateAudioModeControls();
            cbShareSystemAudio.CheckedChanged += (sender, args) => UpdateAudioModeControls();
            UpdateAudioModeControls();

            AddSettingsCheck(privacyTab, cbPrivate);
            AddSettingsRow(privacyTab, "Username", txtUser);
            AddSettingsRow(privacyTab, "Password", txtPassword);

            var urlRow = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 4, Margin = new Padding(0, 10, 0, 6) };
            urlRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            urlRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            urlRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            urlRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            var urlLabel = new Label { Text = "Viewer URL", AutoSize = true, ForeColor = Color.White, Anchor = AnchorStyles.Left };
            urlRow.Controls.Add(urlLabel, 0, 0);
            txtURL.Dock = DockStyle.Fill;
            urlRow.Controls.Add(txtURL, 1, 0);
            btnLaunch.Text = "Open viewer";
            btnLaunch.AutoSize = true;
            urlRow.Controls.Add(btnLaunch, 2, 0);
            btnDirectHost = new Button { Text = "Start direct share", AutoSize = true, Enabled = false,
                BackColor = Color.FromArgb(38, 132, 102), ForeColor = Color.White, FlatStyle = FlatStyle.Flat,
                Margin = new Padding(6, 3, 0, 3) };
            btnDirectHost.Click += (sender, args) =>
            {
                var hostUrl = "http://localhost:" + numPort.Value + "/host";
                try { Process.Start(hostUrl); }
                catch (Exception ex) { Log("Could not open direct share host: " + ex.Message); }
            };
            urlRow.Controls.Add(btnDirectHost, 3, 0);
            root.Controls.Add(urlRow, 0, 2);

            btnStartServer.Dock = DockStyle.Fill;
            btnStartServer.Height = 42;
            btnStartServer.BackColor = Color.FromArgb(36, 102, 186);
            btnStartServer.ForeColor = Color.White;
            btnStartServer.Font = new Font("Segoe UI", 11F, FontStyle.Bold);
            root.Controls.Add(btnStartServer, 0, 3);
            txtLog.Dock = DockStyle.Fill;
            txtLog.BackColor = Color.White;
            txtLog.ForeColor = Color.FromArgb(30, 35, 42);
            txtLog.Margin = new Padding(0, 10, 0, 0);
            root.Controls.Add(txtLog, 0, 4);
        }

        private void UpdateHdrSettings()
        {
            _currentSettings.HdrColorCorrection = cbHdrCorrection.Checked;
            _currentSettings.HdrExposure = (double)numHdrExposure.Value;
            _currentSettings.HdrPeakNits = (int)numHdrPeak.Value;
            numHdrExposure.Enabled = numHdrPeak.Enabled = cbHdrCorrection.Checked;
            if (isWorking) StopCaptureProcess();
        }

        private TableLayoutPanel CreateSettingsTab(TabControl tabs, string title)
        {
            var page = new TabPage(title) { BackColor = Color.FromArgb(248, 250, 253), AutoScroll = true, Padding = new Padding(16) };
            tabs.TabPages.Add(page);
            var layout = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, RowCount = 0 };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            page.Controls.Add(layout);
            return layout;
        }

        private void AddSettingsRow(TableLayoutPanel table, string caption, Control control)
        {
            var row = table.RowCount++;
            table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var label = new Label { Text = caption, AutoSize = true, ForeColor = Color.FromArgb(35, 45, 60),
                Font = Font, Anchor = AnchorStyles.Left, Margin = new Padding(0, 9, 12, 9) };
            control.Font = Font;
            control.ForeColor = Color.FromArgb(35, 45, 60);
            control.Margin = new Padding(4, 7, 4, 7);
            if (control is ComboBox || control is TextBox) control.Dock = DockStyle.Fill;
            else control.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            table.Controls.Add(label, 0, row);
            table.Controls.Add(control, 1, row);
        }

        private void AddSettingsCheck(TableLayoutPanel table, CheckBox check)
        {
            var row = table.RowCount++;
            table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            check.Font = Font;
            check.ForeColor = Color.FromArgb(35, 45, 60);
            check.BackColor = Color.Transparent;
            check.Margin = new Padding(0, 10, 0, 10);
            check.AutoSize = true;
            table.Controls.Add(check, 0, row);
            table.SetColumnSpan(check, 2);
        }

        private sealed class AudioDeviceChoice
        {
            public string Id { get; private set; }
            private readonly string _name;
            public AudioDeviceChoice(string id, string name) { Id = id; _name = name; }
            public override string ToString() { return _name; }
        }

        private sealed class AudioProcessChoice
        {
            public int Id { get; private set; }
            public string Name { get; private set; }
            private readonly string _title;
            public AudioProcessChoice(int id, string name, string title) { Id = id; Name = name; _title = title; }
            public override string ToString() { return Name + " (PID " + Id + ") — " + _title; }
        }

        private void RefreshAudioApplications()
        {
            var selected = comboAudioApplication.SelectedItem as AudioProcessChoice;
            var preferredId = selected?.Id ?? _currentSettings.AudioProcessId;
            var preferredName = selected?.Name ?? _currentSettings.AudioProcessName;
            var choices = new List<AudioProcessChoice>();
            var ownProcessId = Process.GetCurrentProcess().Id;
            foreach (var process in Process.GetProcesses())
            {
                try
                {
                    if (process.Id != ownProcessId && !String.IsNullOrWhiteSpace(process.MainWindowTitle))
                        choices.Add(new AudioProcessChoice(process.Id, process.ProcessName, process.MainWindowTitle));
                }
                catch (Exception ex) when (ex is InvalidOperationException || ex is System.ComponentModel.Win32Exception) { }
                finally { process.Dispose(); }
            }
            choices.Sort((a, b) => String.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            comboAudioApplication.BeginUpdate();
            comboAudioApplication.Items.Clear();
            foreach (var choice in choices) comboAudioApplication.Items.Add(choice);
            comboAudioApplication.SelectedItem = choices.FirstOrDefault(choice => choice.Id == preferredId)
                ?? choices.FirstOrDefault(choice => String.Equals(choice.Name, preferredName, StringComparison.OrdinalIgnoreCase));
            comboAudioApplication.EndUpdate();
        }

        private void UpdateAudioModeControls()
        {
            if (comboAudioMode == null || audioModeHint == null) return;
            var enabled = cbShareSystemAudio.Checked;
            var processMode = comboAudioMode.SelectedIndex != 0;
            comboAudioMode.Enabled = enabled;
            comboAudioDevice.Enabled = enabled && !processMode;
            comboAudioApplication.Enabled = enabled && processMode;
            audioModeHint.Text = !enabled ? "Audio sharing is off." : !processMode
                ? "The selected playback device shares every application on that device, including calls."
                : comboAudioMode.SelectedIndex == 1
                    ? "Only the selected application's process tree is shared. Restart the stream after changing applications."
                    : "The selected application's process tree is left out of the shared audio. Choose your call app to avoid echo.";
        }

        private void SaveAudioSourceSettings()
        {
            _currentSettings.AudioMode = comboAudioMode.SelectedIndex == 1 ? "include" :
                comboAudioMode.SelectedIndex == 2 ? "exclude" : "device";
            var application = comboAudioApplication.SelectedItem as AudioProcessChoice;
            _currentSettings.AudioProcessId = application?.Id ?? 0;
            _currentSettings.AudioProcessName = application?.Name;
        }

        private void ValidateAudioSourceSettings()
        {
            if (!_currentSettings.ShareSystemAudio || _currentSettings.AudioMode == "device") return;
            if (_currentSettings.AudioProcessId <= 0)
                throw new InvalidOperationException("Choose an application on the Audio tab before starting the server.");
            try
            {
                using (var process = Process.GetProcessById(_currentSettings.AudioProcessId))
                {
                    if (process.HasExited || !String.Equals(process.ProcessName, _currentSettings.AudioProcessName, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("The selected audio application has exited. Refresh the application list.");
                }
            }
            catch (ArgumentException)
            {
                throw new InvalidOperationException("The selected audio application has exited. Refresh the application list.");
            }
        }

        private async void btnStartServer_Click(object sender, EventArgs e)
        {

            if (btnStartServer.Tag.ToString() != "start")
            {
                btnStartServer.Tag = "start";
                btnStartServer.Text = "Start Server";
                if (btnDirectHost != null) btnDirectHost.Enabled = false;
                isWorking = false;
                Interlocked.Increment(ref _serverGeneration);
                Interlocked.Exchange(ref _legacyCaptureStarted, 0);
                Interlocked.Exchange(ref _jpegCaptureStarted, 0);
                StopCaptureProcess();
                _rtcSignaling.Reset();
                _fileShares.CancelDownloads();
                _h264Stream.BeginStream();
                _h264Stream.FailStream("The ScreenTask server is stopped.");
                var audioStream = _audioStream;
                _audioStream = null;
                audioStream?.Dispose();
                if (serv.IsListening)
                {
                    serv.Stop();
                    serv.Close();
                }
                Log("Server Stopped.");
                appNotify.ShowBalloonTip(1_000, "ScreenTask", "Server Stopped.", ToolTipIcon.Info);

                return;
            }

            try
            {

                _fileTransferOnly = _startFileOnlyRequested;
                _startFileOnlyRequested = false;
                serv = new HttpListener();
                serv.IgnoreWriteExceptions = false;
                Interlocked.Increment(ref _serverGeneration);
                _latestFrame = null;
                isWorking = true;
                _currentSettings.FrameRate = (int)numShotEvery.Value;
                _requestedFrameRate = _currentSettings.FrameRate;
                _currentSettings.ShareSystemAudio = cbShareSystemAudio.Checked;
                _currentSettings.ImageQuality = qualitySlider.Value;
                _currentSettings.OutputWidth = GetSelectedWidth();
                _currentSettings.MaxBitrateMbps = GetSelectedBitrate();
                _currentSettings.AudioDeviceId = (comboAudioDevice.SelectedItem as AudioDeviceChoice)?.Id;
                SaveAudioSourceSettings();
                if (!_fileTransferOnly) ValidateAudioSourceSettings();
                _currentSettings.IsShowMouseEnabled = cbCaptureMouse.Checked;
                _currentSettings.SelectedScreenIndex = comboScreens.SelectedIndex;
                _currentSettings.Port = (int)numPort.Value;
                _currentSettings.IP = _ips.ElementAt(comboIPs.SelectedIndex).Item2;
                Volatile.Write(ref _captureFps, 0);
                Volatile.Write(ref _sentFps, 0);
                Volatile.Write(ref _sentMbps, 0);
                Volatile.Write(ref _avgFrameKb, 0);
                Volatile.Write(ref _sendWriteMs, 0);
                Volatile.Write(ref _bitrateWaitPct, 0);
                Volatile.Write(ref _skippedFrames, 0);
                Log("Starting Server, Please Wait...");
                await AddFirewallRule((int)numPort.Value);
                await StartServer();

            }
            catch (ObjectDisposedException)
            {
                serv = new HttpListener();
                serv.IgnoreWriteExceptions = false;
            }
            catch (HttpListenerException httpEx)
            {
                isWorking = false;
                btnStartServer.Tag = "start";
                btnStartServer.Text = "Start Server";
                var audioStream = _audioStream;
                _audioStream = null;
                audioStream?.Dispose();
                serv = new HttpListener();
                serv.IgnoreWriteExceptions = false;

                if (httpEx.ErrorCode == 32) // Port Already Used
                {
                    Log($"This port {numPort.Value} is already used");
                    var msgResult = MessageBox.Show($"This port {numPort.Value} is already used, Do you want to use another random one ?", "Port Already Used !", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                    if (msgResult == DialogResult.Yes)
                    {
                        numPort.Value += DateTime.Now.Second;
                        Log($"New port is {numPort.Value}");
                        btnStartServer_Click(sender, e);
                    }
                    else
                    {

                        Log("Port Change Request Declined");
                    }

                }
                else if (httpEx.ErrorCode == 183)
                {
                    Log("HTTP listener URL reservation conflict: " + httpEx.Message);
                }
                Log("Server failed to start: " + httpEx.Message + " (HTTP error " + httpEx.ErrorCode + ")");
            }
            catch (Exception ex)
            {
                isWorking = false;
                btnStartServer.Tag = "start";
                btnStartServer.Text = "Start Server";
                var audioStream = _audioStream;
                _audioStream = null;
                audioStream?.Dispose();
                Log("Error! : " + ex.ToString());
            }
        }
        private async Task StartServer()
        {
            //serv = serv??new HttpListener();
            var selectedIP = _ips.ElementAt(comboIPs.SelectedIndex).Item2;

            var url = string.Format("http://{0}:{1}", selectedIP, numPort.Value.ToString());
            txtURL.Text = url;
            serv.Prefixes.Clear();
            serv.Prefixes.Add("http://localhost:" + numPort.Value.ToString() + "/");
            if (_currentSettings.AllowPublicAccess)
                serv.Prefixes.Add("http://+:" + numPort.Value.ToString() + "/");
            else
                serv.Prefixes.Add(url + "/");
            serv.Start();
            _fileShares.StartServing();
            btnStartServer.Tag = "stop";
            btnStartServer.Text = "Stop Server";
            if (btnDirectHost != null) btnDirectHost.Enabled = !_fileTransferOnly;
            if (filePublicAddress != null && (String.IsNullOrWhiteSpace(filePublicAddress.Text) || filePublicAddress.Text == fileSuggestedAddress)) filePublicAddress.Text = url;
            fileSuggestedAddress = url;
            Log("Server Started Successfully!" + (_fileTransferOnly ? " File transfers only; screen and audio endpoints are disabled." : ""));
            appNotify.ShowBalloonTip(1_000, "ScreenTask", $"Server Started Successfully!\r\n{url}", ToolTipIcon.Info);

                _h264Stream.BeginStream();
            if (_currentSettings.ShareSystemAudio && !_fileTransferOnly)
                _ = Task.Run(() => StartAudioCapture());

            Log("Network URL : " + url);
            Log("Localhost URL : " + "http://localhost:" + numPort.Value.ToString() + "/");
            while (isWorking)
            {
                var ctx = await serv.GetContextAsync();
                try
                {
                    var resPath = ctx.Request.Url.LocalPath;

                    if ((FileShareHub.IsRoute(resPath) || resPath == "/ScreenTask.jpg" || resPath == "/stream.mjpg" || resPath == "/stream.mp4" || resPath == "/audio" || resPath == "/stats" || resPath == "/chat" || resPath == "/rtc-signaling" || resPath == "/connection-info") && !IsAuthorized(ctx))
                    {
                        ctx.Response.StatusCode = 401;
                        ctx.Response.AddHeader("WWW-Authenticate", "Basic realm=\"ScreenTask\"");
                        ctx.Response.Close();
                        continue;
                    }

                    if (FileShareHub.IsRoute(resPath))
                    {
                        _ = Task.Run(() => _fileShares.HandleAsync(ctx));
                        continue;
                    }

                    if (_fileTransferOnly && resPath != "/" && resPath != "/files" && resPath != "/files.html" &&
                        !resPath.StartsWith("/files/", StringComparison.Ordinal) && resPath != "/connection-info")
                    {
                        var data = Encoding.UTF8.GetBytes("File-only mode is active. Screen sharing and audio are disabled.");
                        ctx.Response.StatusCode = 403;
                        ctx.Response.ContentType = "text/plain; charset=utf-8";
                        ctx.Response.ContentLength64 = data.Length;
                        if (ctx.Request.HttpMethod != "HEAD") await ctx.Response.OutputStream.WriteAsync(data, 0, data.Length);
                        ctx.Response.Close();
                        continue;
                    }

                    if (resPath == "/stream.mjpg")
                    {
                        EnsureLegacyCaptureStarted();
                        _ = StreamFramesAsync(ctx);
                        continue;
                    }

                    if (resPath == "/stream.mp4")
                    {
                        EnsureLegacyCaptureStarted();
                        StreamH264Async(ctx);
                        continue;
                    }

                    if (resPath == "/connection-info")
                    {
                        var data = Encoding.UTF8.GetBytes("{\"viewerOrigin\":\"" + url + "\",\"hdrCorrection\":" + (_currentSettings.HdrColorCorrection ? "true" : "false") + ",\"screen\":" + _currentSettings.SelectedScreenIndex + ",\"fileOnly\":" + (_fileTransferOnly ? "true" : "false") + "}");
                        ctx.Response.ContentType = "application/json";
                        ctx.Response.Headers["Cache-Control"] = "no-store";
                        ctx.Response.ContentLength64 = data.Length;
                        await ctx.Response.OutputStream.WriteAsync(data, 0, data.Length);
                        ctx.Response.Close();
                        continue;
                    }

                    if (resPath == "/stats")
                    {
                        var json = String.Format(System.Globalization.CultureInfo.InvariantCulture,
                            "{{\"targetFps\":{0},\"sourceFps\":{1:0.0},\"captureFps\":{2:0.0},\"sentFps\":{3:0.0},\"copyMs\":{4:0.0},\"jpegMs\":{5:0.0},\"backend\":\"{6}\",\"codec\":\"{14}\",\"viewers\":{7},\"sentMbps\":{8:0.00},\"bitrateLimitMbps\":{9},\"avgFrameKb\":{10:0.0},\"sendWriteMs\":{11:0.0},\"bitrateWaitPct\":{12:0.0},\"skippedFrames\":{13}}}",
                            _requestedFrameRate, Volatile.Read(ref _sourceFps), Volatile.Read(ref _captureFps), Volatile.Read(ref _sentFps),
                            Volatile.Read(ref _copyMs), Volatile.Read(ref _jpegMs), _captureBackend, Volatile.Read(ref _activeViewers),
                            Volatile.Read(ref _sentMbps), _currentSettings.MaxBitrateMbps, Volatile.Read(ref _avgFrameKb),
                            Volatile.Read(ref _sendWriteMs), Volatile.Read(ref _bitrateWaitPct), Volatile.Read(ref _skippedFrames), _captureCodec);
                        var data = Encoding.UTF8.GetBytes(json);
                        ctx.Response.ContentType = "application/json";
                        ctx.Response.Headers["Cache-Control"] = "no-store";
                        ctx.Response.ContentLength64 = data.Length;
                        await ctx.Response.OutputStream.WriteAsync(data, 0, data.Length);
                        ctx.Response.Close();
                        continue;
                    }

                    if (resPath == "/chat" && ctx.Request.IsWebSocketRequest)
                    {
                        _ = HandleChatClientAsync(ctx);
                        continue;
                    }

                    if (resPath == "/audio" && ctx.Request.IsWebSocketRequest)
                    {
                        if (_audioStream == null)
                        {
                            ctx.Response.StatusCode = 503;
                            ctx.Response.Close();
                        }
                        else
                        {
                            _ = _audioStream.HandleClientAsync(ctx);
                        }
                        continue;
                    }

                    if (resPath == "/rtc-signaling" && ctx.Request.IsWebSocketRequest)
                    {
                        _ = _rtcSignaling.HandleClientAsync(ctx);
                        continue;
                    }

                    if (resPath == "/ScreenTask.jpg")
                    {
                        var currentFrame = _latestFrame;
                        if (currentFrame == null)
                        {
                            ctx.Response.StatusCode = 503;
                        }
                        else
                        {
                            ctx.Response.ContentType = "image/jpeg";
                            ctx.Response.ContentLength64 = currentFrame.Length;
                            await ctx.Response.OutputStream.WriteAsync(currentFrame, 0, currentFrame.Length);
                        }
                        ctx.Response.Close();
                        continue;
                    }

                    if (resPath == "/") // Route The Root Dir to the Index Page
                        resPath = "/index.html";
                    if (resPath == "/files" || resPath == "/files.html" || resPath.StartsWith("/files/", StringComparison.Ordinal)) resPath = "/index.html";
                    if (resPath == "/host") resPath = "/host.html";
                    if (resPath == "/direct") resPath = "/direct.html";
                    var webRoot = Path.GetFullPath(Path.Combine(Application.StartupPath, "WebServer")) + Path.DirectorySeparatorChar;
                    string page;
                    try { page = Path.GetFullPath(webRoot + resPath.TrimStart('/')); }
                    catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException)
                    { ctx.Response.StatusCode = 400; ctx.Response.Close(); continue; }
                    if (!page.StartsWith(webRoot, StringComparison.OrdinalIgnoreCase))
                    { ctx.Response.StatusCode = 403; ctx.Response.Close(); continue; }
                    bool fileExist;
                    lock (locker)
                        fileExist = File.Exists(page);
                    if (!fileExist)
                    {
                        var errorPage = Encoding.UTF8.GetBytes("<h1 style=\"color:red\">Error 404 , File Not Found </h1><hr><a href=\".\\\">Back to Home</a>");
                        ctx.Response.ContentType = "text/html";
                        ctx.Response.StatusCode = 404;
                        try
                        {
                            await ctx.Response.OutputStream.WriteAsync(errorPage, 0, errorPage.Length);
                        }
                        catch (IOException)
                        {
                            // Client disconnected, do nothing.
                        }
                        catch (HttpListenerException)
                        {
                            // Client disconnected, do nothing.
                        }
                        ctx.Response.Close();
                        continue;
                    }


                    if (!IsAuthorized(ctx))
                    {
                        ctx.Response.StatusCode = 401;
                        ctx.Response.AddHeader("WWW-Authenticate", "Basic realm=\"ScreenTask\"");
                        ctx.Response.Close();
                        continue;
                    }

                    //Everything OK! ??? Then Read The File From HDD as Bytes and Send it to the Client
                    byte[] filedata;

                    // Required for One-Time Access of the file {Reader\Writer Problem in OS}
                    rwl.AcquireReaderLock(Timeout.Infinite);
                    filedata = File.ReadAllBytes(page);
                    rwl.ReleaseReaderLock();

                    var fileinfo = new FileInfo(page);
                    if (fileinfo.Extension == ".html" || fileinfo.Extension == ".htm")
                        ctx.Response.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate";
                    else if (fileinfo.Extension == ".css")
                        ctx.Response.Headers["Cache-Control"] = "public, max-age=3600";
                    if (fileinfo.Extension == ".css") // important for IE -> Content-Type must be defiend for CSS files unless will ignored !!!
                        ctx.Response.ContentType = "text/css";
                    else if (fileinfo.Extension == ".js")
                    {
                        ctx.Response.ContentType = "application/javascript";
                        ctx.Response.Headers["Cache-Control"] = "no-store";
                    }
                    else if (fileinfo.Extension == ".svg")
                        ctx.Response.ContentType = "image/svg+xml";
                    else if (fileinfo.Extension == ".html" || fileinfo.Extension == ".htm")
                        ctx.Response.ContentType = "text/html"; // Important For Chrome Otherwise will display the HTML as plain text.



                    ctx.Response.StatusCode = 200;
                    try
                    {
                        await ctx.Response.OutputStream.WriteAsync(filedata, 0, filedata.Length);
                    }
                    catch (IOException)
                    {
                        // Client disconnected, do nothing.
                    }
                    catch (HttpListenerException)
                    {
                        // Client disconnected, do nothing.
                    }

                    ctx.Response.Close();
                }
                catch (Exception ex) when (ex is IOException || ex is HttpListenerException || ex is ObjectDisposedException)
                {
                    // One dropped request must not stop the listener. File
                    // downloads handle their own asynchronous disconnects.
                    try { ctx.Response.Abort(); } catch (ObjectDisposedException) { }
                }
            }

        }
        private void EnsureLegacyCaptureStarted()
        {
            if (!isWorking || Interlocked.CompareExchange(ref _legacyCaptureStarted, 1, 0) != 0) return;
            _ = Task.Factory.StartNew(() => CaptureScreenH264Preferred(), TaskCreationOptions.LongRunning);
        }

        private void EnsureJpegCompatibilityCapture()
        {
            if (Interlocked.CompareExchange(ref _jpegCaptureStarted, 1, 0) != 0) return;
            var generation = Volatile.Read(ref _serverGeneration);
            _ = Task.Run(async () =>
            {
                while (isWorking && generation == Volatile.Read(ref _serverGeneration))
                {
                    if (Volatile.Read(ref _jpegViewers) > 0 && _captureCodec == "H.264")
                    {
                        if (_currentSettings.HdrColorCorrection)
                        {
                            try { CaptureWithFfmpeg(Path.Combine(Application.StartupPath, "ffmpeg.exe"), 10, _currentSettings.OutputWidth,
                                _currentSettings.ImageQuality, _currentSettings.IsShowMouseEnabled, _currentSettings.SelectedScreenIndex, true); }
                            catch (Exception ex) { Log("HDR JPEG capture unavailable: " + ex.Message); await Task.Delay(2000); }
                        }
                        else TakeScreenshot(_currentSettings.IsShowMouseEnabled);
                    }
                    await Task.Delay(100); // Compatibility viewers need at most 10 FPS.
                }
            });
        }

        private void StartAudioCapture()
        {
            if (!isWorking)
                return;

            var mode = _currentSettings.AudioMode;
            var audioStream = new AudioStreamServer(_currentSettings.AudioDeviceId,
                mode == "device" ? 0 : _currentSettings.AudioProcessId, mode == "exclude", Log);
            try
            {
                audioStream.Start();
                if (!isWorking)
                {
                    audioStream.Dispose();
                    return;
                }

                _audioStream = audioStream;
                Log("Audio capture started: " + mode + (mode == "device" ? "" : " " + _currentSettings.AudioProcessName + " (PID " + _currentSettings.AudioProcessId + ")"));
            }
            catch (Exception audioException)
            {
                audioStream.Dispose();
                Log("System audio is unavailable: " + audioException.Message);
            }
        }

        private void CaptureScreenH264Preferred()
        {
            var ffmpeg = Path.Combine(Application.StartupPath, "ffmpeg.exe");
            if (!File.Exists(ffmpeg))
            {
                Log("H.264 capture is unavailable because ffmpeg.exe was not found; using the JPEG compatibility stream.");
                _h264Stream.FailStream("FFmpeg was not found.");
                CaptureScreenEvery();
                return;
            }

            var encoders = new[] { "h264_mf", "libx264" };
            while (isWorking)
            {
                var fps = _requestedFrameRate;
                var width = _currentSettings.OutputWidth;
                var quality = _currentSettings.ImageQuality;
                var mouse = _currentSettings.IsShowMouseEnabled;
                var screen = _currentSettings.SelectedScreenIndex;
                var hdr = _currentSettings.HdrColorCorrection;
                var exposure = _currentSettings.HdrExposure;
                var peakNits = _currentSettings.HdrPeakNits;
                var restartedForSettings = false;

                foreach (var encoder in encoders)
                {
                    _h264Stream.BeginStream();
                    try
                    {
                        CaptureWithH264Ffmpeg(ffmpeg, encoder, fps, width, quality, mouse, screen, hdr, exposure, peakNits);
                    }
                    catch (Exception ex)
                    {
                        if (!isWorking) return;
                        Log("H.264 " + encoder + " capture failed: " + ex.Message);
                    }

                    if (!isWorking) return;
                    if (fps != _requestedFrameRate || width != _currentSettings.OutputWidth ||
                        quality != _currentSettings.ImageQuality || mouse != _currentSettings.IsShowMouseEnabled ||
                        screen != _currentSettings.SelectedScreenIndex || hdr != _currentSettings.HdrColorCorrection ||
                        exposure != _currentSettings.HdrExposure || peakNits != _currentSettings.HdrPeakNits)
                    {
                        restartedForSettings = true;
                        break;
                    }
                }

                if (restartedForSettings) continue;
                if (!isWorking) return;

                Log("H.264 capture could not start; falling back to JPEG capture.");
                _captureCodec = "MJPEG";
                _captureBackend = "GDI";
                _h264Stream.BeginStream();
                _h264Stream.FailStream("H.264 capture is unavailable; the JPEG stream is active.");
                CaptureScreenEvery();
                return;
            }
        }

        private void CaptureWithH264Ffmpeg(string ffmpeg, string encoder, int fps, int requestedWidth, int quality, bool mouse, int screen, bool hdr, double exposure, int peakNits)
        {
            if (screen < 0 || screen >= Screen.AllScreens.Length) screen = 0;
            var bounds = Screen.AllScreens[screen].Bounds;
            var width = requestedWidth > 0 ? Math.Min(bounds.Width, requestedWidth) : bounds.Width;
            width -= width % 2;
            if (width < 2) width = bounds.Width - (bounds.Width % 2);
            var height = Math.Max(2, (int)Math.Round(bounds.Height * width / (double)bounds.Width));
            height -= height % 2;
            var level = GetH264Level(width, height, fps);
            var levelName = level == 32 ? "3.2" : level == 42 ? "4.2" : level == 51 ? "5.1" : level == 52 ? "5.2" : level == 60 ? "6.0" : "6.1";
            var codec = String.Format(System.Globalization.CultureInfo.InvariantCulture, "avc1.42E0{0:X2}", level);
            var bitrateMbps = _currentSettings.MaxBitrateMbps;
            var filter = HdrColorCorrection.CaptureFilter(screen, Math.Max(1, Math.Min(120, fps)), mouse, width, hdr, exposure, peakNits, false);
            var encoderOptions = encoder == "h264_mf"
                ? "-scenario display_remoting -rate_control " + (bitrateMbps > 0 ? "cbr" : "quality") +
                    " -quality " + Math.Max(1, Math.Min(100, quality))
                : "-preset ultrafast -tune zerolatency";
            var profileOption = encoder == "h264_mf" ? "-profile:v 66" : "-profile:v baseline";
            var rateOptions = bitrateMbps > 0
                ? String.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "-b:v {0}M -maxrate {0}M -bufsize {1}M ", bitrateMbps, Math.Max(2, bitrateMbps * 2))
                : String.Empty;
            var keyframeInterval = Math.Max(1, fps / 2);
            var args = "-hide_banner -nostdin -loglevel warning -stats_period 1 -progress pipe:2 -nostats " +
                "-filter_complex \"" + filter + "\" -an -c:v " + encoder + " " + encoderOptions + " " + rateOptions +
                "-color_primaries bt709 -color_trc bt709 -colorspace bt709 -color_range tv " + profileOption + " -level:v " + levelName + " -g " + keyframeInterval + " -bf 0 " +
                "-force_key_frames \"expr:gte(t,n_forced*0.5)\" -f mp4 " +
                "-movflags +empty_moov+default_base_moof+frag_keyframe+separate_moof -frag_duration 500000 -flush_packets 1 pipe:1";
            var info = new ProcessStartInfo(ffmpeg, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using (var process = new Process { StartInfo = info })
            {
                process.ErrorDataReceived += (sender, eventArgs) =>
                {
                    if (String.IsNullOrWhiteSpace(eventArgs.Data)) return;
                    var fpsValue = GetProgressNumber(eventArgs.Data, "fps");
                    if (fpsValue >= 0)
                    {
                        Volatile.Write(ref _sourceFps, fpsValue);
                        Volatile.Write(ref _captureFps, fpsValue);
                    }
                    if (!eventArgs.Data.StartsWith("frame=", StringComparison.Ordinal) &&
                        !eventArgs.Data.StartsWith("fps=", StringComparison.Ordinal) &&
                        !eventArgs.Data.StartsWith("bitrate=", StringComparison.Ordinal) &&
                        !eventArgs.Data.StartsWith("total_size=", StringComparison.Ordinal) &&
                        !eventArgs.Data.StartsWith("out_time=", StringComparison.Ordinal) &&
                        !eventArgs.Data.StartsWith("out_time_us=", StringComparison.Ordinal) &&
                        !eventArgs.Data.StartsWith("out_time_ms=", StringComparison.Ordinal) &&
                        !eventArgs.Data.StartsWith("stream_", StringComparison.Ordinal) &&
                        !eventArgs.Data.StartsWith("dup_frames=", StringComparison.Ordinal) &&
                        !eventArgs.Data.StartsWith("drop_frames=", StringComparison.Ordinal) &&
                        !eventArgs.Data.StartsWith("speed=", StringComparison.Ordinal) &&
                        !eventArgs.Data.StartsWith("progress=", StringComparison.Ordinal))
                        Log("FFmpeg: " + eventArgs.Data);
                };
                if (!process.Start()) throw new InvalidOperationException("FFmpeg did not start.");
                _captureProcess = process;
                process.BeginErrorReadLine();
                _captureCodec = "H.264";
                _captureBackend = "DXGI / H.264 " + (encoder == "h264_mf" ? "Media Foundation" : "software") + " (" + encoder + ")" + (hdr ? " / HDR to SDR" : "");
                _h264Stream.SetCodecString(codec);
                _h264Stream.SetHdrCorrected(hdr);
                Volatile.Write(ref _copyMs, 0);
                Volatile.Write(ref _jpegMs, 0);
                Volatile.Write(ref _sentFps, 0);
                Volatile.Write(ref _sentMbps, 0);
                if (hdr) Log("HDR color correction: linear scRGB to SDR BT.709, exposure " + exposure + " EV, peak " + peakNits + " nits.");
                Log("H.264 " + encoder + " started at " + fps + " FPS, " + width + "x" + height +
                    (bitrateMbps > 0 ? ", capped at " + bitrateMbps + " Mbps." : ", uncapped bitrate."));

                try
                {
                    ReadFmp4Output(process.StandardOutput.BaseStream);
                }
                finally
                {
                    StopProcess(process);
                    process.WaitForExit(2000);
                    _captureProcess = null;
                }

                if (!isWorking) return;
                if (!_h264Stream.HasInitialization)
                    throw new InvalidOperationException("FFmpeg exited before producing an H.264 initialization segment (exit code " + process.ExitCode + ").");
                if (process.ExitCode != 0)
                    throw new InvalidOperationException("FFmpeg exited with code " + process.ExitCode + ".");
            }
        }

        private void ReadFmp4Output(Stream output)
        {
            var initialization = new List<byte[]>();
            var prefix = new List<byte[]>();
            var fragment = new List<byte[]>();
            byte[] currentMoof = null;
            var hasInitialization = false;
            byte[] box;
            string type;

            while (isWorking && H264StreamHub.TryReadMp4Box(output, out box, out type))
            {
                if (!hasInitialization)
                {
                    initialization.Add(box);
                    if (type == "moov")
                    {
                        var init = JoinBoxes(initialization);
                        _h264Stream.SetCodecString(H264StreamHub.GetCodecString(init));
                        _h264Stream.PublishInitialization(init);
                        initialization.Clear();
                        hasInitialization = true;
                    }
                    continue;
                }

                if (type == "moof")
                {
                    fragment.Clear();
                    fragment.AddRange(prefix);
                    prefix.Clear();
                    currentMoof = box;
                    fragment.Add(box);
                    continue;
                }

                if (currentMoof == null)
                {
                    prefix.Add(box);
                    if (prefix.Count > 8) prefix.RemoveAt(0);
                    continue;
                }

                fragment.Add(box);
                if (type == "mdat")
                {
                    _h264Stream.PublishFragment(JoinBoxes(fragment), H264StreamHub.GetFragmentFrameCount(currentMoof));
                    fragment.Clear();
                    currentMoof = null;
                }
            }
        }

        private static byte[] JoinBoxes(IList<byte[]> boxes)
        {
            var length = 0;
            foreach (var box in boxes) checked { length += box.Length; }
            var joined = new byte[length];
            var offset = 0;
            foreach (var box in boxes)
            {
                Buffer.BlockCopy(box, 0, joined, offset, box.Length);
                offset += box.Length;
            }
            return joined;
        }

        private static int GetH264Level(int width, int height, int fps)
        {
            var macroblocksPerFrame = ((width + 15) / 16) * ((height + 15) / 16);
            var macroblocksPerSecond = (long)macroblocksPerFrame * Math.Max(1, fps);
            if (macroblocksPerFrame <= 5120 && macroblocksPerSecond <= 216000) return 32;
            if (macroblocksPerFrame <= 8704 && macroblocksPerSecond <= 522240) return 42;
            if (macroblocksPerFrame <= 36864 && macroblocksPerSecond <= 983040) return 51;
            if (macroblocksPerFrame <= 36864 && macroblocksPerSecond <= 2073600) return 52;
            if (macroblocksPerFrame <= 139264 && macroblocksPerSecond <= 4177920) return 60;
            return 61;
        }

        private static double GetProgressNumber(string line, string name)
        {
            var start = line.IndexOf(name + "=", StringComparison.Ordinal);
            if (start < 0) return -1;
            start += name.Length + 1;
            var end = line.IndexOfAny(new[] { ' ', '\t', '\r', '\n' }, start);
            if (end < 0) end = line.Length;
            double result;
            return Double.TryParse(line.Substring(start, end - start), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out result) ? result : -1;
        }

        private void StreamH264Async(HttpListenerContext context)
        {
            Interlocked.Increment(ref _activeViewers);
            _ = Task.Run(async () =>
            {
                try { await _h264Stream.HandleClientAsync(context); }
                finally { Interlocked.Decrement(ref _activeViewers); }
            });
        }

        private void CaptureScreenEvery()
        {
            var ffmpeg = Path.Combine(Application.StartupPath, "ffmpeg.exe");
            if (File.Exists(ffmpeg))
            {
                while (isWorking)
                {
                    var fps = _requestedFrameRate;
                    var width = _currentSettings.OutputWidth;
                    var quality = _currentSettings.ImageQuality;
                    var mouse = _currentSettings.IsShowMouseEnabled;
                    var screen = _currentSettings.SelectedScreenIndex;
                    var hdr = _currentSettings.HdrColorCorrection;
                    var exposure = _currentSettings.HdrExposure;
                    var peakNits = _currentSettings.HdrPeakNits;
                    try
                    {
                        CaptureWithFfmpeg(ffmpeg, fps, width, quality, mouse, screen);
                        if (!isWorking) return;
                        if (fps != _requestedFrameRate || width != _currentSettings.OutputWidth ||
                            quality != _currentSettings.ImageQuality || mouse != _currentSettings.IsShowMouseEnabled ||
                            screen != _currentSettings.SelectedScreenIndex || hdr != _currentSettings.HdrColorCorrection ||
                            exposure != _currentSettings.HdrExposure || peakNits != _currentSettings.HdrPeakNits)
                            continue;
                        Log("DirectX capture ended; switching to GDI capture.");
                    }
                    catch (Exception ex)
                    {
                        if (!isWorking) return;
                        Log("DirectX capture is unavailable: " + ex.Message);
                    }
                    break;
                }
            }
            if (isWorking)
            {
                if (_currentSettings.HdrColorCorrection) Log("Native HDR correction is unavailable; Windows GDI fallback cannot guarantee correct HDR colors.");
                CaptureWithGdi();
            }
        }

        private void StopCaptureProcess()
        {
            StopProcess(_captureProcess);
            StopProcess(_jpegCompatibilityProcess);
        }

        private static void StopProcess(Process process)
        {
            if (process == null) return;
            try { if (!process.HasExited) process.Kill(); }
            catch (Exception ex) when (ex is InvalidOperationException || ex is System.ComponentModel.Win32Exception || ex is ObjectDisposedException) { }
        }

        private void CaptureWithFfmpeg(string ffmpeg, int fps, int width, int quality, bool mouse, int screen, bool compatibility = false)
        {
            var hdr = _currentSettings.HdrColorCorrection;
            var exposure = _currentSettings.HdrExposure;
            var peakNits = _currentSettings.HdrPeakNits;
            var sourceRate = Math.Min(240, Math.Max(1, fps * 2));
            var jpegQuantizer = Math.Max(2, Math.Min(20, (int)Math.Round(2 + (100 - quality) * 0.12)));
            var filter = HdrColorCorrection.CaptureFilter(screen, sourceRate, mouse, width, hdr, exposure, peakNits, true);
            var info = new ProcessStartInfo(ffmpeg,
                "-hide_banner -nostdin -loglevel error -filter_complex \"" + filter +
                "\" -c:v mjpeg -q:v " + jpegQuantizer + " -f image2pipe pipe:1")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using (var process = new Process { StartInfo = info })
            {
                process.ErrorDataReceived += (sender, args) =>
                {
                    if (!String.IsNullOrWhiteSpace(args.Data)) Log("FFmpeg: " + args.Data);
                };
                if (!process.Start()) throw new InvalidOperationException("FFmpeg did not start.");
                if (compatibility) _jpegCompatibilityProcess = process; else _captureProcess = process;
                process.BeginErrorReadLine();
                if (!compatibility) _captureBackend = "DirectX" + (hdr ? " / HDR to SDR" : "");
                Volatile.Write(ref _copyMs, 0);
                Volatile.Write(ref _jpegMs, 0);
                Log("DirectX capture started at " + fps + " FPS target.");
                var bytes = new byte[65536];
                MemoryStream jpeg = null;
                byte previous = 0;
                ulong lastHash = 0;
                var sampleStart = Stopwatch.GetTimestamp();
                var sampleFrames = 0;
                var sampleSourceFrames = 0;
                var nextPublish = (double)Stopwatch.GetTimestamp();
                var interval = Stopwatch.Frequency / (double)Math.Max(1, fps);
                try
                {
                    int read;
                    while (isWorking && (compatibility ? Volatile.Read(ref _jpegViewers) > 0 && _captureCodec == "H.264" : fps == _requestedFrameRate) && width == _currentSettings.OutputWidth &&
                        quality == _currentSettings.ImageQuality && mouse == _currentSettings.IsShowMouseEnabled &&
                        screen == _currentSettings.SelectedScreenIndex && hdr == _currentSettings.HdrColorCorrection &&
                        exposure == _currentSettings.HdrExposure && peakNits == _currentSettings.HdrPeakNits &&
                        (read = process.StandardOutput.BaseStream.Read(bytes, 0, bytes.Length)) > 0)
                    {
                        for (var i = 0; i < read; i++)
                        {
                            var value = bytes[i];
                            if (jpeg == null)
                            {
                                if (previous == 0xff && value == 0xd8)
                                {
                                    jpeg = new MemoryStream(262144);
                                    jpeg.WriteByte(0xff);
                                    jpeg.WriteByte(0xd8);
                                }
                            }
                            else
                            {
                                jpeg.WriteByte(value);
                                if (jpeg.Length > 12000000)
                                {
                                    jpeg.Dispose();
                                    jpeg = null;
                                }
                                else if (previous == 0xff && value == 0xd9)
                                {
                                    var frame = jpeg.ToArray();
                                    jpeg.Dispose();
                                    jpeg = null;
                                    sampleSourceFrames++;
                                    ulong hash = 14695981039346656037UL;
                                    unchecked { foreach (var octet in frame) hash = (hash ^ octet) * 1099511628211UL; }
                                    var now = Stopwatch.GetTimestamp();
                                    if (hash != lastHash && now >= nextPublish)
                                    {
                                        PublishFrame(frame);
                                        sampleFrames++;
                                        lastHash = hash;
                                        nextPublish += interval;
                                        if (nextPublish < now) nextPublish = now;
                                    }
                                }
                            }
                            previous = value;
                        }
                        var current = Stopwatch.GetTimestamp();
                        if (!compatibility && current - sampleStart >= Stopwatch.Frequency)
                        {
                            Volatile.Write(ref _captureFps, sampleFrames * (double)Stopwatch.Frequency / (current - sampleStart));
                            Volatile.Write(ref _sourceFps, sampleSourceFrames * (double)Stopwatch.Frequency / (current - sampleStart));
                            sampleFrames = 0;
                            sampleSourceFrames = 0;
                            sampleStart = current;
                        }
                    }
                }
                finally
                {
                    jpeg?.Dispose();
                    StopProcess(process);
                    process.WaitForExit(2000);
                    if (compatibility) _jpegCompatibilityProcess = null; else _captureProcess = null;
                }
            }
        }

        private void PublishFrame(byte[] frame)
        {
            TaskCompletionSource<bool> signal;
            lock (_frameLock)
            {
                _latestFrame = frame;
                _frameSequence++;
                signal = _frameSignal;
                _frameSignal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            }
            signal.TrySetResult(true);
        }

        private void CaptureWithGdi()
        {
            _captureBackend = "GDI";
            var nextFrame = (double)Stopwatch.GetTimestamp();
            var sampleStart = Stopwatch.GetTimestamp();
            var sampleFrames = 0;
            var timerReady = timeBeginPeriod(1) == 0;
            try
            {
                while (isWorking)
                {
                    if (TakeScreenshot(_currentSettings.IsShowMouseEnabled)) sampleFrames++;
                    var now = Stopwatch.GetTimestamp();
                    if (now - sampleStart >= Stopwatch.Frequency)
                    {
                        Volatile.Write(ref _captureFps, sampleFrames * (double)Stopwatch.Frequency / (now - sampleStart));
                        Volatile.Write(ref _sourceFps, sampleFrames * (double)Stopwatch.Frequency / (now - sampleStart));
                        sampleFrames = 0;
                        sampleStart = now;
                    }
                    nextFrame += Stopwatch.Frequency / (double)Math.Max(1, _requestedFrameRate);
                    var remainingTicks = nextFrame - Stopwatch.GetTimestamp();
                    if (remainingTicks > 0)
                    {
                        var remainingMilliseconds = remainingTicks * 1000.0 / Stopwatch.Frequency;
                        if (remainingMilliseconds > 2)
                            Thread.Sleep(Math.Max(1, (int)remainingMilliseconds - 1));
                        while (isWorking && Stopwatch.GetTimestamp() < nextFrame)
                            Thread.SpinWait(30);
                    }
                    else
                    {
                        // Keep the stream live when encoding takes longer than the target interval.
                        nextFrame = Stopwatch.GetTimestamp();
                    }
                }
            }
            finally { if (timerReady) timeEndPeriod(1); }
        }
        private bool TakeScreenshot(bool captureMouse)
        {
            try
            {
                var start = Stopwatch.GetTimestamp();
                long beforeEncode = 0;
                long afterEncode = 0;
                ImageCodecInfo jpgEncoder = GetEncoder(ImageFormat.Jpeg);

                var encoderQuality = System.Drawing.Imaging.Encoder.Quality;
                var encoderParam = new EncoderParameter(encoderQuality, _currentSettings.ImageQuality);
                var encoderParams = new EncoderParameters(1);
                encoderParams.Param[0] = encoderParam;
                using (var frame = new MemoryStream())
                {
                    if (captureMouse)
                    {
                        using (var captured = ScreenCapturePInvoke.CaptureSelectedScreen(true, _currentSettings.SelectedScreenIndex))
                        {
                            var width = _currentSettings.OutputWidth > 0 ? Math.Min(captured.Width, _currentSettings.OutputWidth) : captured.Width;
                            var height = Math.Max(1, (int)Math.Round(captured.Height * width / (double)captured.Width));
                            if (width == captured.Width)
                            {
                                beforeEncode = Stopwatch.GetTimestamp();
                                captured.Save(frame, jpgEncoder, encoderParams);
                                afterEncode = Stopwatch.GetTimestamp();
                            }
                            else
                            {
                                using (var scaled = new Bitmap(width, height))
                                using (var graphics = Graphics.FromImage(scaled))
                                {
                                    graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.Bilinear;
                                    graphics.DrawImage(captured, 0, 0, width, height);
                                    beforeEncode = Stopwatch.GetTimestamp();
                                    scaled.Save(frame, jpgEncoder, encoderParams);
                                    afterEncode = Stopwatch.GetTimestamp();
                                }
                            }
                        }
                    }
                    else
                    {
                        Rectangle bounds = Screen.AllScreens[_currentSettings.SelectedScreenIndex].Bounds;
                        var width = _currentSettings.OutputWidth > 0 ? Math.Min(bounds.Width, _currentSettings.OutputWidth) : bounds.Width;
                        var height = Math.Max(1, (int)Math.Round(bounds.Height * width / (double)bounds.Width));
                        using (var bitmap = new Bitmap(width, height, PixelFormat.Format24bppRgb))
                        using (var graphics = Graphics.FromImage(bitmap))
                        {
                            var desktop = GetDC(IntPtr.Zero);
                            var target = graphics.GetHdc();
                            try
                            {
                                SetStretchBltMode(target, 3); // COLORONCOLOR: fast downscaling.
                                if (!StretchBlt(target, 0, 0, width, height, desktop, bounds.X, bounds.Y,
                                    bounds.Width, bounds.Height, 0x00CC0020))
                                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                            }
                            finally
                            {
                                graphics.ReleaseHdc(target);
                                ReleaseDC(IntPtr.Zero, desktop);
                            }
                            beforeEncode = Stopwatch.GetTimestamp();
                            bitmap.Save(frame, jpgEncoder, encoderParams);
                            afterEncode = Stopwatch.GetTimestamp();
                        }
                    }
                    Volatile.Write(ref _copyMs, (beforeEncode - start) * 1000.0 / Stopwatch.Frequency);
                    Volatile.Write(ref _jpegMs, (afterEncode - beforeEncode) * 1000.0 / Stopwatch.Frequency);
                    PublishFrame(frame.ToArray());
                }
                return true;
            }
            catch (Exception ex)
            {
                Log($"Error taking screenshot: {ex.Message}");
                return false;
            }
        }

        private async Task StreamFramesAsync(HttpListenerContext context)
        {
            Interlocked.Increment(ref _activeViewers);
            Interlocked.Increment(ref _jpegViewers);
            EnsureJpegCompatibilityCapture();
            context.Response.StatusCode = 200;
            context.Response.ContentType = "multipart/x-mixed-replace; boundary=frame";
            context.Response.SendChunked = true;
            context.Response.KeepAlive = true;
            var boundary = Encoding.ASCII.GetBytes("--frame\r\nContent-Type: image/jpeg\r\nContent-Length: ");
            var crlf = Encoding.ASCII.GetBytes("\r\n\r\n");
            var ending = Encoding.ASCII.GetBytes("\r\n");
            long lastSequence = -1;
            var nextSend = Stopwatch.GetTimestamp();
            var sampleStart = Stopwatch.GetTimestamp();
            var sentFrames = 0;
            long sentBytes = 0;
            long writeTicks = 0;
            long bitrateWaitTicks = 0;
            long skippedFrames = 0;

            try
            {
                while (isWorking)
                {
                    byte[] frame;
                    long sequence;
                    Task frameSignal;
                    lock (_frameLock)
                    {
                        frame = _latestFrame;
                        sequence = _frameSequence;
                        frameSignal = _frameSignal.Task;
                    }
                    var now = Stopwatch.GetTimestamp();
                    if (frame != null && sequence != lastSequence && now >= nextSend)
                    {
                        if (lastSequence >= 0 && sequence > lastSequence + 1)
                            skippedFrames += sequence - lastSequence - 1;
                        var writeStart = Stopwatch.GetTimestamp();
                        await context.Response.OutputStream.WriteAsync(boundary, 0, boundary.Length);
                        var length = Encoding.ASCII.GetBytes(frame.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
                        await context.Response.OutputStream.WriteAsync(length, 0, length.Length);
                        await context.Response.OutputStream.WriteAsync(crlf, 0, crlf.Length);
                        await context.Response.OutputStream.WriteAsync(frame, 0, frame.Length);
                        await context.Response.OutputStream.WriteAsync(ending, 0, ending.Length);
                        await context.Response.OutputStream.FlushAsync();
                        writeTicks += Stopwatch.GetTimestamp() - writeStart;
                        lastSequence = sequence;
                        sentFrames++;
                        sentBytes += frame.Length;
                        if (_currentSettings.MaxBitrateMbps > 0)
                        {
                            var requiredTicks = frame.Length * 8.0 * Stopwatch.Frequency / (_currentSettings.MaxBitrateMbps * 1000000.0);
                            nextSend = Stopwatch.GetTimestamp() + (long)requiredTicks;
                        }
                    }
                    else if (now < nextSend)
                    {
                        var waitMs = Math.Max(1, (int)Math.Ceiling((nextSend - now) * 1000.0 / Stopwatch.Frequency));
                        var waitStart = Stopwatch.GetTimestamp();
                        await Task.Delay(Math.Min(waitMs, 1000));
                        if (_currentSettings.MaxBitrateMbps > 0)
                            bitrateWaitTicks += Stopwatch.GetTimestamp() - waitStart;
                    }
                    else
                    {
                        await Task.WhenAny(frameSignal, Task.Delay(1000));
                    }
                    now = Stopwatch.GetTimestamp();
                    if (now - sampleStart >= Stopwatch.Frequency)
                    {
                        var elapsedTicks = now - sampleStart;
                        Volatile.Write(ref _sentFps, sentFrames * (double)Stopwatch.Frequency / elapsedTicks);
                        Volatile.Write(ref _sentMbps, sentBytes * 8.0 * Stopwatch.Frequency / elapsedTicks / 1000000.0);
                        Volatile.Write(ref _avgFrameKb, sentFrames == 0 ? 0 : sentBytes / (double)sentFrames / 1024.0);
                        Volatile.Write(ref _sendWriteMs, sentFrames == 0 ? 0 : writeTicks * 1000.0 / Stopwatch.Frequency / sentFrames);
                        Volatile.Write(ref _bitrateWaitPct, Math.Min(100, bitrateWaitTicks * 100.0 / elapsedTicks));
                        Volatile.Write(ref _skippedFrames, skippedFrames);
                        sentFrames = 0;
                        sentBytes = 0;
                        writeTicks = 0;
                        bitrateWaitTicks = 0;
                        skippedFrames = 0;
                        sampleStart = now;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException || ex is HttpListenerException || ex is ObjectDisposedException)
            {
                // Viewer disconnected or server stopped.
            }
            finally
            {
                Interlocked.Decrement(ref _activeViewers);
                Interlocked.Decrement(ref _jpegViewers);
                context.Response.Close();
            }
        }

        private async Task HandleChatClientAsync(HttpListenerContext context)
        {
            WebSocket socket = null;
            var id = Guid.NewGuid();
            try
            {
                socket = (await context.AcceptWebSocketAsync(null)).WebSocket;
                _chatClients[id] = socket;
                string[] history;
                lock (_chatLock) history = _chatHistory.ToArray();
                await _chatSendLock.WaitAsync();
                try { foreach (var line in history) await SendChatAsync(socket, line); }
                finally { _chatSendLock.Release(); }

                var buffer = new byte[4096];
                while (socket.State == WebSocketState.Open)
                {
                    var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                    if (result.MessageType == WebSocketMessageType.Close) break;
                    if (result.MessageType != WebSocketMessageType.Text || !result.EndOfMessage) continue;
                    var payload = Encoding.UTF8.GetString(buffer, 0, result.Count);
                    var split = payload.IndexOf('\n');
                    if (split < 0) continue;
                    var name = payload.Substring(0, split).Trim();
                    var message = payload.Substring(split + 1).Trim();
                    if (name.Length == 0 || message.Length == 0) continue;
                    name = name.Substring(0, Math.Min(name.Length, 24)).Replace("\r", " ");
                    message = message.Substring(0, Math.Min(message.Length, 400)).Replace("\r", " ");
                    var line = name + "\n" + message;
                    lock (_chatLock)
                    {
                        _chatHistory.Enqueue(line);
                        while (_chatHistory.Count > 100) _chatHistory.Dequeue();
                    }
                    await _chatSendLock.WaitAsync();
                    try { foreach (var client in _chatClients.Values) await SendChatAsync(client, line); }
                    finally { _chatSendLock.Release(); }
                }
            }
            catch (Exception ex) when (ex is WebSocketException || ex is ObjectDisposedException || ex is HttpListenerException || ex is InvalidOperationException)
            {
                // Viewer disconnected.
            }
            finally
            {
                WebSocket removed;
                _chatClients.TryRemove(id, out removed);
                if (socket != null)
                {
                    try { if (socket.State == WebSocketState.Open || socket.State == WebSocketState.CloseReceived) await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Chat closed", CancellationToken.None); }
                    catch (WebSocketException) { }
                    socket.Dispose();
                }
            }
        }

        private static Task SendChatAsync(WebSocket socket, string line)
        {
            var data = Encoding.UTF8.GetBytes(line);
            if (socket.State != WebSocketState.Open) return Task.FromResult(0);
            return socket.SendAsync(new ArraySegment<byte>(data), WebSocketMessageType.Text, true, CancellationToken.None);
        }

        private bool IsAuthorized(HttpListenerContext context)
        {
            if (!_currentSettings.IsPrivateSession)
                return true;

            var authorization = context.Request.Headers["Authorization"];
            if (String.IsNullOrEmpty(authorization) || !authorization.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
                return false;

            try
            {
                var credentials = Encoding.UTF8.GetString(Convert.FromBase64String(authorization.Substring(6)));
                return credentials == String.Format("{0}:{1}", txtUser.Text, txtPassword.Text);
            }
            catch (FormatException)
            {
                return false;
            }
        }

        private ImageCodecInfo GetEncoder(ImageFormat format)
        {
            ImageCodecInfo[] codecs = ImageCodecInfo.GetImageEncoders();
            foreach (ImageCodecInfo codec in codecs)
            {
                if (codec.FormatID == format.Guid)
                {
                    return codec;
                }
            }
            return null;
        }
        private string GetIPv4Address()
        {
            string IP4Address = String.Empty;

            foreach (IPAddress IPA in Dns.GetHostAddresses(Dns.GetHostName()))
            {
                if (IPA.AddressFamily == AddressFamily.InterNetwork)
                {
                    IP4Address = IPA.ToString();
                    break;
                }
            }

            return IP4Address;
        }
        private List<Tuple<string, string>> GetAllIPv4Addresses()
        {
            List<Tuple<string, string>> ipList = new List<Tuple<string, string>>();
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {

                foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily == AddressFamily.InterNetwork)
                    {
                        ipList.Add(Tuple.Create(ni.Name, ua.Address.ToString()));
                    }
                }
            }
            return ipList;
        }
        private async Task AddFirewallRule(int port)
        {
            await Task.Run(() =>
            {
                var rulename = $"Screen Task On Port {port}";
                var remoteip = _currentSettings.AllowPublicAccess ? "any" : "localsubnet";
                var listenUrl = _currentSettings.AllowPublicAccess
                    ? $"http://+:{port}/"
                    : $"http://{_currentSettings.IP}:{port}/";
                var loopbackUrl = $"http://localhost:{port}/";
                var ruleReady = FirewallRuleReady(rulename, port, remoteip);
                var urlacl = RunCMD("netsh http show urlacl url=" + listenUrl);
                var urlaclReady = urlacl.IndexOf(listenUrl, StringComparison.OrdinalIgnoreCase) >= 0;
                var loopbackAcl = RunCMD("netsh http show urlacl url=" + loopbackUrl);
                var loopbackAclReady = loopbackAcl.IndexOf(loopbackUrl, StringComparison.OrdinalIgnoreCase) >= 0;

                if (ruleReady && urlaclReady && loopbackAclReady)
                    return;

                var commands = new List<string>();
                if (!ruleReady)
                {
                    var update = $"netsh advfirewall firewall set rule name=\"{rulename}\" new enable=yes dir=in action=allow remoteip={remoteip} protocol=tcp localport={port}";
                    var add = $"netsh advfirewall firewall add rule name=\"{rulename}\" dir=in action=allow remoteip={remoteip} protocol=tcp localport={port}";
                    commands.Add("(" + update + " || " + add + ")");
                }

                if (!urlaclReady)
                    commands.Add("netsh http add urlacl url=" + listenUrl + " sddl=\"D:(A;;GX;;;WD)\"");
                if (!loopbackAclReady)
                    commands.Add("netsh http add urlacl url=" + loopbackUrl + " sddl=\"D:(A;;GX;;;WD)\"");

                RunCMD(String.Join(" & ", commands), true);

                var updatedUrlAcl = RunCMD("netsh http show urlacl url=" + listenUrl);
                if (updatedUrlAcl.IndexOf(listenUrl, StringComparison.OrdinalIgnoreCase) < 0)
                    throw new InvalidOperationException("Windows did not create the HTTP URL reservation for " + listenUrl + ". Start ScreenTask as administrator once or add the reservation manually.");
                var updatedLoopbackAcl = RunCMD("netsh http show urlacl url=" + loopbackUrl);
                if (updatedLoopbackAcl.IndexOf(loopbackUrl, StringComparison.OrdinalIgnoreCase) < 0)
                    throw new InvalidOperationException("Windows did not create the localhost URL reservation for " + loopbackUrl + ". Start ScreenTask as administrator once or add the reservation manually.");
                if (FirewallRuleReady(rulename, port, remoteip))
                    Log("Screen Task firewall and URL reservation are ready.");
                else
                    Log("HTTP URL reservation is ready, but Windows did not confirm the firewall rule. Remote viewers may be blocked by the firewall.");
            });

        }

        private bool FirewallRuleReady(string name, int port, string remoteIp)
        {
            try
            {
                var type = Type.GetTypeFromProgID("HNetCfg.FwPolicy2");
                if (type == null) return false;
                dynamic policy = Activator.CreateInstance(type);
                dynamic rule = policy.Rules.Item(name);
                if (rule == null) return false;
                var scope = (string)rule.RemoteAddresses;
                var scopeReady = remoteIp == "any" ? scope == "*" || String.Equals(scope, "Any", StringComparison.OrdinalIgnoreCase)
                    : String.Equals(scope, "LocalSubnet", StringComparison.OrdinalIgnoreCase);
                return (bool)rule.Enabled && (int)rule.Direction == 1 && (int)rule.Action == 1
                    && (int)rule.Protocol == 6 && String.Equals((string)rule.LocalPorts, port.ToString(), StringComparison.Ordinal)
                    && scopeReady;
            }
            catch (Exception)
            {
                return false;
            }
        }
        private string RunCMD(string cmd, bool requireAdmin = false)
        {
            using (Process proc = new Process())
            {
                proc.StartInfo.FileName = "cmd.exe";
                proc.StartInfo.Arguments = "/C " + cmd;
                proc.StartInfo.CreateNoWindow = true;
                if (requireAdmin)
                {
                    proc.StartInfo.UseShellExecute = true;
                    proc.StartInfo.Verb = "runas";
                    proc.Start();
                    proc.WaitForExit();
                    return null;
                }
                else
                {
                    proc.StartInfo.UseShellExecute = false;
                    proc.StartInfo.RedirectStandardOutput = true;
                    proc.StartInfo.RedirectStandardError = true;
                    proc.Start();

                    string res = proc.StandardOutput.ReadToEnd();
                    proc.StandardOutput.Close();
                    proc.Close();
                    return res;
                }
            }
        }
        private void Log(string text)
        {
            if (txtLog.InvokeRequired)
            {
                txtLog.Invoke(new Action<string>(Log), text);
                return;
            }
            txtLog.AppendText(DateTime.Now.ToShortDateString() + " " + DateTime.Now.ToShortTimeString() + " : " + text + "\r\n");
        }

        private void cbPrivate_CheckedChanged(object sender, EventArgs e)
        {
            if (cbPrivate.Checked == true)
            {
                txtUser.Enabled = true;
                txtPassword.Enabled = true;
                _currentSettings.IsPrivateSession = true;
            }
            else
            {
                txtUser.Enabled = false;
                txtPassword.Enabled = false;
                _currentSettings.IsPrivateSession = false;
            }
        }

        private void cbCaptureMouse_CheckedChanged(object sender, EventArgs e)
        {
            if (cbCaptureMouse.Checked)
            {
                _currentSettings.IsShowMouseEnabled = true;
            }
            else
            {
                _currentSettings.IsShowMouseEnabled = false;
            }
        }

        private void txtLog_TextChanged(object sender, EventArgs e)
        {
            txtLog.SelectionStart = txtLog.Text.Length;
            txtLog.ScrollToCaret();
        }

        private void frmMain_Load(object sender, EventArgs e)
        {
            _ips = GetAllIPv4Addresses();
            foreach (var ip in _ips)
            {
                comboIPs.Items.Add(ip.Item2 + " - " + ip.Item1);
            }
            comboIPs.SelectedIndex = comboIPs.Items.Count - 1;
            var recommendedIP = _ips.FirstOrDefault(ip => ip.Item2.StartsWith("192.") || ip.Item2.StartsWith("10."));
            if (recommendedIP != null)
            {
                this.comboIPs.SelectedIndex = _ips.IndexOf(recommendedIP);
            }

            try
            {
                if (File.Exists(Constants.AppSettingsFileName))
                {
                    var serializer = new XmlSerializer(typeof(AppSettings));
                    using (var appSettingsFile = File.OpenRead(Constants.AppSettingsFileName))
                    {
                        _currentSettings = (AppSettings)serializer.Deserialize(appSettingsFile);
                    }

                    this.cbPrivate.Checked = _currentSettings.IsPrivateSession;
                    this.cbCaptureMouse.Checked = _currentSettings.IsShowMouseEnabled;
                    this.cbAutoStart.Checked = _currentSettings.IsAutoStartServerEnabled;
                    this.cbMiniStart.Checked = _currentSettings.IsStartMinimizedEnabled;
                    this.txtUser.Text = _currentSettings.Username;
                    this.txtPassword.Text = _currentSettings.Password;
                    this.numPort.Value = _currentSettings.Port;
                    this.numShotEvery.Value = Math.Max(numShotEvery.Minimum, Math.Min(numShotEvery.Maximum, _currentSettings.FrameRate));
                    this.cbShareSystemAudio.Checked = _currentSettings.ShareSystemAudio;
                    var hdrExposure = _currentSettings.HdrExposure;
                    var hdrPeak = _currentSettings.HdrPeakNits;
                    cbHdrCorrection.Checked = _currentSettings.HdrColorCorrection;
                    numHdrExposure.Value = (decimal)Math.Max(-2, Math.Min(2, hdrExposure));
                    numHdrPeak.Value = Math.Max(400, Math.Min(4000, hdrPeak));
                    this.qualitySlider.Value = _currentSettings.ImageQuality != default ? _currentSettings.ImageQuality : 75;
                    comboResolution.SelectedIndex = _currentSettings.OutputWidth >= 1900 ? 1 : _currentSettings.OutputWidth >= 1200 ? 2 : _currentSettings.OutputWidth >= 800 ? 3 : 0;
                    comboBitrate.SelectedIndex = _currentSettings.MaxBitrateMbps <= 0 ? 5 : _currentSettings.MaxBitrateMbps <= 2 ? 0 : _currentSettings.MaxBitrateMbps <= 4 ? 1 : _currentSettings.MaxBitrateMbps <= 8 ? 2 : _currentSettings.MaxBitrateMbps <= 12 ? 3 : 4;
                    if (!String.IsNullOrEmpty(_currentSettings.AudioDeviceId))
                        for (var i = 0; i < comboAudioDevice.Items.Count; i++)
                            if (comboAudioDevice.Items[i] is AudioDeviceChoice choice && choice.Id == _currentSettings.AudioDeviceId) comboAudioDevice.SelectedIndex = i;
                    comboAudioMode.SelectedIndex = _currentSettings.AudioMode == "include" ? 1 : _currentSettings.AudioMode == "exclude" ? 2 : 0;
                    RefreshAudioApplications();
                    lblQualityValue.Text = qualitySlider.Value + "%";
                    this.cbAllowPublicAccess.Checked = _currentSettings.AllowPublicAccess;
                    this.comboIPs.SelectedIndex = _ips.IndexOf(_ips.FirstOrDefault(ip => ip.Item2.Contains(_currentSettings.IP)));
                    if (_currentSettings.SelectedScreenIndex > -1 && comboScreens.Items.Count > 0 && _currentSettings.SelectedScreenIndex <= comboScreens.Items.Count - 1)
                        this.comboScreens.SelectedIndex = _currentSettings.SelectedScreenIndex;
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to load local appsettings.xml file.\r\n{ex.ToString()}", "ScreenTask", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }

            if (_currentSettings.IsStartMinimizedEnabled)
            {
                this.WindowState = FormWindowState.Minimized;
            }
            if (_currentSettings.IsAutoStartServerEnabled)
            {
                btnStartServer_Click(null, null);
            }


        }

        private void lblMe_Click(object sender, EventArgs e)
        {
            Process.Start(Constants.WebsiteUrl);

        }

        private void frmMain_FormClosing(object sender, FormClosingEventArgs e)
        {
            updateCancellation?.Cancel();
            isWorking = false;
            Interlocked.Increment(ref _serverGeneration);
            StopCaptureProcess();
            _rtcSignaling.Dispose();
            _h264Stream.Dispose();
            fileRefreshTimer?.Dispose();
            _fileShares.Dispose();
            var audioStream = _audioStream;
            _audioStream = null;
            audioStream?.Dispose();
            if (serv.IsListening)
            {
                serv.Stop();
                serv.Close();
            }
            try
            {
                _currentSettings.Port = (int)numPort.Value;
                _currentSettings.Username = txtUser.Text;
                _currentSettings.Password = txtPassword.Text;
                _currentSettings.IsShowMouseEnabled = cbCaptureMouse.Checked;
                _currentSettings.IsPrivateSession = cbPrivate.Checked;
                _currentSettings.IsAutoStartServerEnabled = cbAutoStart.Checked;
                _currentSettings.IsStartMinimizedEnabled = cbMiniStart.Checked;
                _currentSettings.FrameRate = (int)numShotEvery.Value;
                _currentSettings.ShareSystemAudio = cbShareSystemAudio.Checked;
                _currentSettings.IP = _ips.ElementAt(comboIPs.SelectedIndex).Item2;
                _currentSettings.SelectedScreenIndex = comboScreens.SelectedIndex;
                _currentSettings.ImageQuality = qualitySlider.Value;
                _currentSettings.OutputWidth = GetSelectedWidth();
                _currentSettings.MaxBitrateMbps = GetSelectedBitrate();
                _currentSettings.AudioDeviceId = (comboAudioDevice.SelectedItem as AudioDeviceChoice)?.Id;
                SaveAudioSourceSettings();
                _currentSettings.AllowPublicAccess = cbAllowPublicAccess.Checked;

                using (var appSettingsFile = new FileStream(Constants.AppSettingsFileName, FileMode.Create, FileAccess.Write))
                {
                    var serializer = new XmlSerializer(typeof(AppSettings));
                    serializer.Serialize(appSettingsFile, _currentSettings);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Cannot save the settings file next to the executable file.\r\n{ex.ToString()}", "ScreenTask", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void btnLaunch_Click(object sender, EventArgs e)
        {
            if (txtURL.Text.StartsWith("http"))
                Process.Start(txtURL.Text);

        }

        private void appNotify_MouseDoubleClick(object sender, MouseEventArgs e)
        {
            this.WindowState = FormWindowState.Normal;
            this.Focus();
            this.ShowInTaskbar = true;
        }

        private void frmMain_Resize(object sender, EventArgs e)
        {
            if (this.WindowState == FormWindowState.Minimized)
            {
                this.ShowInTaskbar = false;
                appNotify.Visible = true;
                appNotify.ShowBalloonTip(1_000, "ScreenTask", "Running in the background", ToolTipIcon.Info);
            }
        }

        private void appNotify_Click(object sender, EventArgs e)
        {
            this.WindowState = FormWindowState.Normal;
            this.ShowInTaskbar = true;
            this.Focus();
        }

        private void qualitySlider_Scroll(object sender, EventArgs e)
        {
            _currentSettings.ImageQuality = qualitySlider.Value;
            if (lblQualityValue != null) lblQualityValue.Text = qualitySlider.Value + "%";

        }

        private int GetSelectedWidth()
        {
            return comboResolution == null || comboResolution.SelectedIndex == 0 ? 0 : new[] { 0, 1920, 1280, 854 }[comboResolution.SelectedIndex];
        }

        private int GetSelectedBitrate()
        {
            return comboBitrate == null || comboBitrate.SelectedIndex < 0 ? 2 : new[] { 2, 4, 8, 12, 20, 0 }[comboBitrate.SelectedIndex];
        }

        private void cbAllowPublicAccess_CheckedChanged(object sender, EventArgs e)
        {
            _currentSettings.AllowPublicAccess = cbAllowPublicAccess.Checked;
        }
    }
}
