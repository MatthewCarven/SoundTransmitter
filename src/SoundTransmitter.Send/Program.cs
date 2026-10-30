using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using SoundTransmitter.Core;

namespace SoundTransmitter.Send;

static class Program
{
    const string Usage = """
        st-send: streams whatever a Windows playback device is playing to st-recv.

          st-send --to <host[:port]> [options]
          st-send --list

          --to <host[:port]>  the receiver (port default 47800)
          --device <name>     playback device to capture (default: the default device)
          --port <n>          local UDP port (default 47801)
          --format s16|f32    wire format (default s16)
          --tone <hz>         also play a quiet test tone into the device
          --tone-db <db>      test tone level, dB full scale (default -30)
          --seconds <n>       stop after this long
          --log <file>        append log lines to this file as well
        """;

    static int Main(string[] argv)
    {
        Args args;
        try
        {
            args = new Args(argv, ["to", "device", "port", "format", "tone", "tone-db", "seconds", "log"], ["list", "help"]);
        }
        catch (ArgumentException e)
        {
            Console.Error.WriteLine(e.Message);
            Console.Error.WriteLine(Usage);
            return 2;
        }
        if (args.Has("help"))
        {
            Console.WriteLine(Usage);
            return 0;
        }
        if (args.Has("list"))
        {
            Endpoints.PrintList();
            return 0;
        }
        if (args.Get("to") is not { } to)
        {
            Console.Error.WriteLine(Usage);
            return 2;
        }
        if (args.Get("log") is { } log) Log.ToFile(log);

        // The capture thread polls every 5 ms; at the default 15.6 ms timer
        // tick each of those sleeps would be three times as long.
        TimeBeginPeriod(1);
        Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.AboveNormal;

        using var stop = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            stop.Cancel();
        };
        if (args.Get("seconds") is not null) stop.CancelAfter(TimeSpan.FromSeconds(args.Double("seconds", 0)));

        try
        {
            var format = (args.Get("format") ?? "s16").ToLowerInvariant() switch
            {
                "s16" => SampleFormat.S16,
                "f32" => SampleFormat.F32,
                var f => throw new ArgumentException($"unknown format \"{f}\""),
            };
            var sender = new Sender(
                Net.Resolve(to, Net.ReceiverPort),
                args.Int("port", Net.SenderPort),
                args.Get("device"),
                format,
                args.Double("tone", 0),
                args.Double("tone-db", -30));
            sender.Run(stop.Token);
            return 0;
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or SocketException)
        {
            Log.Line("error: " + e.Message);
            return 1;
        }
    }

    [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    static extern uint TimeBeginPeriod(uint milliseconds);
}

sealed class Sender(IPEndPoint receiver, int localPort, string? deviceName, SampleFormat format, double toneHz, double toneDb)
{
    static readonly TimeSpan ReceiverTimeout = TimeSpan.FromSeconds(5);

    readonly Socket socket = Net.OpenUdp(localPort);
    readonly byte[] control = new byte[PacketHeader.Size];
    long lastHelloTicks = long.MinValue;
    volatile bool receiverActive;
    uint beaconSequence;
    long packetsSent, sendErrors, framesCaptured;
    double longestCaptureGapMs;

    public void Run(CancellationToken stop)
    {
        Log.Line($"sending to {receiver}, from UDP port {localPort}");
        var rx = new Thread(() => ReceiveHellos(stop)) { IsBackground = true, Name = "hellos" };
        rx.Start();

        while (!stop.IsCancellationRequested)
        {
            try
            {
                RunCapture(stop);
            }
            catch (Exception e) when (e is COMException or InvalidOperationException)
            {
                Log.Line($"capture stopped: {e.Message}");
            }
            if (!stop.IsCancellationRequested)
            {
                Log.Line("reopening in 2 s");
                stop.WaitHandle.WaitOne(2000);
            }
        }
        Log.Line("stopped");
    }

