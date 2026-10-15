using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ScreenTask
{
    /// <summary>
    /// Minimal signaling only. It forwards SDP/ICE setup messages between one
    /// screen-share host and one viewer; video/audio never pass through here.
    /// </summary>
    internal sealed class RtcSignalingHub : IDisposable
    {
        private readonly ConcurrentDictionary<string, Room> _rooms = new ConcurrentDictionary<string, Room>(StringComparer.Ordinal);
        private volatile bool _disposed;
        public Action<string> Log { get; set; }

        public async Task HandleClientAsync(HttpListenerContext context)
        {
            try { await HandleCoreAsync(context); }
            catch (Exception ex) when (ex is HttpListenerException || ex is WebSocketException || ex is ObjectDisposedException || ex is InvalidOperationException)
            {
                Log?.Invoke("Handshake failed: " + ex.Message);
                try { context.Response.Abort(); } catch { }
            }
        }

        private async Task HandleCoreAsync(HttpListenerContext context)
        {
            var roomId = context.Request.QueryString["room"];
            var role = context.Request.QueryString["role"];
            if (String.IsNullOrWhiteSpace(roomId) || roomId.Length < 16 || roomId.Length > 128 ||
                !IsSafeToken(roomId) || (role != "host" && role != "viewer"))
            {
                context.Response.StatusCode = 400;
                context.Response.Close();
                return;
            }

            if (_disposed)
            {
                context.Response.StatusCode = 503;
                context.Response.Close();
                return;
            }

            var socket = (await context.AcceptWebSocketAsync(null)).WebSocket;
            var room = _rooms.GetOrAdd(roomId, _ => new Room());
            var joined = false;
            try
            {
                // Keep membership changes and their notifications in order. A
                // reconnect must never overtake the old peer-left notification.
                await room.MembershipLock.WaitAsync();
                try
                {
                    lock (room.Sync)
                    {
                        if (!room.Closed && (role == "host" ? room.Host : room.Viewer) == null)
                        {
                            if (role == "host") room.Host = socket; else room.Viewer = socket;
                            joined = true;
                        }
                    }
                    if (!joined)
                    {
                        await SendTextAsync(socket, "{\"type\":\"busy\"}");
                        return;
                    }
                    Log?.Invoke(role + " joined from " + context.Request.RemoteEndPoint);
                    await SendRoomTextAsync(room, socket, "{\"type\":\"joined\"}");
                    WebSocket host, viewer;
                    lock (room.Sync) { host = room.Host; viewer = room.Viewer; }
                    if (host != null && viewer != null && host.State == WebSocketState.Open && viewer.State == WebSocketState.Open)
                    {
                        await SendRoomTextAsync(room, viewer, "{\"type\":\"peer-ready\"}");
                        await SendRoomTextAsync(room, host, "{\"type\":\"peer-ready\"}");
                        Log?.Invoke("Host and viewer paired; negotiating direct media.");
                    }
                }
                finally { room.MembershipLock.Release(); }

                var buffer = new byte[16 * 1024];
                while (socket.State == WebSocketState.Open)
                {
                    var message = await ReceiveTextAsync(socket, buffer);
                    if (message == null) break;
                    WebSocket peer;
                    lock (room.Sync) peer = role == "host" ? room.Viewer : room.Host;
                    if (peer != null && peer.State == WebSocketState.Open)
                        await SendRoomTextAsync(room, peer, message);
                }
            }
            catch (Exception ex) when (ex is WebSocketException || ex is ObjectDisposedException || ex is InvalidOperationException)
            {
                Log?.Invoke(role + " signaling ended: " + ex.Message);
            }
            finally
            {
                if (joined)
                {
                    await room.MembershipLock.WaitAsync();
                    try
                    {
                        WebSocket other;
                        lock (room.Sync)
                        {
                            if (role == "host" && ReferenceEquals(room.Host, socket)) room.Host = null;
                            if (role == "viewer" && ReferenceEquals(room.Viewer, socket)) room.Viewer = null;
                            other = role == "host" ? room.Viewer : room.Host;
                            if (room.Host == null && room.Viewer == null) room.Closed = true;
                        }
                        if (other != null && other.State == WebSocketState.Open)
                        {
                            try { await SendRoomTextAsync(room, other, "{\"type\":\"peer-left\"}"); }
                            catch (Exception ex) when (ex is WebSocketException || ex is ObjectDisposedException || ex is InvalidOperationException) { }
                        }
                        if (room.Closed)
                            ((ICollection<KeyValuePair<string, Room>>)_rooms).Remove(new KeyValuePair<string, Room>(roomId, room));
                    }
                    finally { room.MembershipLock.Release(); }
                    Log?.Invoke(role + " disconnected.");
                }
                await CloseSocketAsync(socket, "Signaling ended");
            }
        }

        private static bool IsSafeToken(string value)
        {
            foreach (var c in value)
                if (!((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '-' || c == '_')) return false;
            return true;
        }

        private static async Task<string> ReceiveTextAsync(WebSocket socket, byte[] buffer)
        {
            var builder = new MemoryStream();
            WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                if (result.MessageType == WebSocketMessageType.Close) return null;
                if (result.MessageType != WebSocketMessageType.Text) return null;
                if (builder.Length + result.Count > 64 * 1024) throw new WebSocketException("Signaling message is too large.");
                builder.Write(buffer, 0, result.Count);
            } while (!result.EndOfMessage);
            return Encoding.UTF8.GetString(builder.ToArray());
        }

        private static Task SendTextAsync(WebSocket socket, string message)
        {
            var bytes = Encoding.UTF8.GetBytes(message);
            if (socket == null || socket.State != WebSocketState.Open) return Task.FromResult(0);
            return socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
        }

        private static async Task SendRoomTextAsync(Room room, WebSocket socket, string message)
        {
            await room.SendLock.WaitAsync();
            try { await SendTextAsync(socket, message); }
            finally { room.SendLock.Release(); }
        }

        private static async Task CloseSocketAsync(WebSocket socket, string reason)
        {
            try
            {
                using (var timeout = new CancellationTokenSource(2000))
                    if (socket.State == WebSocketState.Open || socket.State == WebSocketState.CloseReceived)
                        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, reason, timeout.Token);
            }
            catch (Exception ex) when (ex is WebSocketException || ex is OperationCanceledException || ex is ObjectDisposedException) { }
            finally { socket.Dispose(); }
        }

        public void Dispose()
        {
            _disposed = true;
            Reset();
        }

        public void Reset()
        {
            foreach (var room in _rooms.Values)
            {
                lock (room.Sync)
                {
                    room.Closed = true;
                    try { room.Host?.Abort(); } catch { }
                    try { room.Viewer?.Abort(); } catch { }
                }
            }
            _rooms.Clear();
        }

        private sealed class Room
        {
            public readonly object Sync = new object();
            public readonly SemaphoreSlim MembershipLock = new SemaphoreSlim(1, 1);
            public readonly SemaphoreSlim SendLock = new SemaphoreSlim(1, 1);
            public WebSocket Host;
            public WebSocket Viewer;
            public bool Closed;
        }
    }
}
