using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using SoundTransmitter.Core;

namespace SoundTransmitter.Recv;

static class Program
{
    const string Usage = """
        st-recv: plays the stream from st-send on a local playback device.

          st-recv --from <host[:port]> [options]
          st-recv --list

          --from <host[:port]>  the sender (port default 47801)
          --port <n>            local UDP port (default 47800)
          --device <name>       playback device (default: the default device)
          --latency <ms>        starting buffer target (default 100)
          --max-latency <ms>    each underrun raises the target 30 ms, up to this (default 300)
          --mute                run everything but play silence (for testing)
          --check-tone <hz>     count glitches in a test tone from st-send --tone
          --seconds <n>         stop after this long and print a summary
          --log <file>          append log lines to this file as well
        """;

    static int Main(string[] argv)
    {
        Args args;
        try
        {
            args = new Args(argv, ["from", "port", "device", "latency", "max-latency", "check-tone", "seconds", "log"], ["mute", "list", "help"]);
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
        if (args.Get("from") is not { } from)
        {
            Console.Error.WriteLine(Usage);
            return 2;
        }
        if (args.Get("log") is { } log) Log.ToFile(log);
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
            var receiver = new Receiver(
                Net.Resolve(from, Net.SenderPort),
                args.Int("port", Net.ReceiverPort),
                args.Get("device"),
                args.Double("latency", 100) / 1000,
                args.Double("max-latency", 300) / 1000,
                args.Has("mute"),
                args.Double("check-tone", 0));
            return receiver.Run(stop.Token);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or SocketException or COMException)
        {
            Log.Line("error: " + e.Message);
            return 1;
        }
    }
}

sealed class Receiver(IPEndPoint sender, int localPort, string? deviceName, double latency, double maxLatency, bool mute, double toneHz)
{
    /// Packets normally come every 5 ms, in clumps of two or three as the
    /// sender's capture polls. A silence longer than this is worth a log line.
    const double GapLogMs = 40;

    readonly Socket socket = Net.OpenUdp(localPort);
    long beacons, foreign, malformed, gaps;

    public int Run(CancellationToken stop)
    {
        var device = Endpoints.Find(deviceName);
        var mix = device.AudioClient.MixFormat;
        AudioFormats.RequireFloat32(mix, device.FriendlyName);

        var playout = new Playout(mix.SampleRate, latency, maxLatency);
        var tone = toneHz > 0 ? new ToneChecker(toneHz, mix.SampleRate) : null;
        var provider = new PlayoutProvider(playout, mix, mute, tone);

        var rx = new Thread(() => Receive(playout, stop)) { IsBackground = true, Name = "receive" };
        rx.Start();

        using var output = new WasapiOut(device, AudioClientShareMode.Shared, true, 30);
        Exception? failure = null;
        output.PlaybackStopped += (_, e) => failure = e.Exception;
        output.Init(provider);
        output.Play();
        Log.Line($"listening on UDP {localPort} for {sender}; playing to {Endpoints.Describe(device)}; " +
            $"target buffer {latency * 1000:F0} ms (may grow to {Math.Max(latency, maxLatency) * 1000:F0}){(mute ? "; MUTED" : "")}");

        var hello = new byte[PacketHeader.Size];
        uint helloSequence = 0;
        var clock = Stopwatch.StartNew();
        var nextHello = TimeSpan.Zero;
        var nextReport = TimeSpan.FromSeconds(5);
        long reportedPackets = 0;
        long reportedUnderruns = 0;

        while (!stop.IsCancellationRequested && failure is null)
        {
            stop.WaitHandle.WaitOne(100);
            var now = clock.Elapsed;
            var current = playout.Stats();
            if (current.Underruns > reportedUnderruns)
            {
                Log.Line($"underrun #{current.Underruns}: buffer ran dry; rebuffering to {current.TargetMs:F0} ms");
                reportedUnderruns = current.Underruns;
            }
            if (now >= nextHello)
            {
                PacketHeader.Control(PacketType.Hello, 0, helloSequence++).Write(hello);
                try { socket.SendTo(hello, SocketFlags.None, sender); } catch (SocketException) { }
                nextHello = now + TimeSpan.FromSeconds(1);
            }
            if (now >= nextReport)
            {
                var s = playout.Stats();
                Log.Line(Describe(s, s.Packets - reportedPackets, tone));
                reportedPackets = s.Packets;
                nextReport = now + TimeSpan.FromSeconds(5);
            }
        }

        output.Stop();
        var final = playout.Stats();
        Log.Line("summary: " + Describe(final, null, tone) +
            $" | gaps over {GapLogMs:F0} ms {gaps} | beacons {beacons} | foreign {foreign} | malformed {malformed} | ran {clock.Elapsed.TotalSeconds:F0} s");
        if (failure != null)
        {
            Log.Line("playback failed: " + failure.Message);
            return 1;
        }
        return 0;
    }