    void RunCapture(CancellationToken stop)
    {
        var device = Endpoints.Find(deviceName);
        Log.Line("capturing " + Endpoints.Describe(device));

        using var capture = new LoopbackCapture(device);
        var wf = capture.WaveFormat;
        AudioFormats.RequireFloat32(wf, device.FriendlyName);

        uint streamId = (uint)Random.Shared.NextInt64(1, uint.MaxValue);
        int framesPerPacket = Packetizer.DefaultFramesPerPacket(format);
        var packetizer = new Packetizer(streamId, wf.SampleRate, format, framesPerPacket);
        var stereo = Array.Empty<float>();
        long lastCallback = 0;

        // Loopback only delivers while something is playing. A stream of our
        // own keeps it running, so the receiver sees one steady clock; it's
        // silence unless a test tone was asked for.
        using var keepAlive = new WasapiOut(device, AudioClientShareMode.Shared, true, 50);
        keepAlive.Init(new ToneProvider(device.AudioClient.MixFormat, toneHz, toneDb));
        keepAlive.Play();

        capture.Start(floats =>
        {
            // Data should turn up every 10 ms or so. A long pause here means the
            // desktop itself stalled, not the network.
            long now = Stopwatch.GetTimestamp();
            if (lastCallback != 0)
            {
                double gapMs = Stopwatch.GetElapsedTime(lastCallback, now).TotalMilliseconds;
                if (gapMs > longestCaptureGapMs) longestCaptureGapMs = gapMs;
                if (gapMs > 40) Log.Line($"capture gap: {gapMs:F0} ms between deliveries");
            }
            lastCallback = now;

            int frames = floats.Length / wf.Channels;
            if (stereo.Length < frames * 2) stereo = new float[frames * 2];
            Samples.ToStereo(floats, wf.Channels, stereo);
            framesCaptured += frames;
            packetizer.Add(stereo.AsSpan(0, frames * 2), SendAudio);
        });
        Log.Line($"stream {streamId:X8}: {wf.SampleRate} Hz {wf.Channels} ch in, {format} stereo out, " +
            $"{framesPerPacket} frames a packet{(toneHz > 0 ? $", test tone {toneHz} Hz at {toneDb} dB" : "")}");

        var clock = Stopwatch.StartNew();
        var nextBeacon = TimeSpan.Zero;
        var nextReport = TimeSpan.FromSeconds(10);
        long reportedPackets = 0, reportedDiscontinuities = 0;
        while (!stop.IsCancellationRequested && !capture.Stopped.IsSet)
        {
            WaitHandle.WaitAny([stop.WaitHandle, capture.Stopped.WaitHandle], 200);
            var now = clock.Elapsed;

            if (capture.Discontinuities > reportedDiscontinuities)
            {
                reportedDiscontinuities = capture.Discontinuities;
                Log.Line($"capture overflow #{reportedDiscontinuities}: Windows dropped audio before we read it");
            }
            if (receiverActive && Stopwatch.GetElapsedTime(Interlocked.Read(ref lastHelloTicks)) > ReceiverTimeout)
            {
                receiverActive = false;
                Log.Line("receiver went quiet; waiting for it");
            }
            if (!receiverActive && now >= nextBeacon)
            {
                SendControl(PacketType.Beacon, streamId, beaconSequence++);
                nextBeacon = now + TimeSpan.FromSeconds(1);
            }
            if (now >= nextReport)
            {
                long sent = Interlocked.Read(ref packetsSent);
                Log.Line($"{(receiverActive ? "streaming" : "idle")} | captured {framesCaptured / (double)wf.SampleRate:F0} s | " +
                    $"sent {sent} packets (+{sent - reportedPackets}) | send errors {sendErrors} | " +
                    $"longest capture gap {longestCaptureGapMs:F0} ms | overflows {capture.Discontinuities}");
                reportedPackets = sent;
                longestCaptureGapMs = 0;
                nextReport = now + TimeSpan.FromSeconds(10);
            }
        }

        capture.Stop();
        keepAlive.Stop();
        if (capture.Failure is { } failure) throw new InvalidOperationException(failure.Message, failure);
    }

    void SendAudio(ReadOnlySpan<byte> datagram)
    {
        if (!receiverActive) return;
        try
        {
            socket.SendTo(datagram, SocketFlags.None, receiver);
            Interlocked.Increment(ref packetsSent);
        }
        catch (SocketException)
        {
            Interlocked.Increment(ref sendErrors);
        }
    }

    void SendControl(PacketType type, uint streamId, uint sequence)
    {
        PacketHeader.Control(type, streamId, sequence).Write(control);
        try { socket.SendTo(control, SocketFlags.None, receiver); }
        catch (SocketException) { Interlocked.Increment(ref sendErrors); }
    }

    void ReceiveHellos(CancellationToken stop)
    {
        var buffer = new byte[2048];
        socket.ReceiveTimeout = 500;
        EndPoint from = new IPEndPoint(IPAddress.Any, 0);
        while (!stop.IsCancellationRequested)
        {
            int n;
            try { n = socket.ReceiveFrom(buffer, ref from); }
            catch (SocketException) { continue; }
            if (!((IPEndPoint)from).Address.Equals(receiver.Address)) continue;
            if (!PacketHeader.TryParse(buffer.AsSpan(0, n), out var h, out _) || h.Type != PacketType.Hello) continue;

            Interlocked.Exchange(ref lastHelloTicks, Stopwatch.GetTimestamp());
            if (!receiverActive)
            {
                receiverActive = true;
                Log.Line($"receiver {from} is listening; streaming");
            }
        }
    }
}

