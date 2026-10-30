namespace SoundTransmitter.Core;

public delegate void DatagramHandler(ReadOnlySpan<byte> datagram);

/// Cuts a stream of stereo frames, arriving in whatever chunk sizes the
/// capture hands over, into fixed-size audio packets.
public sealed class Packetizer
{
    readonly uint streamId;
    readonly int sampleRate;
    readonly SampleFormat format;
    readonly int framesPerPacket;
    readonly float[] pending;
    readonly byte[] datagram;
    int pendingFrames;
    uint rng = 0x9E3779B9;

    /// Index of the next frame to go into a packet; the frame count so far.
    public long NextFrame { get; private set; }
    public uint NextSequence { get; private set; }

    public Packetizer(uint streamId, int sampleRate, SampleFormat format, int framesPerPacket)
    {
        if (framesPerPacket is < 1 or > ushort.MaxValue) throw new ArgumentOutOfRangeException(nameof(framesPerPacket));
        this.streamId = streamId;
        this.sampleRate = sampleRate;
        this.format = format;
        this.framesPerPacket = framesPerPacket;
        pending = new float[framesPerPacket * 2];
        datagram = new byte[PacketHeader.Size + framesPerPacket * 2 * PacketHeader.BytesPerSample(format)];
    }

    /// Frames per packet that keep one packet inside a single Ethernet frame
    /// (about 960 bytes of payload for stereo).
    public static int DefaultFramesPerPacket(SampleFormat format) => format == SampleFormat.F32 ? 120 : 240;

    public void Add(ReadOnlySpan<float> stereo, DatagramHandler emit)
    {
        while (!stereo.IsEmpty)
        {
            int take = Math.Min(framesPerPacket - pendingFrames, stereo.Length / 2);
            stereo[..(take * 2)].CopyTo(pending.AsSpan(pendingFrames * 2));
            pendingFrames += take;
            stereo = stereo[(take * 2)..];
            if (pendingFrames == framesPerPacket) Flush(emit);
        }
    }

    void Flush(DatagramHandler emit)
    {
        var header = new PacketHeader(PacketType.Audio, streamId, NextSequence, NextFrame,
            sampleRate, 2, format, (ushort)framesPerPacket);
        header.Write(datagram);
        var payload = datagram.AsSpan(PacketHeader.Size);
        if (format == SampleFormat.S16) Samples.EncodeS16(pending, payload, ref rng);
        else Samples.EncodeF32(pending, payload);

        NextSequence++;
        NextFrame += framesPerPacket;
        pendingFrames = 0;
        emit(datagram);
    }
}