    static string Describe(PlayoutStats s, long? recent, ToneChecker? tone)
    {
        string state = s.Streams == 0 ? "waiting" : s.Playing ? "playing" : "buffering";
        string text = $"{state} | buffer {s.LevelMs:F1} / {s.TargetMs:F0} ms | drift {s.CorrectionPpm:+0;-0} ppm | {s.InputRate} Hz in | " +
            $"packets {s.Packets}{(recent is { } r ? $" (+{r})" : "")} lost {s.Lost} late {s.Late} | " +
            $"underruns {s.Underruns} resyncs {s.Resyncs} streams {s.Streams}";
        if (tone != null)
            text += $" | tone {tone.Blocks} blocks, {tone.Glitches} glitches, worst clean {tone.WorstCleanResidualDb:F0} dB";
        return text;
    }

    void Receive(Playout playout, CancellationToken stop)
    {
        var buffer = new byte[65536];
        socket.ReceiveTimeout = 500;
        EndPoint from = new IPEndPoint(IPAddress.Any, 0);
        long lastAudio = 0;
        while (!stop.IsCancellationRequested)
        {
            int n;
            try { n = socket.ReceiveFrom(buffer, ref from); }
            catch (SocketException) { continue; }
            if (!((IPEndPoint)from).Address.Equals(sender.Address))
            {
                foreign++;
                continue;
            }
            if (!PacketHeader.TryParse(buffer.AsSpan(0, n), out var h, out var payload))
            {
                malformed++;
                continue;
            }
            if (h.Type == PacketType.Audio)
            {
                long now = Stopwatch.GetTimestamp();
                if (lastAudio != 0)
                {
                    double gapMs = Stopwatch.GetElapsedTime(lastAudio, now).TotalMilliseconds;
                    if (gapMs > GapLogMs)
                    {
                        gaps++;
                        Log.Line($"network gap: {gapMs:F0} ms with no packets (before #{h.Sequence})");
                    }
                }
                lastAudio = now;
                playout.OnAudio(h, payload);
            }
            else if (h.Type == PacketType.Beacon)
            {
                beacons++;
                lastAudio = 0;
            }
        }
    }
}

/// What WASAPI pulls from: the playout's stereo, spread over the device's channels.
sealed class PlayoutProvider(Playout playout, WaveFormat format, bool mute, ToneChecker? tone) : IWaveProvider
{
    float[] stereo = [];

    public WaveFormat WaveFormat => format;

    public int Read(byte[] buffer, int offset, int count)
    {
        int channels = format.Channels;
        int frames = count / (4 * channels);
        if (stereo.Length < frames * 2) stereo = new float[frames * 2];
        var st = stereo.AsSpan(0, frames * 2);
        playout.Render(st);
        tone?.Feed(st, playout.LastStep, playout.InputRate);

        var output = MemoryMarshal.Cast<byte, float>(buffer.AsSpan(offset, frames * channels * 4));
        if (mute)
        {
            output.Clear();
        }
        else if (channels == 2)
        {
            st.CopyTo(output);
        }
        else
        {
            output.Clear();
            for (int i = 0; i < frames; i++)
            {
                if (channels == 1)
                {
                    output[i] = 0.5f * (st[2 * i] + st[2 * i + 1]);
                }
                else
                {
                    output[i * channels] = st[2 * i];
                    output[i * channels + 1] = st[2 * i + 1];
                }
            }
        }
        return frames * channels * 4;
    }
}
