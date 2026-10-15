using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace ScreenTask
{
    public partial class frmMain
    {
        private readonly FileShareHub _fileShares = new FileShareHub();
        private ListView fileList;
        private TextBox filePublicAddress;
        private Label fileTransferHint;
        private System.Windows.Forms.Timer fileRefreshTimer;
        private string fileSuggestedAddress;
        private bool _fileTransferOnly;
        private bool _startFileOnlyRequested;

        private void BuildFileTab(TabControl tabs)
        {
            var page = new TabPage("Files") { BackColor = Color.FromArgb(248, 250, 253), Padding = new Padding(12) };
            tabs.TabPages.Add(page);
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5 };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            page.Controls.Add(layout);
            layout.Controls.Add(new Label { Text = "Send large files straight from this PC. Your friend downloads in a browser. No app size cap; interrupted downloads can resume while the same link is shared.",
                AutoSize = true, MaximumSize = new Size(650, 0), Margin = new Padding(0, 0, 0, 10) }, 0, 0);
            var addressRow = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2 };
            addressRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); addressRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            addressRow.Controls.Add(new Label { Text = "Link address", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
            filePublicAddress = new TextBox { Dock = DockStyle.Fill, Margin = new Padding(8, 0, 0, 8) };
            addressRow.Controls.Add(filePublicAddress, 1, 0); layout.Controls.Add(addressRow, 0, 1);
            fileList = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, HideSelection = false, MultiSelect = true, AllowDrop = true };
            fileList.Columns.Add("File", 280); fileList.Columns.Add("Size", 90); fileList.Columns.Add("Status", 110); fileList.Columns.Add("Bytes sent", 110);
            fileList.DragEnter += (sender, e) => { if (e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effect = DragDropEffects.Copy; };
            fileList.DragDrop += (sender, e) => AddSharedFiles((string[])e.Data.GetData(DataFormats.FileDrop));
            layout.Controls.Add(fileList, 0, 2);
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = true, Margin = new Padding(0, 8, 0, 8) };
            Action<string, Action> addButton = (text, action) => {
                var button = new Button { Text = text, AutoSize = true, Margin = new Padding(0, 0, 6, 4) };
                button.Click += (sender, e) => action(); buttons.Controls.Add(button);
            };
            addButton("Start file server only", () => {
                if (isWorking) { fileTransferHint.Text = "The server is already running. Stop it first to switch to file-only mode."; return; }
                _startFileOnlyRequested = true;
                btnStartServer_Click(null, EventArgs.Empty);
            });
            addButton("Add files…", () => {
                using (var picker = new OpenFileDialog { Multiselect = true, Title = "Select files to share", CheckFileExists = true })
                    if (picker.ShowDialog(this) == DialogResult.OK) AddSharedFiles(picker.FileNames);
            });
            addButton("Copy link(s)", () => {
                try
                {
                    if (!isWorking) throw new InvalidOperationException("Start the server first. Screen capture is not required for file transfers.");
                    var selected = fileList.SelectedItems.Cast<ListViewItem>().ToArray();
                    if (selected.Length == 0) throw new InvalidOperationException("Select one or more files first.");
                    var origin = GetFileOrigin();
                    Clipboard.SetText(String.Join(Environment.NewLine, selected.Select(item => origin + "/files/" + (string)item.Tag)));
                    fileTransferHint.Text = "Links copied. Keep ScreenTask running and these files shared until the downloads finish.";
                }
                catch (Exception ex) { fileTransferHint.Text = ex.Message; }
            });
            addButton("Remove selected", () => {
                foreach (ListViewItem item in fileList.SelectedItems) _fileShares.Revoke((string)item.Tag);
                RefreshSharedFiles();
                fileTransferHint.Text = "Selected links removed and their active transfers cancelled. Original files remain on disk.";
            });
            addButton("Stop all sharing", () => { _fileShares.Clear(); RefreshSharedFiles(); fileTransferHint.Text = "All file links removed. Original files remain on disk."; });
            addButton("Open file page", () => {
                if (!isWorking) { fileTransferHint.Text = "Start the server first, then open a file page."; return; }
                var token = fileList.SelectedItems.Count == 1 ? "/" + (string)fileList.SelectedItems[0].Tag : "";
                try { Process.Start("http://localhost:" + numPort.Value.ToString(CultureInfo.InvariantCulture) + "/files" + token); }
                catch (Exception ex) { fileTransferHint.Text = ex.Message; }
            });
            layout.Controls.Add(buttons, 0, 3);
            fileTransferHint = new Label { Text = "Start the server, add files, then copy their links. Remote friends need your reachable public address and TCP port here, or your VPN/LAN address. Share links only with intended recipients.",
                AutoSize = true, MaximumSize = new Size(650, 0), ForeColor = Color.FromArgb(75, 85, 100) };
            layout.Controls.Add(fileTransferHint, 0, 4);
            fileRefreshTimer = new System.Windows.Forms.Timer { Interval = 750 };
            fileRefreshTimer.Tick += (sender, e) => RefreshSharedFiles(); fileRefreshTimer.Start();
        }

        private string GetFileOrigin()
        {
            Uri value;
            if (!Uri.TryCreate(filePublicAddress.Text.Trim(), UriKind.Absolute, out value) ||
                (value.Scheme != "http" && value.Scheme != "https") || !String.IsNullOrEmpty(value.UserInfo) ||
                value.AbsolutePath != "/" || !String.IsNullOrEmpty(value.Query) || !String.IsNullOrEmpty(value.Fragment))
                throw new InvalidOperationException("Enter a server address including http:// or https:// and its port, without a path. Example: http://your-address:7070");
            return value.GetLeftPart(UriPartial.Authority);
        }

        private void AddSharedFiles(IEnumerable<string> paths)
        {
            var added = 0; var errors = new List<string>();
            foreach (var path in paths)
            {
                try { _fileShares.AddFile(path); added++; }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is NotSupportedException)
                { errors.Add(Path.GetFileName(path) + ": " + ex.Message); }
            }
            RefreshSharedFiles();
            fileTransferHint.Text = errors.Count > 0 ? "Added " + added + ". " + String.Join("; ", errors.Take(3)) :
                "Added " + added + " file(s). Select them and copy the links. Removing a share revokes its link; files are not copied or deleted.";
        }

        private void RefreshSharedFiles()
        {
            if (fileList == null || fileList.IsDisposed) return;
            var files = _fileShares.GetFiles();
            var tokens = new HashSet<string>(files.Select(file => file.Token), StringComparer.Ordinal);
            var rows = fileList.Items.Cast<ListViewItem>().ToDictionary(row => (string)row.Tag, StringComparer.Ordinal);
            fileList.BeginUpdate();
            try
            {
                foreach (var item in fileList.Items.Cast<ListViewItem>().Where(item => !tokens.Contains((string)item.Tag)).ToArray()) fileList.Items.Remove(item);
                foreach (var file in files)
                {
                    ListViewItem item; rows.TryGetValue(file.Token, out item);
                    if (item == null) { item = new ListViewItem(new[] { file.Name, FormatFileSize(file.Length), "Ready", "0 B" }) { Tag = file.Token }; fileList.Items.Add(item); }
                    var active = Volatile.Read(ref file.ActiveDownloads);
                    item.SubItems[2].Text = active > 0 ? "Sending (" + active + ")" : isWorking ? "Ready" : "Server stopped";
                    item.SubItems[3].Text = FormatFileSize(Interlocked.Read(ref file.BytesSent));
                }
            }
            finally { fileList.EndUpdate(); }
        }

        private static string FormatFileSize(long bytes)
        {
            var units = new[] { "B", "KiB", "MiB", "GiB", "TiB", "PiB" }; double size = bytes; var index = 0;
            while (size >= 1024 && index < units.Length - 1) { size /= 1024; index++; }
            return size.ToString(index == 0 ? "0" : "0.##", CultureInfo.CurrentCulture) + " " + units[index];
        }
    }
}
