using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ScreenTask
{
    public partial class frmMain
    {
        private Label updateStatus;
        private ProgressBar updateProgress;
        private Button updateCheck, updateInstall, updateSource, updateCancel;
        private CancellationTokenSource updateCancellation;
        private bool updateAvailable;

        private void BuildUpdatesTab(TabControl tabs)
        {
            var page = new TabPage("Updates") { BackColor = Color.FromArgb(248, 250, 253), Padding = new Padding(16) };
            tabs.TabPages.Add(page);
            var layout = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true };
            page.Controls.Add(layout);
            layout.Controls.Add(new Label { Text = "ScreenTask " + currentVersion.ToString(3), AutoSize = true, Font = new Font(Font, FontStyle.Bold), Margin = new Padding(0, 0, 0, 12) });
            layout.Controls.Add(new Label { Text = "Get updates from snoopeh/ScreenTask on GitHub. Update now installs the latest Windows release and restarts ScreenTask. Active streams and file transfers will stop. Your saved settings stay in place.", AutoSize = true, MaximumSize = new Size(640, 0), Margin = new Padding(0, 0, 0, 12) });
            var row = new FlowLayoutPanel { AutoSize = true, MaximumSize = new Size(650, 0) };
            layout.Controls.Add(row);
            updateCheck = new Button { Text = "Check for updates", AutoSize = true };
            updateInstall = new Button { Text = "Update now", AutoSize = true, Enabled = false };
            updateCancel = new Button { Text = "Cancel download", AutoSize = true, Enabled = false };
            row.Controls.AddRange(new Control[] { updateCheck, updateInstall, updateCancel });
            updateProgress = new ProgressBar { Width = 610, Height = 20, Margin = new Padding(0, 12, 0, 8) };
            layout.Controls.Add(updateProgress);
            updateStatus = new Label { Text = "Check for a newer release when you are ready.", AutoSize = true, MaximumSize = new Size(640, 0), Margin = new Padding(0, 0, 0, 20) };
            layout.Controls.Add(updateStatus);
            layout.Controls.Add(new Label { Text = "Want the code? Download latest source saves the current main branch. Building source requires the .NET SDK; use Update now to run the app without building it.", AutoSize = true, MaximumSize = new Size(640, 0) });
            updateSource = new Button { Text = "Download latest source…", AutoSize = true, Margin = new Padding(0, 10, 0, 10) };
            layout.Controls.Add(updateSource);
            var github = new LinkLabel { Text = ReleaseUpdateClient.Repository, AutoSize = true };
            github.LinkClicked += (s, e) => Process.Start(ReleaseUpdateClient.Repository);
            layout.Controls.Add(github);
            updateCheck.Click += async (s, e) => await RunUpdateAction(async (client, token) => {
                updateStatus.Text = "Checking GitHub…";
                var release = await client.CheckAsync(token);
                updateAvailable = release.Version > currentVersion;
                updateStatus.Text = updateAvailable ? "Version " + release.Version.ToString(3) + " is available. Update now downloads and installs it." : "You are up to date (latest release: " + release.Version.ToString(3) + ").";
            });
            updateInstall.Click += async (s, e) => await RunUpdateAction(InstallLatestUpdate);
            updateSource.Click += async (s, e) => {
                using (var picker = new SaveFileDialog { Title = "Save latest ScreenTask source", FileName = "ScreenTask-main-source.zip", Filter = "Source archive (*.zip)|*.zip" })
                {
                    if (picker.ShowDialog(this) != DialogResult.OK) return;
                    await RunUpdateAction(async (client, token) => {
                        var partial = picker.FileName + ".partial-" + Guid.NewGuid().ToString("N");
                        updateStatus.Text = "Downloading latest main branch source…";
                        try
                        {
                            await client.DownloadAsync(ReleaseUpdateClient.SourceUrl, partial, 0, null, new Progress<int>(p => updateProgress.Value = p), token);
                            if (File.Exists(picker.FileName)) File.Replace(partial, picker.FileName, null);
                            else File.Move(partial, picker.FileName);
                            updateStatus.Text = "Source saved to " + picker.FileName;
                        }
                        finally { if (File.Exists(partial)) File.Delete(partial); }
                    });
                }
            };
            updateCancel.Click += (s, e) => updateCancellation?.Cancel();
        }

        private async Task RunUpdateAction(Func<ReleaseUpdateClient, CancellationToken, Task> action)
        {
            if (updateCancellation != null) return;
            using (var cancellation = new CancellationTokenSource())
            using (var client = new ReleaseUpdateClient())
            {
                updateCancellation = cancellation;
                updateCheck.Enabled = updateInstall.Enabled = updateSource.Enabled = false;
                updateCancel.Enabled = true;
                updateProgress.Value = 0;
                try { await action(client, cancellation.Token); }
                catch (OperationCanceledException) { if (!IsDisposed) updateStatus.Text = "Download cancelled. Your installation has not changed."; }
                catch (Exception ex) { if (!IsDisposed) updateStatus.Text = "Update: " + ex.Message; }
                finally
                {
                    updateCancellation = null;
                    if (!IsDisposed)
                    {
                        updateCheck.Enabled = updateSource.Enabled = true;
                        updateInstall.Enabled = updateAvailable;
                        updateCancel.Enabled = false;
                    }
                }
            }
        }

        private async Task InstallLatestUpdate(ReleaseUpdateClient client, CancellationToken cancellation)
        {
            updateStatus.Text = "Checking latest release…";
            var release = await client.CheckAsync(cancellation);
            if (release.Version <= currentVersion) { updateAvailable = false; updateStatus.Text = "You are already up to date."; return; }
            var install = AppDomain.CurrentDomain.BaseDirectory;
            UpdateInstaller.NoLinks(install);
            var probe = Path.Combine(install, ".update-write-test-" + Guid.NewGuid().ToString("N"));
            try { using (File.Create(probe)) { } }
            catch (UnauthorizedAccessException) { throw new IOException("Move ScreenTask to a writable folder before updating, such as Documents or Downloads."); }
            finally { if (File.Exists(probe)) File.Delete(probe); }
            var temp = Path.Combine(Path.GetTempPath(), "ScreenTask-update-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temp);
            var launched = false;
            try
            {
                var archive = Path.Combine(temp, ReleaseUpdateClient.AssetName);
                var stage = Path.Combine(temp, "stage");
                updateStatus.Text = "Downloading " + release.Version.ToString(3) + "…";
                await client.DownloadAsync(release.Package.browser_download_url, archive, release.Package.size, release.Package.digest, new Progress<int>(p => updateProgress.Value = p), cancellation);
                updateStatus.Text = "Verifying update files…";
                await Task.Run(() => UpdateInstaller.Extract(archive, stage), cancellation);
                var stagedVersion = AssemblyName.GetAssemblyName(Path.Combine(stage, "ScreenTask.exe")).Version;
                if (stagedVersion != release.Version) throw new InvalidDataException("The downloaded application version does not match its release.");
                cancellation.ThrowIfCancellationRequested();
                var helper = Path.Combine(temp, "ScreenTask.Updater.exe");
                File.Copy(Path.Combine(stage, "ScreenTask.Updater.exe"), helper);
                var eventName = "Local\\ScreenTask-update-" + Guid.NewGuid().ToString("N");
                using (var ready = new EventWaitHandle(false, EventResetMode.ManualReset, eventName))
                using (var updater = Process.Start(new ProcessStartInfo(helper) {
                    Arguments = "--apply \"" + install.TrimEnd('\\') + "\" \"" + stage + "\" " + Process.GetCurrentProcess().Id + " " + eventName,
                    UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = temp
                }))
                {
                    if (!await Task.Run(() => ready.WaitOne(15000))) throw new IOException("The updater could not start. ScreenTask is still running.");
                    launched = true;
                }
                updateCancel.Enabled = false;
                updateStatus.Text = "Installing update and restarting…";
                Close();
            }
            finally { if (!launched) Directory.Delete(temp, true); }
        }
    }
}
