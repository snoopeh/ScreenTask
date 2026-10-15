using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Wave;
using NAudio.CoreAudioApi;

namespace ScreenTask
{
    /// <summary>Captures a playback device or a process loopback stream for browser viewers.</summary>
    internal sealed class AudioStreamServer : IDisposable
    {
        private static readonly Guid IeeeFloatSubFormat = new Guid("00000003-0000-0010-8000-00aa00389b71");
        private static readonly int[] ImaIndexTable = { -1, -1, -1, -1, 2, 4, 6, 8, -1, -1, -1, -1, 2, 4, 6, 8 };
        private static readonly int[] ImaStepTable =
        {
            7, 8, 9, 10, 11, 12, 13, 14, 16, 17, 19, 21, 23, 25, 28, 31,
            34, 37, 41, 45, 50, 55, 60, 66, 73, 80, 88, 97, 107, 118, 130, 143,
            157, 173, 190, 209, 230, 253, 279, 307, 337, 371, 408, 449, 494, 544, 598, 658,
            724, 796, 876, 963, 1060, 1166, 1282, 1411, 1552, 1707, 1878, 2066, 2272, 2499, 2749, 3024,
            3327, 3660, 4026, 4428, 4871, 5358, 5894, 6484, 7132, 7845, 8630, 9493, 10442, 11487, 12635, 13899,
            15289, 16818, 18500, 20350, 22385, 24623, 27086, 29794, 32767
        };
        private readonly ConcurrentDictionary<Guid, AudioClient> _clients = new ConcurrentDictionary<Guid, AudioClient>();
        private readonly object _imaEncodeLock = new object();
        private int[] _imaStepIndexes;
        private WasapiLoopbackCapture _capture;
        private readonly string _deviceId;
        private readonly int _targetProcessId;
        private readonly bool _excludeProcess;
        private readonly Action<string> _reportError;
        private Process _process;
        private int _sampleRate;
        private int _channels;

        public AudioStreamServer(string deviceId = null, int targetProcessId = 0, bool excludeProcess = false, Action<string> reportError = null)
        {
            _deviceId = deviceId;
            _targetProcessId = targetProcessId;
            _excludeProcess = excludeProcess;
            _reportError = reportError;
        }

        public void Start()
        {
            if (_capture != null || _process != null)
                return;

            if (_targetProcessId > 0)
            {
                StartProcessCapture();
                return;
            }

            if (String.IsNullOrWhiteSpace(_deviceId))
                _capture = new WasapiLoopbackCapture();
            else
            {
                var enumerator = new MMDeviceEnumerator();
                _capture = new WasapiLoopbackCapture(enumerator.GetDevice(_deviceId));
            }
            _capture.DataAvailable += Capture_DataAvailable;
            _capture.StartRecording();
            _sampleRate = _capture.WaveFormat.SampleRate;
            _channels = _capture.WaveFormat.Channels;
        }

        private void StartProcessCapture()
        {
            var helper = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ScreenTask.AudioCaptureHelper.exe");
            if (!File.Exists(helper)) throw new FileNotFoundException("Application audio helper is missing.", helper);
            var info = new ProcessStartInfo(helper, _targetProcessId + " " + (_excludeProcess ? "exclude" : "include"))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            _process = Process.Start(info);
            if (_process == null) throw new InvalidOperationException("Application audio helper did not start.");
            var stream = _process.StandardOutput.BaseStream;
            var header = new byte[9];
            var headerTask = Task.Run(() => ReadExactly(stream, header, header.Length));
            if (!headerTask.Wait(10000) || !headerTask.Result || header[0] != 1)
            {
                var error = _process.HasExited ? _process.StandardError.ReadToEnd() : "Application audio helper did not become ready.";
                DisposeProcess();
                throw new InvalidOperationException(error);
            }
            _sampleRate = BitConverter.ToInt32(header, 1);
            _channels = BitConverter.ToInt32(header, 5);
            if (_sampleRate < 8000 || _sampleRate > 192000 || _channels < 1 || _channels > 8)
            {
                DisposeProcess();
                throw new InvalidDataException("Application audio helper sent an invalid format.");
            }
            _ = Task.Run(() => ReadProcessFrames(stream));
        }

        private static bool ReadExactly(Stream stream, byte[] buffer, int count)
        {
            var offset = 0;
            while (offset < count)
            {
                var read = stream.Read(buffer, offset, count - offset);
                if (read == 0) return false;
                offset += read;
            }
            return true;
        }

