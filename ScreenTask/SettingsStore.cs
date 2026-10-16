using System;
using System.IO;
using System.Xml.Serialization;

namespace ScreenTask
{
    internal static class SettingsStore
    {
        internal static AppSettings Load(string path, string legacyPath)
        {
            var source = File.Exists(path) ? path : legacyPath;
            if (!File.Exists(source)) return new AppSettings();
            AppSettings settings;
            try { using (var stream = File.OpenRead(source)) settings = (AppSettings)new XmlSerializer(typeof(AppSettings)).Deserialize(stream); }
            catch (InvalidOperationException) when (source == path && File.Exists(path + ".bak"))
            {
                using (var stream = File.OpenRead(path + ".bak")) settings = (AppSettings)new XmlSerializer(typeof(AppSettings)).Deserialize(stream);
                File.Move(path, path + ".corrupt-" + Guid.NewGuid().ToString("N"));
                Save(path, settings);
            }
            if (source != path)
            {
                settings.ManualHdrExposure = settings.HdrExposure;
                settings.ManualHdrPeakNits = Math.Max(400, Math.Min(4000, settings.HdrPeakNits));
                Save(path, settings);
            }
            return settings;
        }

        internal static void Save(string path, AppSettings settings)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    new XmlSerializer(typeof(AppSettings)).Serialize(stream, settings);
                    stream.Flush(true);
                }
                if (File.Exists(path)) File.Replace(temporary, path, path + ".bak");
                else File.Move(temporary, path);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }
}
