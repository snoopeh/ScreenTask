using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ScreenTask
{
    public partial class frmMain
    {
        private bool _settingsReady;
        private string _preferencesPath = Constants.AppSettingsFileName;
        private Func<int, Task> _configureNetwork;
        private int _captureSettingsVersion;
        private volatile bool _captureStopRequested;
        private Task _legacyCaptureTask;
        private bool _applyingLiveSettings;
        private System.Windows.Forms.Timer settingsTimer, hdrProfileTimer;
        private ComboBox hdrMode;
        private TableLayoutPanel hdrManualControls;
        private Label hdrCalibrationStatus;
        private CheckBox fileOnlyCheck;
        private string _appliedCapture, _appliedAudio, _appliedNetwork;
        private string _viewerOrigin;
        private int _audioGeneration;
        private readonly SemaphoreSlim _audioRestartGate = new SemaphoreSlim(1, 1);

        private void InitializeLiveSettings()
        {
            _configureNetwork = AddFirewallRule;
            settingsTimer = new System.Windows.Forms.Timer { Interval = 450 };
            settingsTimer.Tick += async (s, e) => { settingsTimer.Stop(); await ApplyLiveSettingsAsync(); };
            hdrProfileTimer = new System.Windows.Forms.Timer { Interval = 3000 };
            hdrProfileTimer.Tick += (s, e) => { if (_settingsReady && hdrMode.SelectedIndex == 0 && RefreshHdrProfile()) QueueSettingsChange(); };
            hdrProfileTimer.Start();
            foreach (var control in new Control[] { comboIPs, numPort, cbAllowPublicAccess, comboScreens, numShotEvery, comboResolution, comboBitrate,
                qualitySlider, cbCaptureMouse, cbShareSystemAudio, comboAudioDevice, comboAudioMode, comboAudioApplication,
                cbPrivate, txtUser, txtPassword, cbAutoStart, cbMiniStart, fileOnlyCheck })
            {
                if (control is ComboBox combo) combo.SelectedIndexChanged += (s, e) => QueueSettingsChange();
                else if (control is NumericUpDown number) number.ValueChanged += (s, e) => QueueSettingsChange();
                else if (control is CheckBox check) check.CheckedChanged += (s, e) => QueueSettingsChange();
                else if (control is TextBox text) text.TextChanged += (s, e) => QueueSettingsChange();
                else if (control is TrackBar slider) slider.ValueChanged += (s, e) => QueueSettingsChange();
            }
        }

        private void QueueSettingsChange()
        {
            if (!_settingsReady || IsDisposed) return;
            settingsTimer.Stop(); settingsTimer.Start();
        }

        private bool RefreshHdrProfile()
        {
            if (hdrMode.SelectedIndex != 0 || comboScreens.SelectedIndex < 0) return false;
            var profile = HdrDisplayProbe.Read(Screen.AllScreens[comboScreens.SelectedIndex].DeviceName);
            var changed = _currentSettings.HdrColorCorrection != profile.Enabled || _currentSettings.HdrPeakNits != profile.PeakNits || _currentSettings.HdrExposure != 0;
            _currentSettings.HdrColorCorrection = profile.Enabled;
            _currentSettings.HdrPeakNits = profile.PeakNits;
            _currentSettings.HdrExposure = 0;
            hdrCalibrationStatus.Text = !profile.Known ? "Windows HDR detection is unavailable. SDR is active; Manual remains available if needed." : profile.Enabled
                ? "HDR detected. Colors calibrated from the Windows display profile (" + profile.PeakNits + " nits)."
                : "SDR display detected. Original SDR colors are preserved. Turn on HDR in Windows to calibrate automatically.";
            return changed;
        }

        private void ReadControlsIntoSettings()
        {
            _currentSettings.Port = (int)numPort.Value;
            if (_ips != null && comboIPs.SelectedIndex >= 0 && comboIPs.SelectedIndex < _ips.Count) _currentSettings.IP = _ips[comboIPs.SelectedIndex].Item2;
            _currentSettings.Username = txtUser.Text; _currentSettings.Password = txtPassword.Text;
            _currentSettings.IsShowMouseEnabled = cbCaptureMouse.Checked;
            _currentSettings.IsPrivateSession = cbPrivate.Checked;
            _currentSettings.IsAutoStartServerEnabled = cbAutoStart.Checked;
            _currentSettings.IsStartMinimizedEnabled = cbMiniStart.Checked;
            _currentSettings.FrameRate = _requestedFrameRate = (int)numShotEvery.Value;
            _currentSettings.SelectedScreenIndex = Math.Max(0, comboScreens.SelectedIndex);
            _currentSettings.ImageQuality = qualitySlider.Value;
            _currentSettings.OutputWidth = GetSelectedWidth(); _currentSettings.MaxBitrateMbps = GetSelectedBitrate();
            _currentSettings.ShareSystemAudio = cbShareSystemAudio.Checked;
            _currentSettings.AudioDeviceId = (comboAudioDevice.SelectedItem as AudioDeviceChoice)?.Id;
            SaveAudioSourceSettings();
            _currentSettings.AllowPublicAccess = cbAllowPublicAccess.Checked;
            _currentSettings.FileTransferOnly = fileOnlyCheck.Checked;
            _currentSettings.AutomaticHdr = hdrMode.SelectedIndex == 0;
            _currentSettings.ManualHdrExposure = (double)numHdrExposure.Value;
            _currentSettings.ManualHdrPeakNits = (int)numHdrPeak.Value;
            if (_currentSettings.AutomaticHdr) RefreshHdrProfile();
            else
            {
                _currentSettings.HdrColorCorrection = hdrMode.SelectedIndex == 2;
                _currentSettings.HdrExposure = _currentSettings.ManualHdrExposure;
                _currentSettings.HdrPeakNits = _currentSettings.ManualHdrPeakNits;
            }
        }

        private string CaptureSettingsKey() => String.Join("|", _currentSettings.FrameRate, _currentSettings.SelectedScreenIndex, _currentSettings.OutputWidth,
            _currentSettings.ImageQuality, _currentSettings.MaxBitrateMbps, _currentSettings.IsShowMouseEnabled, _currentSettings.HdrColorCorrection,
            _currentSettings.HdrPeakNits, _currentSettings.HdrExposure, _fileTransferOnly);
        private string AudioSettingsKey() => String.Join("|", _currentSettings.ShareSystemAudio, _currentSettings.AudioDeviceId,
            _currentSettings.AudioMode, _currentSettings.AudioProcessId, _fileTransferOnly);
        private string NetworkSettingsKey() => String.Join("|", _currentSettings.IP, _currentSettings.Port, _currentSettings.AllowPublicAccess);
        private void RememberAppliedSettings() { _appliedCapture = CaptureSettingsKey(); _appliedAudio = AudioSettingsKey(); _appliedNetwork = NetworkSettingsKey(); }

        private async Task ApplyLiveSettingsAsync()
        {
            if (!_settingsReady || _applyingLiveSettings) { QueueSettingsChange(); return; }
            _applyingLiveSettings = true;
            try
            {
                ReadControlsIntoSettings();
                try { SettingsStore.Save(_preferencesPath, _currentSettings); }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                { Log("Could not save preferences; live changes still apply: " + ex.Message); }
                if (!isWorking) return;
                var oldFileOnly = _fileTransferOnly;
                _fileTransferOnly = _currentSettings.FileTransferOnly;
                if (_appliedCapture != CaptureSettingsKey())
                {
                    _appliedCapture = CaptureSettingsKey();
                    Interlocked.Increment(ref _captureSettingsVersion);
                    if (_fileTransferOnly) _captureStopRequested = true;
                    StopCaptureProcess();
                    if (_fileTransferOnly)
                    {
                        _h264Stream.FailStream("File-only mode is active.");
                        _rtcSignaling.Reset();
                    }
                    else if (oldFileOnly)
                    {
                        if (_legacyCaptureTask != null) await _legacyCaptureTask;
                        _captureStopRequested = false;
                        Interlocked.Exchange(ref _legacyCaptureStarted, 0);
                    }
                }
                if (_appliedAudio != AudioSettingsKey())
                {
                    _appliedAudio = AudioSettingsKey();
                    var generation = Interlocked.Increment(ref _audioGeneration);
                    var previous = Interlocked.Exchange(ref _audioStream, null);
                    previous?.Dispose();
                    if (_currentSettings.ShareSystemAudio && !_fileTransferOnly)
                        _ = Task.Run(async () => {
                            await _audioRestartGate.WaitAsync();
                            try { if (generation == Volatile.Read(ref _audioGeneration)) { ValidateAudioSourceSettings(); StartAudioCapture(); } }
                            catch (Exception ex) { Log("Audio settings: " + ex.Message); }
                            finally { _audioRestartGate.Release(); }
                        });
                }
                btnDirectHost.Enabled = !_fileTransferOnly;
                if (_appliedNetwork != NetworkSettingsKey())
                {
                    var key = NetworkSettingsKey();
                    var origin = "http://" + _currentSettings.IP + ":" + _currentSettings.Port;
                    var desired = new[] { "http://localhost:" + _currentSettings.Port + "/", _currentSettings.AllowPublicAccess ? "http://+:" + _currentSettings.Port + "/" : origin + "/" }.Distinct().ToArray();
                    if (_configureNetwork != null) await _configureNetwork(_currentSettings.Port);
                    else await AddFirewallRule(_currentSettings.Port);
                    if (!isWorking || !serv.IsListening) return;
                    var previous = serv.Prefixes.ToArray();
                    try { foreach (var prefix in desired.Except(previous)) serv.Prefixes.Add(prefix); }
                    catch { foreach (var prefix in desired.Except(previous)) serv.Prefixes.Remove(prefix); throw; }
                    foreach (var prefix in previous.Except(desired)) serv.Prefixes.Remove(prefix);
                    _viewerOrigin = origin; txtURL.Text = origin; _appliedNetwork = key;
                    Log("Listening address updated: " + origin + ". Existing invitation addresses may need a new link.");
                }
            }
            catch (Exception ex) { Log("Could not apply settings: " + ex.Message); }
            finally { _applyingLiveSettings = false; }
        }
    }
}
