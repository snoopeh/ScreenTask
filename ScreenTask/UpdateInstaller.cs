using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace ScreenTask
{
    // Shared with the small updater executable and the isolated filesystem tests.
    internal static class UpdateInstaller
    {
        internal static string Relative(string name)
        {
            name = name.Replace('\\', '/');
            var parts = name.Split('/');
            if (parts.Length == 0 || parts.Any(p => p.Length == 0 || p == "." || p == ".." ||
                p.EndsWith(".") || p.EndsWith(" ") || p.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                System.Text.RegularExpressions.Regex.IsMatch(p, "^(CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])($|\\.)", System.Text.RegularExpressions.RegexOptions.IgnoreCase)))
                throw new InvalidDataException("Unsafe package path: " + name);
            // Only application files belong in a release; never overwrite settings or user data.
            var allowed = parts.Length == 1 && (name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ||
                name == "ScreenTask.exe" || name == "ScreenTask.exe.config" || name == "ScreenTask.Updater.exe" ||
                name == "ScreenTask.Updater.exe.config" || name == "ScreenTask.AudioCaptureHelper.exe" ||
                name == "ffmpeg.exe" || name == "README.md" || name == "COPYING" || name == "FFMPEG-LICENSE.txt" || name == "THIRD-PARTY.md") ||
                parts.Length == 2 && parts[0] == "WebServer" && new[] { ".html", ".js", ".css", ".png", ".ico" }.Contains(Path.GetExtension(parts[1]).ToLowerInvariant());
            if (!allowed) throw new InvalidDataException("Unexpected package file: " + name);
            return Path.Combine(parts);
        }

        internal static void NoLinks(string path)
        {
            for (var item = new DirectoryInfo(Path.GetFullPath(path)); item != null; item = item.Parent)
                if (item.Exists && (item.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Updates cannot be installed through a linked directory: " + item.FullName);
        }

        internal static void Extract(string archive, string stage)
        {
            if (Directory.Exists(stage)) throw new IOException("Update staging directory already exists.");
            NoLinks(Path.GetDirectoryName(stage));
            Directory.CreateDirectory(stage);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long size = 0;
            using (var zip = ZipFile.OpenRead(archive))
            {
                if (zip.Entries.Count > 200) throw new InvalidDataException("Too many files in update package.");
                foreach (var entry in zip.Entries)
                {
                    if (entry.FullName.EndsWith("/") && entry.Length == 0)
                    {
                        if (entry.FullName != "WebServer/") throw new InvalidDataException("Unexpected package directory.");
                        continue;
                    }
                    var name = Relative(entry.FullName);
                    if (!seen.Add(name) || ((entry.ExternalAttributes >> 16) & 0xf000) == 0xa000)
                        throw new InvalidDataException("Duplicate or linked package file.");
                    size = checked(size + entry.Length);
                    if (size > 1024L * 1024 * 1024) throw new InvalidDataException("Unpacked update exceeds size limit.");
                    var destination = Path.Combine(stage, name);
                    Directory.CreateDirectory(Path.GetDirectoryName(destination));
                    entry.ExtractToFile(destination);
                }
            }
            foreach (var required in new[] { "ScreenTask.exe", "ScreenTask.Updater.exe", "ScreenTask.AudioCaptureHelper.exe", "ffmpeg.exe", "WebServer/index.html" })
                if (!File.Exists(Path.Combine(stage, required))) throw new InvalidDataException("Update is missing " + required);
        }

        internal static void Apply(string stage, string install, Action<int> afterCopy = null)
        {
            stage = Path.GetFullPath(stage);
            install = Path.GetFullPath(install).TrimEnd(Path.DirectorySeparatorChar);
            NoLinks(stage); NoLinks(install);
            if (!Directory.Exists(install) || stage.StartsWith(install + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Invalid update directories.");
            var files = Directory.GetFiles(stage, "*", SearchOption.AllDirectories).Select(file => Relative(file.Substring(stage.Length).TrimStart(Path.DirectorySeparatorChar))).ToArray();
            var backup = Path.Combine(install, ".screentask-backup-" + Guid.NewGuid().ToString("N"));
            var changed = new List<Tuple<string, bool>>();
            Directory.CreateDirectory(backup);
            try
            {
                foreach (var relative in files)
                {
                    var source = Path.Combine(stage, relative);
                    var target = Path.Combine(install, relative);
                    NoLinks(Path.GetDirectoryName(source)); NoLinks(Path.GetDirectoryName(target));
                    if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0 || File.Exists(target) && (File.GetAttributes(target) & FileAttributes.ReparsePoint) != 0)
                        throw new IOException("Linked application files cannot be updated.");
                    var existed = File.Exists(target);
                    if (existed)
                    {
                        var saved = Path.Combine(backup, relative);
                        Directory.CreateDirectory(Path.GetDirectoryName(saved));
                        File.Copy(target, saved);
                    }
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    changed.Add(Tuple.Create(relative, existed));
                    File.Copy(source, target, true);
                    afterCopy?.Invoke(changed.Count);
                }
            }
            catch (Exception original)
            {
                var failures = new List<Exception> { original };
                foreach (var change in changed.AsEnumerable().Reverse())
                    try
                    {
                        var target = Path.Combine(install, change.Item1);
                        if (change.Item2) File.Copy(Path.Combine(backup, change.Item1), target, true);
                        else File.Delete(target);
                    }
                    catch (Exception rollback) { failures.Add(rollback); }
                if (failures.Count > 1) throw new AggregateException("Update failed; recovery copies are in " + backup, failures);
                throw;
            }
            // Retain the previous application files for manual recovery.
        }
    }
}
