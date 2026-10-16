using System.Collections.Generic;
namespace ScreenTask
{
    public partial class frmMain
    {
        private readonly FileShareHub _fileShares = new FileShareHub();
        private readonly BrowserFileHub _browserFiles = new BrowserFileHub();
        private bool _fileTransferOnly;
        // Retains compatibility with existing registered HTTP file links.
        private void AddSharedFiles(IEnumerable<string> paths)
        {
            foreach (var path in paths) _fileShares.AddFile(path);
        }
    }
}
