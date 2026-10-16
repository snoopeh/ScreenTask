using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace ScreenTask
{
    // Browsers keep selected File objects. On a download/range request, the
    // sender streams only those bytes through a bounded WebSocket window.
    // Nothing is uploaded ahead of time or stored on the server's disk.
    internal sealed class BrowserFileHub : IDisposable
    {
        private readonly ConcurrentDictionary<string, Session> sessions = new ConcurrentDictionary<string, Session>();
        private readonly ConcurrentDictionary<string, Share> shares = new ConcurrentDictionary<string, Share>();
        private readonly ConcurrentDictionary<string, Transfer> pending = new ConcurrentDictionary<string, Transfer>();
        private readonly ConcurrentDictionary<string, Transfer> transfers = new ConcurrentDictionary<string, Transfer>();
        private readonly SemaphoreSlim slots = new SemaphoreSlim(4, 4);
        private readonly CancellationTokenSource lifetime = new CancellationTokenSource();
        private volatile bool online;
        internal BrowserFileHub() { _ = Task.Run(ReportProgress); }
        internal void StartServing() { online = true; }
        internal void StopServing() { online = false; foreach (var t in transfers.Values) t.Cancel(); }
        internal static bool IsRoute(string path) => path == "/file-control" || path.StartsWith("/file-source/", StringComparison.Ordinal) ||
            path.StartsWith("/browser-file-info/", StringComparison.Ordinal) || path.StartsWith("/browser-download/", StringComparison.Ordinal) || path.StartsWith("/file-progress/", StringComparison.Ordinal);
        private static bool Token(string value) => value != null && value.Length == 48 && value.All(c => c >= 'a' && c <= 'f' || c >= '0' && c <= '9');
        private static string NewToken() { var data = new byte[24]; using (var random = RandomNumberGenerator.Create()) random.GetBytes(data); return BitConverter.ToString(data).Replace("-", "").ToLowerInvariant(); }
        private static string Serialize(object data) => new JavaScriptSerializer().Serialize(data);

        internal async Task HandleAsync(HttpListenerContext context)
        {
            try
            {
                context.Response.Headers["Cache-Control"] = "private, no-store";
                context.Response.Headers["X-Content-Type-Options"] = "nosniff";
                context.Response.Headers["Referrer-Policy"] = "no-referrer";
                if (!online) { await Reply(context, 503, "File sharing is stopped."); return; }
                var path = context.Request.Url.LocalPath;
                if (path == "/file-control" || path.StartsWith("/file-source/", StringComparison.Ordinal))
                {
                    Uri origin;
                    if (!context.Request.IsWebSocketRequest || !Uri.TryCreate(context.Request.Headers["Origin"], UriKind.Absolute, out origin) ||
                        !String.Equals(origin.Authority, context.Request.Url.Authority, StringComparison.OrdinalIgnoreCase) || (origin.Scheme != "http" && origin.Scheme != "https"))
                    { await Reply(context, 403, "Open file sharing from this ScreenTask page."); return; }
                    if (path == "/file-control") await Control(context);
                    else await AttachSource(context, path.Substring("/file-source/".Length));
                    return;
                }
                if (context.Request.HttpMethod != "GET" && context.Request.HttpMethod != "HEAD") { await Reply(context, 405, "Use GET or HEAD."); return; }
                var token = path.Substring(path.LastIndexOf('/') + 1);
                Share file;
                if (!Token(token) || !shares.TryGetValue(token, out file)) { await Reply(context, 404, "This link expired. The sender must keep their sharing page open."); return; }
                if (path == "/browser-file-info/" + token)
                {
                    await Json(context, new { name = file.Name, size = file.Size.ToString(CultureInfo.InvariantCulture), download = "/browser-download/" + token, route = "ScreenTask streamed connection" }); return;
                }
                if (path == "/file-progress/" + token)
                {
                    Transfer transfer;
                    var id = context.Request.QueryString["transfer"];
                    if (!Token(id) || !transfers.TryGetValue(id, out transfer) || transfer.File != file) { await Json(context, new { state = "waiting", sent = "0", size = file.Size.ToString(), speed = 0, seconds = 0 }); return; }
                    await Json(context, transfer.Snapshot()); return;
                }
                if (path != "/browser-download/" + token) { await Reply(context, 404, "Invalid file route."); return; }
                await Download(context, file);
            }
            catch (Exception ex) when (ex is IOException || ex is WebSocketException || ex is HttpListenerException || ex is ObjectDisposedException || ex is OperationCanceledException || ex is InvalidOperationException || ex is ArgumentException)
            { try { context.Response.Abort(); } catch { } }
            finally { try { context.Response.Close(); } catch { } }
        }

        private async Task Control(HttpListenerContext context)
        {
            if (sessions.Count >= 32) { await Reply(context, 429, "Too many active sharing pages."); return; }
            var session = new Session((await context.AcceptWebSocketAsync(null)).WebSocket);
            var id = NewToken();
            sessions[id] = session;
            try
            {
                await session.Send(new { type = "ready" });
                while (!lifetime.IsCancellationRequested && session.Socket.State == WebSocketState.Open)
                {
                    var json = await ReadText(session.Socket, lifetime.Token);
                    if (json == null) break;
                    var message = new JavaScriptSerializer().Deserialize<Command>(json);
                    if (message == null) throw new InvalidDataException();
                    if (message.type == "add")
                    {
                        if (session.Files.Count >= 64 || String.IsNullOrWhiteSpace(message.id) || message.id.Length > 64 || String.IsNullOrWhiteSpace(message.name) || message.name.Length > 512 ||
                            message.name.Any(c => c < 32 || c == '/' || c == '\\') || message.size < 0 || message.size > 9007199254740991L)
                        { await session.Send(new { type = "error", message = "Select up to 64 files with valid names and sizes." }); continue; }
                        var existing = session.Files.Values.FirstOrDefault(f => f.Id == message.id);
                        var share = existing ?? new Share { Owner = session, Id = message.id, Name = message.name, Size = message.size, Token = NewToken() };
                        shares[share.Token] = share; session.Files[share.Token] = share;
                        await session.Send(new { type = "shared", id = share.Id, token = share.Token, name = share.Name, size = share.Size.ToString(CultureInfo.InvariantCulture) });
                    }
                    else if (message.type == "remove")
                    {
                        Share file;
                        if (message.token != null && session.Files.TryRemove(message.token, out file)) Revoke(file);
                    }
                    else if (message.type == "cancel")
                    {
                        Transfer transfer;
                        if (message.transfer != null && transfers.TryGetValue(message.transfer, out transfer) && transfer.File.Owner == session) transfer.Cancel();
                    }
                }
            }
            finally
            {
                Session removed; sessions.TryRemove(id, out removed);
                foreach (var share in session.Files.Values) Revoke(share);
                session.Socket.Abort(); session.Socket.Dispose();
            }
        }

        private void Revoke(Share file)
        {
            Share removed; shares.TryRemove(file.Token, out removed);
            foreach (var transfer in transfers.Values.Where(t => t.File == file)) transfer.Cancel();
        }

        private async Task AttachSource(HttpListenerContext context, string nonce)
        {
            Transfer transfer;
            if (!Token(nonce) || !pending.TryRemove(nonce, out transfer) || transfer.Cancellation.IsCancellationRequested)
            { await Reply(context, 404, "Transfer expired."); return; }
            var socket = (await context.AcceptWebSocketAsync(null)).WebSocket;
            transfer.Source = socket;
            transfer.Ready.TrySetResult(socket);
            await transfer.Finished.Task;
        }

        private async Task Download(HttpListenerContext context, Share file)
        {
            var response = context.Response;
            var etag = "\"" + file.Token + "\"";
            response.Headers["ETag"] = etag;
            response.Headers["Accept-Ranges"] = "bytes";
            var range = context.Request.HttpMethod == "HEAD" ? null : context.Request.Headers["Range"];
            if (!String.IsNullOrEmpty(context.Request.Headers["If-Range"]) && context.Request.Headers["If-Range"] != etag) range = null;
            if (!String.IsNullOrEmpty(context.Request.Headers["If-Match"]) && context.Request.Headers["If-Match"] != etag && context.Request.Headers["If-Match"] != "*") { await Reply(context, 412, "File version changed."); return; }
            if (context.Request.Headers["If-None-Match"] == etag || context.Request.Headers["If-None-Match"] == "*") { response.StatusCode = 304; return; }
            long first = 0, last = file.Size - 1;
            if (!String.IsNullOrEmpty(range) && !FileShareHub.TryRange(range, file.Size, out first, out last))
            { response.Headers["Content-Range"] = "bytes */" + file.Size; await Reply(context, 416, "Requested range is unavailable."); return; }
            var length = file.Size == 0 ? 0 : last - first + 1;
            if (context.Request.HttpMethod == "HEAD") { SetDownloadHeaders(response, file, range, first, last, length); return; }
            if (!slots.Wait(0)) { response.Headers["Retry-After"] = "5"; await Reply(context, 429, "Four downloads are active. Try again shortly."); return; }
            Transfer t = null;
            var nonce = NewToken();
            try
            {
                foreach (var old in transfers.Where(pair => pair.Value.Finished.Task.IsCompleted && DateTime.UtcNow - pair.Value.Updated > TimeSpan.FromMinutes(5)).ToArray()) { Transfer removed; transfers.TryRemove(old.Key, out removed); }
                if (transfers.Count >= 512) { await Reply(context, 429, "Transfer history is busy. Retry in a few minutes."); return; }
                var id = context.Request.QueryString["transfer"];
                if (!Token(id)) id = NewToken();
                t = new Transfer { File = file, Id = id, Length = length, Response = response };
                Transfer existing;
                if (!transfers.TryAdd(id, t) && !(transfers.TryGetValue(id, out existing) && existing.Finished.Task.IsCompleted && transfers.TryUpdate(id, t, existing)))
                { t = null; await Reply(context, 409, "This download is already active. Retry shortly."); return; }
                if (length > 0)
                {
                    pending[nonce] = t;
                    await file.Owner.Send(new { type = "read", id = file.Id, token = file.Token, nonce, transfer = t.Id, start = first.ToString(CultureInfo.InvariantCulture), length = length.ToString(CultureInfo.InvariantCulture) });
                    var timeout = Task.Delay(20000, t.Cancellation.Token);
                    if (await Task.WhenAny(t.Ready.Task, timeout) != t.Ready.Task) { await Reply(context, 410, "The sender did not respond. Ask them to reopen their sharing page."); return; }
                    t.Cancellation.Token.ThrowIfCancellationRequested();
                }
                SetDownloadHeaders(response, file, range, first, last, length);
                t.State = "sending";
                var buffer = new byte[64 * 1024];
                while (Interlocked.Read(ref t.Sent) < length)
                {
                    using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(t.Cancellation.Token, lifetime.Token))
                    {
                        timeout.CancelAfter(30000);
                        var packet = await t.Source.ReceiveAsync(new ArraySegment<byte>(buffer), timeout.Token);
                        if (packet.MessageType != WebSocketMessageType.Binary || packet.Count == 0 || packet.Count > length - Interlocked.Read(ref t.Sent)) throw new IOException("The sender stopped or sent invalid bytes.");
                        await response.OutputStream.WriteAsync(buffer, 0, packet.Count, timeout.Token);
                        Interlocked.Add(ref t.Sent, packet.Count); t.Updated = DateTime.UtcNow;
                        var ack = Encoding.ASCII.GetBytes(Interlocked.Read(ref t.Sent).ToString(CultureInfo.InvariantCulture));
                        await t.Source.SendAsync(new ArraySegment<byte>(ack), WebSocketMessageType.Text, true, timeout.Token);
                    }
                }
                await response.OutputStream.FlushAsync();
                t.State = "complete";
            }
            finally
            {
                Transfer removed; pending.TryRemove(nonce, out removed);
                if (t != null)
                {
                    if (t.State != "complete") { t.State = t.Cancellation.IsCancellationRequested ? "cancelled" : "interrupted"; try { response.Abort(); } catch { } }
                    t.Updated = DateTime.UtcNow; t.Elapsed.Stop();
                    t.Source?.Abort(); t.Source?.Dispose(); t.Finished.TrySetResult(true);
                }
                slots.Release();
            }
        }

        private static void SetDownloadHeaders(HttpListenerResponse response, Share file, string range, long first, long last, long length)
        {
            response.StatusCode = String.IsNullOrEmpty(range) ? 200 : 206;
            if (range != null) response.Headers["Content-Range"] = String.Format(CultureInfo.InvariantCulture, "bytes {0}-{1}/{2}", first, last, file.Size);
            response.ContentType = "application/octet-stream";
            response.Headers["Content-Disposition"] = "attachment; filename=\"" + new string(file.Name.Select(c => c >= 32 && c <= 126 && c != '"' && c != ';' ? c : '_').ToArray()) + "\"; filename*=UTF-8''" + Uri.EscapeDataString(file.Name);
            response.ContentLength64 = length;
        }

        private async Task ReportProgress()
        {
            try
            {
                while (!lifetime.IsCancellationRequested)
                {
                    await Task.Delay(700, lifetime.Token);
                    foreach (var session in sessions.Values)
                    {
                        var items = transfers.Values.Where(t => t.File.Owner == session).OrderByDescending(t => t.Updated).Take(64).Select(t => new { token = t.File.Token, transfer = t.Id, progress = t.Snapshot() }).ToArray();
                        if (items.Length == 0) continue;
                        try { await session.Send(new { type = "progress", transfers = items }); }
                        catch (Exception ex) when (ex is WebSocketException || ex is ObjectDisposedException || ex is OperationCanceledException) { session.Socket.Abort(); }
                    }
                }
            }
            catch (OperationCanceledException) { }
        }

        private static async Task<string> ReadText(WebSocket socket, CancellationToken token)
        {
            var buffer = new byte[4096];
            using (var message = new MemoryStream())
            {
                WebSocketReceiveResult result;
                do
                {
                    result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), token);
                    if (result.MessageType == WebSocketMessageType.Close) return null;
                    if (result.MessageType != WebSocketMessageType.Text || message.Length + result.Count > 65536) throw new InvalidDataException("Invalid sharing command.");
                    message.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);
                return Encoding.UTF8.GetString(message.ToArray());
            }
        }
        private static Task Json(HttpListenerContext context, object value) => Reply(context, 200, Serialize(value), true);
        private static async Task Reply(HttpListenerContext context, int status, string text, bool json = false)
        {
            var bytes = Encoding.UTF8.GetBytes(text); context.Response.StatusCode = status;
            context.Response.ContentType = json ? "application/json; charset=utf-8" : "text/plain; charset=utf-8";
            context.Response.ContentLength64 = bytes.Length;
            if (context.Request.HttpMethod != "HEAD") await context.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length);
        }
        public void Dispose() { StopServing(); lifetime.Cancel(); foreach (var session in sessions.Values) session.Socket.Abort(); }

        internal sealed class Command { public string type { get; set; } public string id { get; set; } public string name { get; set; } public long size { get; set; } public string token { get; set; } public string transfer { get; set; } }
        private sealed class Session
        {
            internal readonly WebSocket Socket;
            internal readonly SemaphoreSlim Sending = new SemaphoreSlim(1, 1);
            internal readonly ConcurrentDictionary<string, Share> Files = new ConcurrentDictionary<string, Share>();
            internal Session(WebSocket socket) { Socket = socket; }
            internal async Task Send(object value)
            {
                using (var timeout = new CancellationTokenSource(10000))
                {
                    await Sending.WaitAsync(timeout.Token);
                    try { var bytes = Encoding.UTF8.GetBytes(Serialize(value)); await Socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, timeout.Token); }
                    finally { Sending.Release(); }
                }
            }
        }
        private sealed class Share { internal Session Owner; internal string Id, Name, Token; internal long Size; }
        private sealed class Transfer
        {
            internal Share File; internal string Id, State = "waiting";
            internal long Length, Sent;
            internal DateTime Updated = DateTime.UtcNow;
            internal readonly Stopwatch Elapsed = Stopwatch.StartNew();
            internal HttpListenerResponse Response;
            internal WebSocket Source;
            internal readonly CancellationTokenSource Cancellation = new CancellationTokenSource();
            internal readonly TaskCompletionSource<WebSocket> Ready = new TaskCompletionSource<WebSocket>(TaskCreationOptions.RunContinuationsAsynchronously);
            internal readonly TaskCompletionSource<bool> Finished = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            internal object Snapshot() { var sent = Interlocked.Read(ref Sent); return new { state = State, sent = sent.ToString(CultureInfo.InvariantCulture), size = Length.ToString(CultureInfo.InvariantCulture), speed = sent / Math.Max(.001, Elapsed.Elapsed.TotalSeconds), seconds = Elapsed.Elapsed.TotalSeconds }; }
            internal void Cancel() { if (Finished.Task.IsCompleted) return; Cancellation.Cancel(); Source?.Abort(); try { Response?.Abort(); } catch { } }
        }
    }
}
