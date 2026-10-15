using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace ScreenTask
{
    internal sealed class ReleaseUpdateClient : IDisposable
    {
        internal const string Repository = "https://github.com/snoopeh/ScreenTask";
        internal const string SourceUrl = Repository + "/archive/refs/heads/main.zip";
        internal const string AssetName = "ScreenTask-win-x64.zip";
        private readonly HttpClient http;

        internal sealed class Asset
        {
            public string name { get; set; }
            public string browser_download_url { get; set; }
            public string digest { get; set; }
            public long size { get; set; }
        }

        internal sealed class Release
        {
            public string tag_name { get; set; }
            public bool draft { get; set; }
            public bool prerelease { get; set; }
            public List<Asset> assets { get; set; }
            internal Version Version;
            internal Asset Package;
        }

        internal ReleaseUpdateClient()
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            http = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("ScreenTask-Updater/1.10");
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        }

        internal static Release ParseRelease(string json)
        {
            var release = new JavaScriptSerializer().Deserialize<Release>(json);
            Version version;
            if (release == null || release.draft || release.prerelease ||
                !Version.TryParse((release.tag_name ?? "").TrimStart('v'), out version))
                throw new InvalidDataException("GitHub returned an invalid stable release.");
            release.Version = new Version(version.Major, version.Minor, Math.Max(0, version.Build), Math.Max(0, version.Revision));
            release.Package = (release.assets ?? new List<Asset>()).SingleOrDefault(a => a.name == AssetName);
            if (release.Package == null) throw new InvalidDataException("The latest release has no Windows download yet. Try again after its build finishes.");
            var asset = release.Package;
            var expected = Repository + "/releases/download/" + release.tag_name + "/" + AssetName;
            if (asset.browser_download_url != expected || asset.size <= 0 || asset.size > 512L * 1024 * 1024 ||
                asset.digest == null || !System.Text.RegularExpressions.Regex.IsMatch(asset.digest, "^sha256:[0-9a-fA-F]{64}$"))
                throw new InvalidDataException("The release download or its SHA-256 checksum is invalid.");
            return release;
        }

        internal async Task<Release> CheckAsync(CancellationToken cancellation)
        {
            using (var response = await http.GetAsync("https://api.github.com/repos/snoopeh/ScreenTask/releases/latest", cancellation))
            {
                if (response.StatusCode == HttpStatusCode.NotFound)
                    throw new InvalidOperationException("No published release is available yet.");
                if ((int)response.StatusCode == 403 || (int)response.StatusCode == 429)
                    throw new InvalidOperationException("GitHub's update request limit was reached. Try again later or open GitHub.");
                response.EnsureSuccessStatusCode();
                return ParseRelease(await response.Content.ReadAsStringAsync());
            }
        }

        internal async Task DownloadAsync(string url, string destination, long expectedSize, string digest, IProgress<int> progress, CancellationToken cancellation)
        {
            if (url != SourceUrl && !url.StartsWith(Repository + "/releases/download/", StringComparison.Ordinal))
                throw new InvalidDataException("Download must come from the ScreenTask repository.");
            try
            {
                using (var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellation))
                {
                    response.EnsureSuccessStatusCode();
                    var total = response.Content.Headers.ContentLength ?? expectedSize;
                    if (total > 512L * 1024 * 1024) throw new InvalidDataException("Download exceeds the package size limit.");
                    using (var input = await response.Content.ReadAsStreamAsync())
                    using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
                    using (var hash = SHA256.Create())
                    {
                        var buffer = new byte[81920];
                        long received = 0;
                        int count;
                        while ((count = await input.ReadAsync(buffer, 0, buffer.Length, cancellation)) != 0)
                        {
                            received += count;
                            if (received > 512L * 1024 * 1024) throw new InvalidDataException("Download exceeds the package size limit.");
                            hash.TransformBlock(buffer, 0, count, buffer, 0);
                            await output.WriteAsync(buffer, 0, count, cancellation);
                            progress?.Report(total > 0 ? (int)Math.Min(100, received * 100 / total) : 0);
                        }
                        hash.TransformFinalBlock(new byte[0], 0, 0);
                        if (expectedSize > 0 && received != expectedSize) throw new InvalidDataException("The download was incomplete.");
                        if (digest != null && !String.Equals("sha256:" + BitConverter.ToString(hash.Hash).Replace("-", ""), digest, StringComparison.OrdinalIgnoreCase))
                            throw new InvalidDataException("Download checksum failed. The current installation has not been changed.");
                    }
                }
            }
            catch { if (File.Exists(destination)) File.Delete(destination); throw; }
        }

        public void Dispose() { http.Dispose(); }
    }
}
