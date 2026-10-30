using SoundTransmitter.Core;

namespace SoundTransmitter.Tests;

public class ProtocolTests
{
    static byte[] AudioDatagram(out PacketHeader header)
    {
        header = new PacketHeader(PacketType.Audio, 0xDEADBEEF, 41, 123_456_789_012, 44100, 2, SampleFormat.S16, 3);
        var bytes = new byte[PacketHeader.Size + header.PayloadBytes];
        header.Write(bytes);
        for (int i = PacketHeader.Size; i < bytes.Length; i++) bytes[i] = (byte)i;
        return bytes;
    }

    [Fact]
    public void AudioHeaderRoundTrips()
    {
        var bytes = AudioDatagram(out var written);
        Assert.True(PacketHeader.TryParse(bytes, out var read, out var payload));
        Assert.Equal(written, read);
        Assert.Equal(12, payload.Length);
        Assert.Equal((byte)PacketHeader.Size, payload[0]);
    }

    [Fact]
    public void ControlPacketsCarryNoPayload()
    {
        var bytes = new byte[PacketHeader.Size];
        PacketHeader.Control(PacketType.Hello, 7, 9).Write(bytes);
        Assert.True(PacketHeader.TryParse(bytes, out var read, out var payload));
        Assert.Equal(PacketType.Hello, read.Type);
        Assert.Equal(7u, read.StreamId);
        Assert.Equal(9u, read.Sequence);
        Assert.True(payload.IsEmpty);
    }

    [Fact]
    public void RejectsMalformedDatagrams()
    {
        var good = AudioDatagram(out _);

        Assert.False(PacketHeader.TryParse(good.AsSpan(0, 20), out _, out _));      // short
        Assert.False(PacketHeader.TryParse(good.AsSpan(0, good.Length - 1), out _, out _)); // truncated payload

        var magic = (byte[])good.Clone();
        magic[0] ^= 1;
        Assert.False(PacketHeader.TryParse(magic, out _, out _));

        var version = (byte[])good.Clone();
        version[4] = 99;
        Assert.False(PacketHeader.TryParse(version, out _, out _));

        var channels = (byte[])good.Clone();
        channels[28] = 0;
        Assert.False(PacketHeader.TryParse(channels, out _, out _));

        var type = (byte[])good.Clone();
        type[5] = 77;
        Assert.False(PacketHeader.TryParse(type, out _, out _));
    }

    [Fact]
    public void ALargerHeaderFromALaterVersionStillFindsThePayload()
    {
        var header = new PacketHeader(PacketType.Audio, 1, 2, 3, 48000, 1, SampleFormat.F32, 1);
        var bytes = new byte[PacketHeader.Size + 8 + 4];
        header.Write(bytes);
        bytes[6] = PacketHeader.Size + 8; // header size field
        BitConverter.GetBytes(0.25f).CopyTo(bytes, PacketHeader.Size + 8);
        Assert.True(PacketHeader.TryParse(bytes, out _, out var payload));
        Assert.Equal(0.25f, BitConverter.ToSingle(payload));
    }
}
