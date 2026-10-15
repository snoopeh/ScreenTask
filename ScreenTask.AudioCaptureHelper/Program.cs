using System.Buffers.Binary;
using NAudio.CoreAudioApi;
using NAudio.Wave;

if (args.Length != 2 || !uint.TryParse(args[0], out var targetPid) || targetPid == 0 ||
    (args[1] != "include" && args[1] != "exclude"))
{
    Console.Error.WriteLine("Usage: ScreenTask.AudioCaptureHelper <pid> <include|exclude>");
    return 2;
}

try
{
    var mode = args[1] == "include"
        ? ProcessLoopbackMode.IncludeTargetProcessTree
        : ProcessLoopbackMode.ExcludeTargetProcessTree;
    using var recorder = await new WasapiRecorderBuilder()
        .WithProcessLoopback(targetPid, mode)
        .WithFormat(WaveFormat.CreateIeeeFloatWaveFormat(44100, 2))
        .BuildAsync();

    var output = Console.OpenStandardOutput();
    var outputLock = new object();
    var readyWritten = 0;
    recorder.DataAvailable += (buffer, flags, _, _) =>
    {
        if (buffer.IsEmpty || Volatile.Read(ref readyWritten) == 0) return;
        lock (outputLock)
        {
            Span<byte> length = stackalloc byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(length, buffer.Length);
            output.Write(length);
            if ((flags & AudioClientBufferFlags.Silent) != 0)
                output.Write(new byte[buffer.Length]);
            else
                output.Write(buffer);
            output.Flush();
        }
    };
    recorder.RecordingStopped += (_, e) =>
    {
        if (e.Exception != null) Console.Error.WriteLine(e.Exception);
    };
    recorder.StartRecording();
    Span<byte> ready = stackalloc byte[9];
    ready[0] = 1;
    BinaryPrimitives.WriteInt32LittleEndian(ready.Slice(1, 4), recorder.WaveFormat.SampleRate);
    BinaryPrimitives.WriteInt32LittleEndian(ready.Slice(5, 4), recorder.WaveFormat.Channels);
    lock (outputLock)
    {
        output.Write(ready);
        output.Flush();
        Volatile.Write(ref readyWritten, 1);
    }
    await Task.Delay(Timeout.Infinite);
    return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine(error);
    return 1;
}
