using SoundTransmitter.Core;

namespace SoundTransmitter.Tests;

/// The receiver against a simulated network: a sender whose clock runs off by
/// some ppm, packets that arrive late, out of order or not at all, and a sound
/// card pulling 10 ms at a time on its own exact clock. The audio is a tone, so
/// the <see cref="ToneChecker"/> can judge the output.
///
/// Rates are scaled down (11025 in, 12000 out: the 44.1 -> 48 kHz ratio) to keep
/// ten simulated minutes quick; nothing in the receiver depends on the absolute rate.
public class PlayoutSimTests
{
    const int InRate = 11025;
    const int OutRate = 12000;
    const int PacketFrames = 60;    // ~5.4 ms
    const int CallbackFrames = 120; // 10 ms
    const double ToneHz = 440;
    const double Target = 0.040;

    sealed class Sim
    {
        public Playout Playout { get; init; } = new(OutRate, Target);
        public readonly ToneChecker Tone = new(ToneHz, OutRate);
        public readonly List<(double t, PlayoutStats s, long glitches)> Samples = new();

        public double DriftPpm;
        public double JitterMs = 10;
        public double LateFraction = 0.01;  // of packets, delayed a further LateMs
        public double LateMs = 20;          // worst case 30 ms: inside the 40 ms target
        public double LossFraction;
        public Func<double, bool> Stalled = _ => false; // network holds everything
        public double RestartAt = double.PositiveInfinity;

        readonly Random rng = new(1234);
        readonly PriorityQueue<byte[], double> inFlight = new();
        readonly List<byte[]> held = new();

        public void Run(double seconds)
        {
            double senderRate = InRate * (1 + DriftPpm * 1e-6);
            var packetizer = new Packetizer(1, InRate, SampleFormat.S16, PacketFrames);
            bool restarted = false;
            long frame = 0;
            var chunk = new float[PacketFrames * 2];
            var output = new float[CallbackFrames * 2];
            double renderEvery = CallbackFrames / (double)OutRate;
            double nextRender = 0, nextSample = 1;

            for (double t = 0; t < seconds; t += 0.001)
            {
                if (!restarted && t >= RestartAt)
                {
                    restarted = true;
                    packetizer = new Packetizer(2, InRate, SampleFormat.S16, PacketFrames);
                }

                // The sender finishes a packet each time its clock has captured one.
                while ((frame + PacketFrames) / senderRate <= t)
                {
                    for (int i = 0; i < PacketFrames; i++)
                        chunk[2 * i] = chunk[2 * i + 1] = (float)(0.5 * Math.Sin(2 * Math.PI * ToneHz * (frame + i) / InRate));
                    double sentAt = (frame + PacketFrames) / senderRate;
                    frame += PacketFrames;
                    packetizer.Add(chunk, d =>
                    {
                        if (rng.NextDouble() < LossFraction) return;
                        double delay = rng.NextDouble() * JitterMs / 1000;
                        if (rng.NextDouble() < LateFraction) delay += LateMs / 1000;
                        inFlight.Enqueue(d.ToArray(), sentAt + delay);
                    });
                }

                bool stalled = Stalled(t);
                while (inFlight.TryPeek(out var d, out double arrives) && arrives <= t)
                {
                    inFlight.Dequeue();
                    if (stalled) held.Add(d);
                    else Deliver(d);
                }
                if (!stalled && held.Count > 0)
                {
                    foreach (var d in held) Deliver(d);
                    held.Clear();
                }

                while (nextRender <= t)
                {
                    Playout.Render(output);
                    Tone.Feed(output, Playout.LastStep, InRate);
                    nextRender += renderEvery;
                }

                if (t >= nextSample)
                {
                    Samples.Add((t, Playout.Stats(), Tone.Glitches));
                    nextSample += 1;
                }
            }
        }

        void Deliver(byte[] d)
        {
            Assert.True(PacketHeader.TryParse(d, out var h, out var payload));
            Playout.OnAudio(h, payload);
        }

        public (double level, double ppm) MeanOverLast(double seconds)
        {
            double end = Samples[^1].t;
            var tail = Samples.Where(x => x.t > end - seconds).Select(x => x.s).ToList();
            return (tail.Average(s => s.LevelMs), tail.Average(s => s.CorrectionPpm));
        }
    }

    [Theory]
    [InlineData(100)]
    [InlineData(-100)]
    [InlineData(0)]
    public void DriftIsTrackedWithoutAGlitch(double ppm)
    {
        var sim = new Sim { DriftPpm = ppm };
        sim.Run(600);
        var s = sim.Playout.Stats();
        var (level, measured) = sim.MeanOverLast(120);

        Assert.Equal(0, s.Underruns);
        Assert.Equal(0, s.Resyncs);
        Assert.Equal(0, s.Late);
        Assert.Equal(0, sim.Tone.Glitches);
        Assert.True(sim.Tone.Blocks > 55_000, $"only {sim.Tone.Blocks} blocks checked");
        Assert.InRange(level, Target * 1000 - 2, Target * 1000 + 2);
        Assert.InRange(measured, ppm - 10, ppm + 10);
        // Peak excursion of the buffer over the whole run stays well clear of empty.
        double minLevel = sim.Samples.Skip(5).Min(x => x.s.LevelMs);
        Assert.True(minLevel > 25, $"buffer fell to {minLevel:F1} ms");
    }