        private void ReadProcessFrames(Stream stream)
        {
            var lengthBytes = new byte[4];
            try
            {
                while (ReadExactly(stream, lengthBytes, 4))
                {
                    var length = BitConverter.ToInt32(lengthBytes, 0);
                    if (length <= 0 || length > 1048576 || length % (_channels * 4) != 0) break;
                    var samples = new byte[length];
                    if (!ReadExactly(stream, samples, length)) break;
                    BroadcastSamples(samples, _sampleRate, _channels);
                }
            }
            catch (Exception ex) when (ex is IOException || ex is ObjectDisposedException) { }
            finally
            {
                if (_process != null)
                {
                    try { _reportError?.Invoke("Application audio capture ended. Restart the server or refresh the application list."); }
                    catch (Exception) { }
                }
            }
        }

        public async Task HandleClientAsync(HttpListenerContext context)
        {
            var socket = (await context.AcceptWebSocketAsync(null)).WebSocket;
            var id = Guid.NewGuid();
            var client = new AudioClient(socket);
            var metadata = System.Text.Encoding.UTF8.GetBytes(
                "{\"codec\":\"ima-adpcm-4bit\",\"sampleRate\":" + _sampleRate + ",\"channels\":" + _channels + "}");

            await socket.SendAsync(new ArraySegment<byte>(metadata), WebSocketMessageType.Text, true, CancellationToken.None);
            _clients[id] = client;

            var receiveBuffer = new byte[256];
            try
            {
                while (socket.State == WebSocketState.Open)
                {
                    var received = await socket.ReceiveAsync(new ArraySegment<byte>(receiveBuffer), CancellationToken.None);
                    if (received.MessageType == WebSocketMessageType.Close)
                        break;
                }
            }
            catch (Exception ex) when (ex is WebSocketException || ex is ObjectDisposedException)
            {
                // The viewer closed its tab or lost its connection.
            }
            finally
            {
                AudioClient ignored;
                _clients.TryRemove(id, out ignored);
                try
                {
                    if (socket.State == WebSocketState.Open || socket.State == WebSocketState.CloseReceived)
                        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Viewer disconnected", CancellationToken.None);
                }
                catch (WebSocketException)
                {
                }
                client.Dispose();
                socket.Dispose();
            }
        }

        private void Capture_DataAvailable(object sender, WaveInEventArgs e)
        {
            var format = _capture.WaveFormat;
            var bytesPerSample = format.BitsPerSample / 8;
            var sampleCount = e.BytesRecorded / bytesPerSample;
            if (sampleCount == 0)
                return;

            var samples = new byte[sampleCount * sizeof(float)];
            var isFloat = format.Encoding == WaveFormatEncoding.IeeeFloat;
            if (format.Encoding == WaveFormatEncoding.Extensible && format is WaveFormatExtensible extensible)
                isFloat = extensible.SubFormat == IeeeFloatSubFormat;

            for (var i = 0; i < sampleCount; i++)
            {
                float sample;
                var offset = i * bytesPerSample;
                if (isFloat && bytesPerSample == 4)
                    sample = BitConverter.ToSingle(e.Buffer, offset);
                else if (bytesPerSample == 1)
                    sample = (e.Buffer[offset] - 128) / 128f;
                else if (bytesPerSample == 2)
                    sample = BitConverter.ToInt16(e.Buffer, offset) / 32768f;
                else if (bytesPerSample == 3)
                {
                    var value = e.Buffer[offset] | (e.Buffer[offset + 1] << 8) | (e.Buffer[offset + 2] << 16);
                    if ((value & 0x800000) != 0)
                        value |= unchecked((int)0xff000000);
                    sample = value / 8388608f;
                }
                else if (bytesPerSample == 4)
                    sample = BitConverter.ToInt32(e.Buffer, offset) / 2147483648f;
                else
                    return;

                Buffer.BlockCopy(BitConverter.GetBytes(Math.Max(-1f, Math.Min(1f, sample))), 0, samples, i * sizeof(float), sizeof(float));
            }

            BroadcastSamples(samples, format.SampleRate, format.Channels);
        }

        private void BroadcastSamples(byte[] samples, int sampleRate, int channels)
        {
            if (_clients.IsEmpty) return;
            byte[] packet;
            lock (_imaEncodeLock) packet = EncodeImaAdpcmPacket(samples, sampleRate, channels);
            if (packet == null) return;
            foreach (var client in _clients.Values)
                client.TrySend(packet);
        }

