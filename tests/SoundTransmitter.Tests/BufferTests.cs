using SoundTransmitter.Core;

namespace SoundTransmitter.Tests;

public class PacketizerTests
{
    [Fact]
    public void OddChunksBecomeConsecutiveFixedPackets()
    {
        var p = new Packetizer(5, 48000, SampleFormat.F32, 4);
        var got = new List<(PacketHeader h, float[] samples)>();
        void Emit(ReadOnlySpan<byte> d)
        {
            Assert.True(PacketHeader.TryParse(d, out var h, out var payload));
            var s = new float[payload.Length / 4];
            Samples.DecodeF32(payload, s);
            got.Add((h, s));
        }

        // 11 frames in chunks of 3, 1 and 7: two full packets, 3 frames pending.
        var frames = Enumerable.Range(0, 22).Select(i => (float)i).ToArray();
        p.Add(frames.AsSpan(0, 6), Emit);
        p.Add(frames.AsSpan(6, 2), Emit);
        p.Add(frames.AsSpan(8, 14), Emit);

        Assert.Equal(2, got.Count);
        Assert.Equal(0L, got[0].h.FirstFrame);
        Assert.Equal(4L, got[1].h.FirstFrame);
        Assert.Equal(0u, got[0].h.Sequence);
        Assert.Equal(1u, got[1].h.Sequence);
        Assert.Equal(frames[..8], got[0].samples);
        Assert.Equal(frames[8..16], got[1].samples);
        Assert.Equal(8L, p.NextFrame);
    }

    [Fact]
    public void DefaultPacketsFitOneEthernetFrame()
    {
        foreach (var f in new[] { SampleFormat.S16, SampleFormat.F32 })
        {
            int bytes = PacketHeader.Size + Packetizer.DefaultFramesPerPacket(f) * 2 * PacketHeader.BytesPerSample(f);
            Assert.True(bytes <= 1472, $"{f}: {bytes} bytes");
        }
    }
}

public class FrameRingTests
{
    [Fact]
    public void MissingAndOverwrittenFramesReadAsSilence()
    {
        var ring = new FrameRing(8);
        ring.Write(10, new[] { 1f, 2f, 3f, 4f });
        ring.Read(10, out var l, out var r);
        Assert.Equal((1f, 2f), (l, r));
        ring.Read(11, out l, out r);
        Assert.Equal((3f, 4f), (l, r));

        ring.Read(12, out l, out r);
        Assert.Equal((0f, 0f), (l, r));
        Assert.False(ring.Has(9));

        ring.Write(18, new[] { 9f, 9f }); // same slot as 10
        Assert.False(ring.Has(10));
        ring.Read(10, out l, out _);
        Assert.Equal(0f, l);
        Assert.True(ring.Has(18));
    }
}
