namespace SoundTransmitter.Core;

public readonly record struct PlayoutStats(
    bool Playing,
    uint StreamId,
    int InputRate,
    double TargetMs,
    double LevelMs,
    double CorrectionPpm,
    long Packets,
    long Lost,
    long Late,
    long Underruns,
    long Resyncs,
    long Streams);

/// The receiver's half: packets go in from the network thread, audio comes out
/// on the sound card's thread at the card's rate.
///
/// Packets land in a <see cref="FrameRing"/> by frame index. Playback waits
/// until the target amount is buffered, then reads through the ring with the
/// resampler, stepping by (input rate / output rate) times the drift
/// controller's correction. Running dry stops playback until the buffer is
/// back to target; running far over target jumps forward instead of slewing.
///
/// Running dry also raises the target (when a ceiling above it was given), so
/// a network that keeps going quiet for longer than the buffer covers, like
/// Wi-Fi off scanning other channels, costs a dropout or two and then fits.
/// Once the network calms down, the target steps back towards where it
/// started: after a long stretch in which the buffer never came within a step
/// of empty, one step comes off. The drift controller slews the level down to
/// the new target, so nothing is skipped.
public sealed class Playout
{
    const int RingFrames = 1 << 17;          // ~2.7 s at 48 kHz
    const double ResyncExcessSeconds = 0.25; // this far over target: jump
    const double GrowthSeconds = 0.03;       // target raise per underrun, and drop per clean stretch
    const double ShrinkMargin = 0.015;       // spare it must still have had, one step lower
    public const double ShrinkAfterSeconds = 600;

    readonly object gate = new();
    readonly FrameRing ring = new(RingFrames);
    readonly DriftController drift;
    readonly int outputRate;
    readonly double startTargetSeconds;
    readonly double maxTargetSeconds;
    readonly double shrinkAfterSeconds;
    float[] decoded = new float[4096];
    float[] stereo = new float[4096];

    Resampler? resampler;
    bool haveStream;
    bool playing;
    uint streamId;
    uint? retiredStreamId;
    int inputRate;
    double nominalStep;
    long writeHead;  // one past the highest frame received
    long readIndex;  // playback position: readIndex + readFrac, in input frames
    double readFrac;
    double lastStep;
    double cleanSeconds;    // played since playback last started or the target last came down
    double lowWaterSeconds; // the least spare buffer over that time

    uint firstSeq;
    uint highestSeq;
    long streamPackets;
    long lostBefore;  // losses from earlier streams

    long packets, late, underruns, resyncs, streams;

    /// <param name="maxTargetSeconds">How far underruns may raise the target;
    /// at or below <paramref name="targetSeconds"/> it stays fixed.</param>
    /// <param name="shrinkAfterSeconds">How long a raised target must go
    /// without a close call before it comes down a step.</param>
    public Playout(int outputRate, double targetSeconds, double maxTargetSeconds = 0,
        double shrinkAfterSeconds = ShrinkAfterSeconds)
    {
        this.outputRate = outputRate;
        startTargetSeconds = targetSeconds;
        this.maxTargetSeconds = maxTargetSeconds;
        this.shrinkAfterSeconds = shrinkAfterSeconds;
        drift = new DriftController(targetSeconds);
    }

    public int OutputRate => outputRate;

    /// Input frames per output frame used by the last render (0 before any).
    public double LastStep
    {
        get { lock (gate) return lastStep; }
    }

    public int InputRate
    {
        get { lock (gate) return inputRate; }
    }

    public void OnAudio(in PacketHeader header, ReadOnlySpan<byte> payload)
    {
        lock (gate)
        {
            // Stragglers from the stream we just left, reordered behind the new one.
            if (header.StreamId == retiredStreamId && header.StreamId != streamId) return;
            if (!haveStream || header.StreamId != streamId || header.SampleRate != inputRate)
                StartStream(header);

            int frames = header.FrameCount;
            long end = header.FirstFrame + frames;
            // Late: playback has already passed the start of this packet and
            // played silence there. What's still ahead of it is worth keeping.
            if (playing && header.FirstFrame <= readIndex) late++;
            if (end <= readIndex - Resampler.HalfTaps) return;

            int samples = frames * header.Channels;
            if (decoded.Length < samples) decoded = new float[samples];
            if (stereo.Length < frames * 2) stereo = new float[frames * 2];
            if (header.Format == SampleFormat.S16) Samples.DecodeS16(payload, decoded);
            else Samples.DecodeF32(payload, decoded);
            Samples.ToStereo(decoded.AsSpan(0, samples), header.Channels, stereo);
            ring.Write(header.FirstFrame, stereo.AsSpan(0, frames * 2));

            packets++;
            streamPackets++;
            int ahead = (int)(header.Sequence - highestSeq);
            if (ahead > 0) highestSeq = header.Sequence;
            if (end > writeHead) writeHead = end;

            // A long stall on our side can leave the reader so far behind that
            // the ring wraps under it.
            if (writeHead - readIndex > RingFrames / 2) Jump();
        }
    }