        private byte[] EncodeImaAdpcmPacket(byte[] floatSamples, int sampleRate, int channels)
        {
            if (floatSamples == null || channels < 1 || channels > 8 || floatSamples.Length % (channels * sizeof(float)) != 0)
                return null;

            var frameCount = floatSamples.Length / (channels * sizeof(float));
            if (frameCount < 1) return null;

            const int fixedHeaderSize = 14;
            var channelHeaderSize = channels * 4;
            var encodedSampleCount = (frameCount - 1) * channels;
            var payloadSize = (encodedSampleCount + 1) / 2;
            var packet = new byte[fixedHeaderSize + channelHeaderSize + payloadSize];
            packet[0] = (byte)'S'; packet[1] = (byte)'T'; packet[2] = (byte)'A'; packet[3] = (byte)'D';
            Buffer.BlockCopy(BitConverter.GetBytes(sampleRate), 0, packet, 4, 4);
            Buffer.BlockCopy(BitConverter.GetBytes((short)channels), 0, packet, 8, 2);
            Buffer.BlockCopy(BitConverter.GetBytes(frameCount), 0, packet, 10, 4);

            var predictors = new int[channels];
            if (_imaStepIndexes == null || _imaStepIndexes.Length != channels)
                _imaStepIndexes = new int[channels];
            for (var channel = 0; channel < channels; channel++)
            {
                predictors[channel] = ReadPcm16(floatSamples, channel * sizeof(float));
                var channelOffset = fixedHeaderSize + channel * 4;
                Buffer.BlockCopy(BitConverter.GetBytes((short)predictors[channel]), 0, packet, channelOffset, 2);
                packet[channelOffset + 2] = (byte)_imaStepIndexes[channel];
            }

            var payloadOffset = fixedHeaderSize + channelHeaderSize;
            var encodedIndex = 0;
            for (var frame = 1; frame < frameCount; frame++)
            {
                for (var channel = 0; channel < channels; channel++)
                {
                    var inputOffset = (frame * channels + channel) * sizeof(float);
                    var code = EncodeImaSample(ReadPcm16(floatSamples, inputOffset), ref predictors[channel], ref _imaStepIndexes[channel]);
                    var outputIndex = payloadOffset + encodedIndex / 2;
                    if ((encodedIndex & 1) == 0) packet[outputIndex] = (byte)code;
                    else packet[outputIndex] |= (byte)(code << 4);
                    encodedIndex++;
                }
            }
            return packet;
        }

        private static short ReadPcm16(byte[] samples, int offset)
        {
            var value = BitConverter.ToSingle(samples, offset);
            if (Single.IsNaN(value)) value = 0;
            value = Math.Max(-1f, Math.Min(1f, value));
            return (short)Math.Max(Int16.MinValue, Math.Min(Int16.MaxValue, (int)Math.Round(value * Int16.MaxValue)));
        }

        private static int EncodeImaSample(int sample, ref int predictor, ref int stepIndex)
        {
            var step = ImaStepTable[stepIndex];
            var difference = sample - predictor;
            var code = 0;
            if (difference < 0) { code = 8; difference = -difference; }
            var delta = step >> 3;
            if (difference >= step) { code |= 4; difference -= step; delta += step; }
            if (difference >= (step >> 1)) { code |= 2; difference -= step >> 1; delta += step >> 1; }
            if (difference >= (step >> 2)) { code |= 1; delta += step >> 2; }
            predictor += (code & 8) != 0 ? -delta : delta;
            predictor = Math.Max(Int16.MinValue, Math.Min(Int16.MaxValue, predictor));
            stepIndex = Math.Max(0, Math.Min(ImaStepTable.Length - 1, stepIndex + ImaIndexTable[code]));
            return code;
        }

        public void Dispose()
        {
            if (_capture != null)
            {
                _capture.DataAvailable -= Capture_DataAvailable;
                _capture.StopRecording();
                _capture.Dispose();
                _capture = null;
            }
            DisposeProcess();

            foreach (var client in _clients.Values)
                client.Dispose();
            _clients.Clear();
        }

        private void DisposeProcess()
        {
            var process = _process;
            _process = null;
            if (process == null) return;
            try { if (!process.HasExited) process.Kill(); }
            catch (Exception ex) when (ex is InvalidOperationException || ex is System.ComponentModel.Win32Exception) { }
            process.Dispose();
        }

        private sealed class AudioClient : IDisposable
        {
            private readonly WebSocket _socket;
            private readonly SemaphoreSlim _sendLock = new SemaphoreSlim(1, 1);

            public AudioClient(WebSocket socket) { _socket = socket; }

            public void TrySend(byte[] packet)
            {
                // Drop a packet instead of building a latency-producing queue for a slow viewer.
                if (_socket.State != WebSocketState.Open || !_sendLock.Wait(0))
                    return;

                Task.Run(async () =>
                {
                    try
                    {
                        if (_socket.State == WebSocketState.Open)
                            await _socket.SendAsync(new ArraySegment<byte>(packet), WebSocketMessageType.Binary, true, CancellationToken.None);
                    }
                    catch (Exception ex) when (ex is WebSocketException || ex is ObjectDisposedException)
                    {
                    }
                    finally
                    {
                        _sendLock.Release();
                    }
                });
            }

            public void Dispose() { _socket.Abort(); }
        }
    }
}
