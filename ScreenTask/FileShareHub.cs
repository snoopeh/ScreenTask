using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ScreenTask
{
    // Local file selection is the only registration API. Network requests can
    // retrieve a registered token, never choose a filesystem path or enumerate shares.
    internal sealed class FileShareHub : IDisposable
    {
        private readonly ConcurrentDictionary<string, SharedFile> _files = new ConcurrentDictionary<string, SharedFile>(StringComparer.Ordinal);
        private readonly SemaphoreSlim _slots = new SemaphoreSlim(4, 4);
        private volatile bool _disposed;
        private volatile bool _online;

        public SharedFile AddFile(string path)
        {
            if (_disposed) throw new ObjectDisposedException("File sharing");
            var info = new FileInfo(Path.GetFullPath(path));
            if (!info.Exists) throw new FileNotFoundException("Select an existing file.");
            if ((info.Attributes & FileAttributes.Directory) != 0) throw new IOException("Select files, rather than folders. ZIP a folder first.");
            var random = new byte[24];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(random);
            var file = new SharedFile(info, BitConverter.ToString(random).Replace("-", "").ToLowerInvariant());
            if (!_files.TryAdd(file.Token, file)) throw new IOException("Could not create a unique share link. Try again.");
            return file;
        }

        public SharedFile[] GetFiles() { return _files.Values.OrderBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase).ToArray(); }

        public void Revoke(string token)
        {
            SharedFile file;
            if (!_files.TryRemove(token, out file)) return;
            lock (file.Sync) file.Revoked = true;
            AbortDownloads(file);
        }

        public void Clear() { foreach (var token in _files.Keys) Revoke(token); }
        public void StartServing() { if (!_disposed) _online = true; }
        public void CancelDownloads() { _online = false; foreach (var file in _files.Values) AbortDownloads(file); }
        private static void AbortDownloads(SharedFile file)
        {
            HttpListenerResponse[] responses;
            lock (file.Sync) responses = file.Responses.ToArray();
            foreach (var response in responses) { try { response.Abort(); } catch (Exception ex) when (ex is ObjectDisposedException || ex is HttpListenerException) { } }
        }

        public static bool IsRoute(string path)
        {
            return path.StartsWith("/download/", StringComparison.Ordinal) || path.StartsWith("/file-info/", StringComparison.Ordinal);
        }

        public async Task HandleAsync(HttpListenerContext context)
        {
            var response = context.Response;
            SharedFile file = null;
            var slot = false;
            var registered = false;
            var headersSent = false;
            try
            {
                response.Headers["Cache-Control"] = "private, no-store";
                response.Headers["X-Content-Type-Options"] = "nosniff";
                response.Headers["Referrer-Policy"] = "no-referrer";
                if (!_online) { await Reply(context, 503, "The file server is stopped. Ask the sender to restart it."); return; }
                if (context.Request.HttpMethod != "GET" && context.Request.HttpMethod != "HEAD")
                {
                    response.Headers["Allow"] = "GET, HEAD";
                    await Reply(context, 405, "This link supports downloads only."); return;
                }
                var path = context.Request.Url.LocalPath;
                var token = path.Substring(path.LastIndexOf('/') + 1);
                var download = path == "/download/" + token;
                if (_disposed || (!download && path != "/file-info/" + token) || !IsToken(token) || !_files.TryGetValue(token, out file))
                { await Reply(context, 404, "This file link is invalid or no longer shared."); return; }

                // Hold a read-only handle for the transfer. Writers/deletion are
                // excluded while sending so resumed requests cannot mix versions.
                using (var input = new FileStream(file.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 256 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    if (input.Length != file.Length || File.GetLastWriteTimeUtc(file.Path).Ticks != file.LastWriteUtc.Ticks)
                    { await Reply(context, 409, "The source file changed. Ask the sender for a new share link."); return; }
                    lock (file.Sync)
                    {
                        if (file.Revoked || !_online) throw new FileNotFoundException();
                        file.Responses.Add(response); registered = true;
                    }
                    if (!download)
                    {
                        await Reply(context, 200, "{\"name\":" + Quote(file.Name) + ",\"size\":\"" + file.Length.ToString(CultureInfo.InvariantCulture) + "\",\"download\":\"/download/" + file.Token + "\"}", true);
                        return;
                    }

                    response.Headers["Accept-Ranges"] = "bytes";
                    response.Headers["ETag"] = file.ETag;
                    response.Headers["Last-Modified"] = file.LastWriteUtc.ToString("R", CultureInfo.InvariantCulture);
                    if (!String.IsNullOrEmpty(context.Request.Headers["If-Match"]) && !Matches(context.Request.Headers["If-Match"], file.ETag))
                    { await Reply(context, 412, "The file version does not match."); return; }
                    if (Matches(context.Request.Headers["If-None-Match"], file.ETag, true)) { response.StatusCode = 304; return; }
                    long first = 0, last = file.Length - 1;
                    var range = context.Request.Headers["Range"];
                    var ifRange = context.Request.Headers["If-Range"];
                    if (context.Request.HttpMethod == "HEAD") range = null;
                    if (!String.IsNullOrEmpty(range) && !String.IsNullOrEmpty(ifRange) && !ValidIfRange(ifRange, file)) range = null;
                    if (!String.IsNullOrEmpty(range) && !TryRange(range, file.Length, out first, out last))
                    {
                        response.Headers["Content-Range"] = "bytes */" + file.Length.ToString(CultureInfo.InvariantCulture);
                        await Reply(context, 416, "Requested byte range is not available."); return;
                    }
                    if (context.Request.HttpMethod != "HEAD")
                    {
                        slot = _slots.Wait(0);
                        if (!slot) { response.Headers["Retry-After"] = "5"; await Reply(context, 429, "Four downloads are active. Retry in a few seconds."); return; }
                    }
                    response.StatusCode = String.IsNullOrEmpty(range) ? 200 : 206;
                    if (response.StatusCode == 206)
                        response.Headers["Content-Range"] = String.Format(CultureInfo.InvariantCulture, "bytes {0}-{1}/{2}", first, last, file.Length);
                    response.ContentType = "application/octet-stream";
                    response.Headers["Content-Disposition"] = "attachment; filename=\"" + AsciiFilename(file.Name) + "\"; filename*=UTF-8''" + Uri.EscapeDataString(file.Name);
                    var remaining = file.Length == 0 ? 0 : last - first + 1;
                    response.ContentLength64 = remaining;
                    response.SendChunked = false;
                    if (context.Request.HttpMethod == "HEAD") return;
                    input.Seek(first, SeekOrigin.Begin);
                    var buffer = new byte[256 * 1024];
                    Interlocked.Increment(ref file.ActiveDownloads);
                    try
                    {
                        headersSent = true;
                        while (remaining > 0)
                        {
                            lock (file.Sync) { if (file.Revoked) throw new IOException("Share removed."); }
                            var count = await input.ReadAsync(buffer, 0, (int)Math.Min(buffer.Length, remaining)).ConfigureAwait(false);
                            if (count == 0) throw new EndOfStreamException();
                            await response.OutputStream.WriteAsync(buffer, 0, count).ConfigureAwait(false);
                            remaining -= count;
                            Interlocked.Add(ref file.BytesSent, count);
                        }
                        await response.OutputStream.FlushAsync().ConfigureAwait(false);
                        if (first == 0 && last == file.Length - 1) Interlocked.Increment(ref file.CompletedDownloads);
                    }
                    finally { Interlocked.Decrement(ref file.ActiveDownloads); }
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is HttpListenerException || ex is ObjectDisposedException)
            {
                if (!headersSent)
                {
                    try { await Reply(context, ex is FileNotFoundException || ex is DirectoryNotFoundException ? 404 : 409, "This file is unavailable. Ask the sender to check or re-add it."); }
                    catch (Exception replyError) when (replyError is IOException || replyError is HttpListenerException || replyError is ObjectDisposedException) { }
                }
                else { try { response.Abort(); } catch (ObjectDisposedException) { } }
            }
            finally
            {
                if (registered) lock (file.Sync) file.Responses.Remove(response);
                if (slot) _slots.Release();
                try { response.Close(); } catch (Exception ex) when (ex is HttpListenerException || ex is ObjectDisposedException) { }
            }
        }

        private static bool IsToken(string token)
        {
            return token.Length == 48 && token.All(c => c >= '0' && c <= '9' || c >= 'a' && c <= 'f');
        }

        internal static bool TryRange(string range, long length, out long first, out long last)
        {
            first = 0; last = length - 1;
            if (length <= 0 || !range.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase)) return false;
            var parts = range.Substring(6).Split('-');
            if (parts.Length != 2 || range.Contains(",")) return false;
            long number;
            if (parts[0].Length == 0)
            {
                if (!Int64.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out number) || number <= 0) return false;
                first = Math.Max(0, length - number); return true;
            }
            if (!Int64.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out first) || first >= length) return false;
            if (parts[1].Length == 0) return true;
            if (!Int64.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out number) || number < first) return false;
            last = Math.Min(last, number); return true;
        }

        private static bool Matches(string header, string etag, bool weak = false)
        {
            return !String.IsNullOrWhiteSpace(header) && header.Split(',').Any(part => { var tag = part.Trim(); if (weak && tag.StartsWith("W/", StringComparison.Ordinal)) tag = tag.Substring(2); return tag == etag || tag == "*"; });
        }

        private static bool ValidIfRange(string value, SharedFile file)
        {
            if (value == file.ETag) return true;
            DateTime date;
            return DateTime.TryParseExact(value, "R", CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out date) &&
                file.LastWriteUtc.Ticks / TimeSpan.TicksPerSecond <= date.Ticks / TimeSpan.TicksPerSecond;
        }

        private static async Task Reply(HttpListenerContext context, int code, string text, bool json = false)
        {
            var data = Encoding.UTF8.GetBytes(text);
            context.Response.StatusCode = code;
            context.Response.ContentType = json ? "application/json; charset=utf-8" : "text/plain; charset=utf-8";
            context.Response.ContentLength64 = data.Length;
            if (context.Request.HttpMethod != "HEAD") await context.Response.OutputStream.WriteAsync(data, 0, data.Length).ConfigureAwait(false);
        }

        private static string AsciiFilename(string name)
        {
            return new string(name.Select(c => c >= 32 && c <= 126 && c != '"' && c != '\\' && c != '/' && c != ';' ? c : '_').ToArray());
        }

        private static string Quote(string value)
        {
            var result = new StringBuilder("\"");
            foreach (var c in value)
            {
                if (c == '"' || c == '\\') result.Append('\\').Append(c);
                else if (c < 32) result.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                else result.Append(c);
            }
            return result.Append('"').ToString();
        }

        public void Dispose() { _disposed = true; CancelDownloads(); Clear(); }

        internal sealed class SharedFile
        {
            internal SharedFile(FileInfo info, string token)
            {
                Path = info.FullName; Name = info.Name; Length = info.Length; LastWriteUtc = info.LastWriteTimeUtc; Token = token;
                ETag = "\"" + token + "-" + Length.ToString("x", CultureInfo.InvariantCulture) + "-" + LastWriteUtc.Ticks.ToString("x", CultureInfo.InvariantCulture) + "\"";
            }
            public string Token { get; private set; }
            public string Name { get; private set; }
            public long Length { get; private set; }
            internal string Path { get; private set; }
            internal DateTime LastWriteUtc { get; private set; }
            internal string ETag { get; private set; }
            internal readonly object Sync = new object();
            internal readonly HashSet<HttpListenerResponse> Responses = new HashSet<HttpListenerResponse>();
            internal bool Revoked;
            public long BytesSent;
            public int ActiveDownloads;
            public int CompletedDownloads;
        }
    }
}