    /// Fills <paramref name="stereoOut"/> (interleaved L R) with the next output frames.
    public void Render(Span<float> stereoOut)
    {
        int frames = stereoOut.Length / 2;
        lock (gate)
        {
            if (!haveStream || resampler is null)
            {
                stereoOut.Clear();
                return;
            }

            double target = drift.TargetSeconds * inputRate;
            double buffered = Buffered();

            if (!playing)
            {
                if (buffered < target)
                {
                    stereoOut.Clear();
                    return;
                }
                // After a stall the backlog arrives in a burst. Nothing is
                // playing yet, so skip what's stale and start at target rather
                // than slewing the excess off at 0.1% for a minute.
                long excess = (long)(buffered - target);
                if (excess > 0) readIndex += excess;
                buffered -= excess;
                playing = true;
                drift.Resync();
                StartCleanStretch();
            }

            if (buffered > target + ResyncExcessSeconds * inputRate)
            {
                Jump();
                buffered = Buffered();
            }

            double worstStep = nominalStep * (1 + DriftController.MaxCorrection);
            if (buffered < frames * worstStep + 1)
            {
                underruns++;
                playing = false;
                if (drift.TargetSeconds < maxTargetSeconds)
                    drift.TargetSeconds = Math.Min(maxTargetSeconds, drift.TargetSeconds + GrowthSeconds);
                stereoOut.Clear();
                return;
            }

            lowWaterSeconds = Math.Min(lowWaterSeconds, (buffered - frames * worstStep) / inputRate);
            cleanSeconds += frames / (double)outputRate;
            if (cleanSeconds >= shrinkAfterSeconds) ShrinkTarget();

            double correction = drift.Update(buffered / inputRate, frames / (double)outputRate);
            double step = nominalStep * (1 + correction);
            lastStep = step;

            for (int i = 0; i < frames; i++)
            {
                resampler.Sample(ring, readIndex, readFrac, out float l, out float r);
                stereoOut[2 * i] = l;
                stereoOut[2 * i + 1] = r;
                readFrac += step;
                long whole = (long)readFrac;
                readIndex += whole;
                readFrac -= whole;
            }
        }
    }

    public PlayoutStats Stats()
    {
        lock (gate)
        {
            long expected = haveStream ? (long)(uint)(highestSeq - firstSeq) + 1 : 0;
            long lost = lostBefore + Math.Max(0, expected - streamPackets);
            return new PlayoutStats(playing, streamId, inputRate, drift.TargetSeconds * 1000, drift.Level * 1000,
                drift.Correction * 1e6, packets, lost, late, underruns, resyncs, streams);
        }
    }

    /// Takes a step off the target if, all through the stretch just played,
    /// the buffer always had a step to spare and then some.
    void ShrinkTarget()
    {
        if (drift.TargetSeconds > startTargetSeconds + 1e-9 &&
            lowWaterSeconds >= GrowthSeconds + ShrinkMargin)
            drift.TargetSeconds = Math.Max(startTargetSeconds, drift.TargetSeconds - GrowthSeconds);
        StartCleanStretch();
    }

    void StartCleanStretch()
    {
        cleanSeconds = 0;
        lowWaterSeconds = double.PositiveInfinity;
    }

    /// Input frames held beyond what the next interpolation needs.
    double Buffered() => writeHead - (readIndex + readFrac) - Resampler.HalfTaps;

    void Jump()
    {
        long target = (long)(drift.TargetSeconds * inputRate);
        readIndex = writeHead - Resampler.HalfTaps - target;
        readFrac = 0;
        resyncs++;
        drift.Resync();
    }

    void StartStream(in PacketHeader header)
    {
        if (haveStream)
        {
            long expected = (long)(uint)(highestSeq - firstSeq) + 1;
            lostBefore += Math.Max(0, expected - streamPackets);
            if (header.StreamId != streamId) retiredStreamId = streamId;
        }
        streams++;
        haveStream = true;
        playing = false;
        streamId = header.StreamId;
        if (header.SampleRate != inputRate || resampler is null)
        {
            inputRate = header.SampleRate;
            nominalStep = (double)inputRate / outputRate;
            resampler = new Resampler(Resampler.CutoffFor(nominalStep));
        }
        ring.Clear();
        drift.Reset();
        writeHead = header.FirstFrame;
        readIndex = header.FirstFrame;
        readFrac = 0;
        firstSeq = highestSeq = header.Sequence;
        streamPackets = 0;
    }
}