    [Fact]
    public void PacketsLaterThanTheBufferAreCountedLateAndHeard()
    {
        // 1% of packets 60 ms late against a 40 ms buffer.
        var sim = new Sim { LateMs = 60 };
        sim.Run(60);
        var s = sim.Playout.Stats();
        double expectedLate = 60.0 * InRate / PacketFrames * 0.01;
        Assert.InRange(s.Late, expectedLate * 0.6, expectedLate * 1.4);
        Assert.True(sim.Tone.Glitches >= s.Late / 2, $"{s.Late} late, {sim.Tone.Glitches} glitches");
        Assert.Equal(0, s.Underruns);
    }

    [Fact]
    public void LostPacketsAreCountedAndPlaybackCarriesOn()
    {
        var sim = new Sim { LossFraction = 0.01 };
        sim.Run(60);
        var s = sim.Playout.Stats();
        double expectedLost = 60.0 * InRate / PacketFrames * 0.01;
        Assert.InRange(s.Lost, expectedLost * 0.6, expectedLost * 1.4);
        Assert.Equal(0, s.Underruns);
        Assert.True(sim.Tone.Glitches > 0, "lost packets should show as glitches");
        Assert.True(s.Playing);
    }

    [Fact]
    public void ANetworkStallRecovers()
    {
        var sim = new Sim { Stalled = t => t is >= 20 and < 20.3 };
        sim.Run(60);
        var s = sim.Playout.Stats();
        Assert.True(s.Underruns >= 1);
        Assert.True(s.Playing);
        // The burst after the stall is skipped, not slewed off: back on target
        // within a couple of seconds, not a minute.
        var after = sim.Samples.Where(x => x.t is > 23 and < 30).Select(x => x.s.LevelMs).ToList();
        Assert.All(after, l => Assert.InRange(l, Target * 1000 - 5, Target * 1000 + 5));
        Assert.True(sim.Tone.Glitches < 100, $"{sim.Tone.Glitches} glitch blocks for one 300 ms stall");
    }

    [Fact]
    public void RepeatedStallsRaiseTheTargetUntilTheyFit()
    {
        // A 70 ms silence every 20 s, as when Wi-Fi goes off to scan, against a
        // 40 ms starting target that may grow to 250. The target is free to come
        // down after a minute without a close call, but every minute has one, so
        // it stays up rather than see-sawing through more dropouts.
        var sim = new Sim
        {
            Playout = new Playout(OutRate, Target, 0.25, shrinkAfterSeconds: 60),
            Stalled = t => t % 20 is >= 10 and < 10.07,
        };
        sim.Run(600);
        var s = sim.Playout.Stats();
        long underrunsByMinute2 = sim.Samples.First(x => x.t >= 120).s.Underruns;

        Assert.InRange(s.Underruns, 1, 3);
        Assert.Equal(underrunsByMinute2, s.Underruns); // none once it has grown
        Assert.InRange(s.TargetMs, 70, 130);
        Assert.Equal(0, s.Late);
    }

    [Fact]
    public void OnceTheNetworkCalmsDownTheTargetComesBackDown()
    {
        // Two long silences early on raise a 100 ms target by a step each; then
        // the network is calm. With a 60 s clean stretch per step, it should be
        // back to 100 ms, and no lower, without a dropout or glitch on the way.
        const double start = 0.1;
        var sim = new Sim
        {
            Playout = new Playout(OutRate, start, 0.25, shrinkAfterSeconds: 60),
            Stalled = t => t is >= 10 and < 10.3 or >= 30 and < 30.3,
        };
        sim.Run(300);
        var s = sim.Playout.Stats();
        var settled = sim.Samples.First(x => x.t >= 35);

        Assert.Equal(160, sim.Samples.Max(x => x.s.TargetMs), 6);
        Assert.Equal(start * 1000, s.TargetMs, 6);
        Assert.Equal(settled.s.Underruns, s.Underruns);
        Assert.Equal(settled.glitches, sim.Tone.Glitches);
        Assert.InRange(sim.MeanOverLast(30).level, start * 1000 - 2, start * 1000 + 2);
    }

    [Fact]
    public void WithoutACeilingTheTargetStaysPut()
    {
        var sim = new Sim { Stalled = t => t % 20 is >= 10 and < 10.07 };
        sim.Run(60);
        Assert.Equal(Target * 1000, sim.Playout.Stats().TargetMs, 6);
    }

    [Fact]
    public void ASenderRestartStartsAFreshStream()
    {
        var sim = new Sim { RestartAt = 15 };
        sim.Run(40);
        var s = sim.Playout.Stats();
        Assert.Equal(2, s.Streams);
        Assert.Equal(2u, s.StreamId);
        Assert.True(s.Playing);
        Assert.Equal(0, s.Lost);
    }
}