delegate void FramesHandler(ReadOnlySpan<float> interleaved);

/// WASAPI loopback of a playback device, polled every 5 ms by a thread that
/// Windows schedules as pro audio (MMCSS). The buffer is 200 ms, so if the
/// desktop stalls, packets go out late instead of audio being lost.
sealed class LoopbackCapture : IDisposable
{
    const long BufferDuration = 200 * 10_000; // 100 ns units

    readonly AudioClient client;
    Thread? thread;
    volatile bool stopping;
    long discontinuities;

    public WaveFormat WaveFormat { get; }
    public ManualResetEventSlim Stopped { get; } = new();
    public Exception? Failure { get; private set; }
    /// Times the engine flagged a gap: it had to drop audio we hadn't read.
    public long Discontinuities => Interlocked.Read(ref discontinuities);

    public LoopbackCapture(MMDevice device)
    {
        client = device.AudioClient;
        WaveFormat = client.MixFormat;
        client.Initialize(AudioClientShareMode.Shared, AudioClientStreamFlags.Loopback, BufferDuration, 0, WaveFormat, Guid.Empty);
    }

    public void Start(FramesHandler onData)
    {
        thread = new Thread(() => Loop(onData)) { IsBackground = true, Priority = ThreadPriority.Highest, Name = "loopback" };
        thread.Start();
    }

    public void Stop()
    {
        stopping = true;
        thread?.Join(2000);
    }

    public void Dispose()
    {
        Stop();
        client.Dispose();
        Stopped.Dispose();
    }

    void Loop(FramesHandler onData)
    {
        uint taskIndex = 0;
        IntPtr mmcss = AvSetMmThreadCharacteristics("Pro Audio", ref taskIndex);
        try
        {
            var capture = client.AudioCaptureClient;
            int channels = WaveFormat.Channels;
            var scratch = new float[WaveFormat.SampleRate * channels / 10];
            bool first = true;
            client.Start();
            while (!stopping)
            {
                Thread.Sleep(5);
                while (!stopping && capture.GetNextPacketSize() > 0)
                {
                    IntPtr data = capture.GetBuffer(out int frames, out var flags);
                    int samples = frames * channels;
                    if (scratch.Length < samples) scratch = new float[samples];
                    if ((flags & AudioClientBufferFlags.Silent) != 0) Array.Clear(scratch, 0, samples);
                    else Marshal.Copy(data, scratch, 0, samples);
                    // The first packet of a stream is routinely flagged.
                    if ((flags & AudioClientBufferFlags.DataDiscontinuity) != 0 && !first)
                        Interlocked.Increment(ref discontinuities);
                    first = false;
                    capture.ReleaseBuffer(frames);
                    onData(scratch.AsSpan(0, samples));
                }
            }
            client.Stop();
        }
        catch (Exception e)
        {
            Failure = e;
        }
        finally
        {
            if (mmcss != IntPtr.Zero) AvRevertMmThreadCharacteristics(mmcss);
            Stopped.Set();
        }
    }

    [DllImport("avrt.dll", CharSet = CharSet.Unicode, EntryPoint = "AvSetMmThreadCharacteristicsW")]
    static extern IntPtr AvSetMmThreadCharacteristics(string taskName, ref uint taskIndex);

    [DllImport("avrt.dll")]
    static extern bool AvRevertMmThreadCharacteristics(IntPtr handle);
}

/// A sine at the device's own format, or silence when the frequency is zero.
sealed class ToneProvider(WaveFormat format, double hz, double db) : IWaveProvider
{
    readonly double amplitude = hz > 0 ? Math.Pow(10, db / 20) : 0;
    readonly double delta = 2 * Math.PI * hz / format.SampleRate;
    double phase;

    public WaveFormat WaveFormat => format;

    public int Read(byte[] buffer, int offset, int count)
    {
        int channels = format.Channels;
        int frames = count / (4 * channels);
        var samples = MemoryMarshal.Cast<byte, float>(buffer.AsSpan(offset, frames * channels * 4));
        for (int i = 0; i < frames; i++)
        {
            float v = (float)(amplitude * Math.Sin(phase));
            phase += delta;
            if (phase > 2 * Math.PI) phase -= 2 * Math.PI;
            for (int c = 0; c < channels; c++) samples[i * channels + c] = v;
        }
        return frames * channels * 4;
    }
}
