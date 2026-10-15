using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ScreenTask
{
    internal sealed class H264StreamHub : IDisposable
    {
        private const int MaxQueuedFragments = 8;
        private readonly object _sync = new object();
        private readonly HashSet<Subscriber> _subscribers = new HashSet<Subscriber>();
        private TaskCompletionSource<byte[]> _initializationReady = NewInitializationSource();
        private byte[] _initializationSegment;
        private string _codecString = "avc1.42E02A";
        private bool _hdrCorrected;
        private bool _disposed;

        public Action<double, double, double, double> ClientSample { get; set; }

        public void BeginStream()
        {
            lock (_sync)
            {
                foreach (var subscriber in _subscribers) subscriber.Complete();
                _subscribers.Clear();
                _initializationReady.TrySetResult(null);
                _initializationSegment = null;
                _hdrCorrected = false;
                _initializationReady = NewInitializationSource();
            }
        }

        public void PublishInitialization(byte[] segment)
        {
            if (segment == null || segment.Length == 0) throw new ArgumentException("An MP4 initialization segment is required.", "segment");
            lock (_sync)
            {
                if (_disposed) return;
                _initializationSegment = segment;
                _initializationReady.TrySetResult(segment);
            }
        }

        public bool HasInitialization
        {
            get { lock (_sync) return _initializationSegment != null; }
        }

        public void SetCodecString(string codecString)
        {
            lock (_sync) _codecString = String.IsNullOrWhiteSpace(codecString) ? "avc1.42E02A" : codecString;
        }

        public void SetHdrCorrected(bool corrected) { lock (_sync) _hdrCorrected = corrected; }

        public void FailStream(string message)
        {
            lock (_sync)
            {
                if (_initializationSegment == null)
                    _initializationReady.TrySetResult(null);
            }
        }

        public void PublishFragment(byte[] data, int frameCount)
        {
            if (data == null || data.Length == 0) return;
            lock (_sync)
            {
                if (_disposed) return;
                foreach (var subscriber in _subscribers)
                    subscriber.Enqueue(new H264Fragment(data, Math.Max(1, frameCount)), MaxQueuedFragments);
            }
        }

        public async Task HandleClientAsync(HttpListenerContext context)
        {
            Subscriber subscriber = null;
            try
            {
                byte[] init = null;
                var timeout = Task.Delay(10000);
                while (init == null)
                {
                    Task<byte[]> initializationTask;
                    lock (_sync) initializationTask = _initializationReady.Task;
                    if (await Task.WhenAny(initializationTask, timeout) != initializationTask) break;
                    init = await initializationTask;
                    if (init != null) break;
                    lock (_sync)
                    {
                        // A new encoder may have replaced the task while this
                        // viewer waited. Wait for that generation instead of
                        // incorrectly reporting that H.264 is unavailable.
                        if (_disposed || initializationTask == _initializationReady.Task) break;
                    }
                }
                if (init == null) { context.Response.StatusCode = 503; context.Response.Close(); return; }
                lock (_sync)
                {
                    if (_disposed || _initializationSegment == null)
                    {
                        context.Response.StatusCode = 503;
                        context.Response.Close();
                        return;
                    }
                    init = _initializationSegment;
                    context.Response.Headers["X-Stream-Codecs"] = _codecString;
                    context.Response.Headers["X-Stream-HDR-Corrected"] = _hdrCorrected ? "true" : "false";
                    subscriber = new Subscriber();
                    _subscribers.Add(subscriber);
                }

                context.Response.StatusCode = 200;
                context.Response.ContentType = "video/mp4";
                context.Response.SendChunked = true;
                context.Response.KeepAlive = true;
                context.Response.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate";
                await context.Response.OutputStream.WriteAsync(init, 0, init.Length);
                await context.Response.OutputStream.FlushAsync();

                var sampleStart = System.Diagnostics.Stopwatch.GetTimestamp();
                long sampledFrames = 0;
                long sampledBytes = 0;
                long sampledWriteTicks = 0;

                while (!_disposed)
                {
                    var fragment = await subscriber.ReadAsync();
                    if (fragment == null) break;
                    var writeStart = System.Diagnostics.Stopwatch.GetTimestamp();
                    await context.Response.OutputStream.WriteAsync(fragment.Data, 0, fragment.Data.Length);
                    await context.Response.OutputStream.FlushAsync();
                    sampledWriteTicks += System.Diagnostics.Stopwatch.GetTimestamp() - writeStart;
                    sampledFrames += fragment.FrameCount;
                    sampledBytes += fragment.Data.Length;
                    var now = System.Diagnostics.Stopwatch.GetTimestamp();
                    if (now - sampleStart >= System.Diagnostics.Stopwatch.Frequency)
                    {
                        var elapsed = now - sampleStart;
                        var sample = ClientSample;
                        if (sample != null)
                            sample(sampledFrames * (double)System.Diagnostics.Stopwatch.Frequency / elapsed,
                                sampledBytes * 8.0 * System.Diagnostics.Stopwatch.Frequency / elapsed / 1000000.0,
                                sampledFrames == 0 ? 0 : sampledBytes / (double)sampledFrames / 1024.0,
                                sampledFrames == 0 ? 0 : sampledWriteTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency / sampledFrames);
                        sampledFrames = 0;
                        sampledBytes = 0;
                        sampledWriteTicks = 0;
                        sampleStart = now;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException || ex is HttpListenerException || ex is ObjectDisposedException || ex is InvalidOperationException)
            {
                // A viewer disconnected, a stream was restarted, or a slow viewer fell behind.
            }
            finally
            {
                if (subscriber != null)
                {
                    lock (_sync) _subscribers.Remove(subscriber);
                    subscriber.Dispose();
                }
                try { context.Response.Close(); }
                catch (HttpListenerException) { }
                catch (ObjectDisposedException) { }
            }
        }

        public void Dispose()
        {
            lock (_sync)
            {
                if (_disposed) return;
                _disposed = true;
                foreach (var subscriber in _subscribers) subscriber.Complete();
                _subscribers.Clear();
                _initializationReady.TrySetResult(null);
            }
        }

        private static TaskCompletionSource<byte[]> NewInitializationSource()
        {
            return new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        internal sealed class H264Fragment
        {
            public H264Fragment(byte[] data, int frameCount)
            {
                Data = data;
                FrameCount = frameCount;
            }

            public byte[] Data { get; private set; }
            public int FrameCount { get; private set; }
        }

        private sealed class Subscriber : IDisposable
        {
            private readonly object _queueLock = new object();
            private readonly Queue<H264Fragment> _queue = new Queue<H264Fragment>();
            private readonly SemaphoreSlim _available = new SemaphoreSlim(0);
            private bool _complete;

            public void Enqueue(H264Fragment fragment, int maxCount)
            {
                lock (_queueLock)
                {
                    if (_complete) return;
                    if (_queue.Count >= maxCount)
                    {
                        // Close slow viewers instead of growing memory or latency without bound.
                        _complete = true;
                        _queue.Clear();
                        _available.Release();
                        return;
                    }
                    _queue.Enqueue(fragment);
                    _available.Release();
                }
            }

            public async Task<H264Fragment> ReadAsync()
            {
                await _available.WaitAsync();
                lock (_queueLock)
                {
                    if (_queue.Count > 0) return _queue.Dequeue();
                    return null;
                }
            }

            public void Complete()
            {
                lock (_queueLock)
                {
                    if (_complete) return;
                    _complete = true;
                    _available.Release();
                }
            }

            public void Dispose()
            {
                _available.Dispose();
            }
        }

        internal static bool TryReadMp4Box(Stream stream, out byte[] box, out string type)
        {
            box = null;
            type = null;
            var header = new byte[8];
            var first = stream.ReadByte();
            if (first < 0) return false;
            header[0] = (byte)first;
            ReadExactly(stream, header, 1, 7);

            ulong size = ReadUInt32(header, 0);
            var headerSize = 8;
            byte[] extended = null;
            if (size == 1)
            {
                extended = new byte[8];
                ReadExactly(stream, extended, 0, 8);
                size = ReadUInt64(extended, 0);
                headerSize = 16;
            }
            else if (size == 0)
            {
                throw new InvalidDataException("An MP4 box with an open-ended size cannot be read from this stream.");
            }

            if (size < (ulong)headerSize || size > 64UL * 1024UL * 1024UL)
                throw new InvalidDataException("The H.264 stream contained an invalid MP4 box size.");

            box = new byte[(int)size];
            Buffer.BlockCopy(header, 0, box, 0, 8);
            if (headerSize == 16)
                Buffer.BlockCopy(extended, 0, box, 8, 8);
            var remaining = box.Length - headerSize;
            if (remaining > 0) ReadExactly(stream, box, headerSize, remaining);
            type = Encoding.ASCII.GetString(box, 4, 4);
            return true;
        }

        internal static int GetFragmentFrameCount(byte[] moof)
        {
            if (moof == null || moof.Length < 8 || Encoding.ASCII.GetString(moof, 4, 4) != "moof") return 1;
            var count = FindTrunSamples(moof, 8, moof.Length);
            return count > 0 ? count : 1;
        }

        internal static string GetCodecString(byte[] initialization)
        {
            // avcC is inside the video sample entry; its first four payload bytes
            // describe the configuration version, profile, constraints and level.
            for (var i = 4; i + 8 <= initialization.Length; i++)
            {
                if (initialization[i] != 'a' || initialization[i + 1] != 'v' ||
                    initialization[i + 2] != 'c' || initialization[i + 3] != 'C') continue;
                var size = ReadUInt32(initialization, i - 4);
                if (size < 12 || size > initialization.Length - i + 4 || initialization[i + 4] != 1) continue;
                return String.Format("avc1.{0:X2}{1:X2}{2:X2}", initialization[i + 5], initialization[i + 6], initialization[i + 7]);
            }
            throw new InvalidDataException("H.264 initialization did not contain an AVC decoder configuration.");
        }

        private static int FindTrunSamples(byte[] data, int start, int end)
        {
            for (var offset = start; offset + 8 <= end;)
            {
                var size = ReadUInt32(data, offset);
                var header = 8;
                if (size == 1)
                {
                    if (offset + 16 > end) break;
                    var extended = ReadUInt64(data, offset + 8);
                    if (extended > Int32.MaxValue) break;
                    size = (uint)extended;
                    header = 16;
                }
                if (size < header || size > end - offset) break;
                var type = Encoding.ASCII.GetString(data, offset + 4, 4);
                if (type == "trun" && size >= header + 8)
                    return (int)ReadUInt32(data, offset + header + 4);
                if (type == "traf")
                {
                    var nested = FindTrunSamples(data, offset + header, offset + (int)size);
                    if (nested > 0) return nested;
                }
                offset += (int)size;
            }
            return 0;
        }

        private static void ReadExactly(Stream stream, byte[] buffer, int offset, int count)
        {
            while (count > 0)
            {
                var read = stream.Read(buffer, offset, count);
                if (read <= 0) throw new EndOfStreamException("The H.264 stream ended inside an MP4 box.");
                offset += read;
                count -= read;
            }
        }

        private static uint ReadUInt32(byte[] data, int offset)
        {
            return ((uint)data[offset] << 24) | ((uint)data[offset + 1] << 16) |
                ((uint)data[offset + 2] << 8) | data[offset + 3];
        }

        private static ulong ReadUInt64(byte[] data, int offset)
        {
            return ((ulong)ReadUInt32(data, offset) << 32) | ReadUInt32(data, offset + 4);
        }
    }
}
