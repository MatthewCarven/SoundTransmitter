using System.Buffers.Binary;

namespace SoundTransmitter.Core;

public enum PacketType : byte
{
    /// Sender to receiver: a run of PCM frames.
    Audio = 1,
    /// Receiver to sender, once a second: "I'm listening, keep streaming".
    Hello = 2,
    /// Sender to receiver while nobody is listening. Keeps the sender's
    /// firewall state open so a receiver's first Hello gets through.
    Beacon = 3,
}

public enum SampleFormat : byte
{
    S16 = 1,
    F32 = 2,
}

/// The fixed header that starts every datagram. Little-endian throughout.
///
///  0  u32  magic "STX1"
///  4  u8   version
///  5  u8   packet type
///  6  u16  header size (payload starts here; lets later versions grow it)
///  8  u32  stream id (random per sender run; a change means "start over")
/// 12  u32  sequence number
/// 16  i64  index of the first frame in this packet, counted from stream start
/// 24  u32  sample rate
/// 28  u8   channels
/// 29  u8   sample format
/// 30  u16  frames in this packet
public readonly record struct PacketHeader(
    PacketType Type,
    uint StreamId,
    uint Sequence,
    long FirstFrame,
    int SampleRate,
    byte Channels,
    SampleFormat Format,
    ushort FrameCount)
{
    public const uint Magic = 0x31585453; // "STX1" as little-endian bytes
    public const byte CurrentVersion = 1;
    public const int Size = 32;

    public int PayloadBytes => FrameCount * Channels * BytesPerSample(Format);

    public static int BytesPerSample(SampleFormat format) => format switch
    {
        SampleFormat.S16 => 2,
        SampleFormat.F32 => 4,
        _ => 0,
    };

    public static PacketHeader Control(PacketType type, uint streamId, uint sequence) =>
        new(type, streamId, sequence, 0, 0, 0, 0, 0);

    public void Write(Span<byte> dst)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(dst, Magic);
        dst[4] = CurrentVersion;
        dst[5] = (byte)Type;
        BinaryPrimitives.WriteUInt16LittleEndian(dst[6..], Size);
        BinaryPrimitives.WriteUInt32LittleEndian(dst[8..], StreamId);
        BinaryPrimitives.WriteUInt32LittleEndian(dst[12..], Sequence);
        BinaryPrimitives.WriteInt64LittleEndian(dst[16..], FirstFrame);
        BinaryPrimitives.WriteInt32LittleEndian(dst[24..], SampleRate);
        dst[28] = Channels;
        dst[29] = (byte)Format;
        BinaryPrimitives.WriteUInt16LittleEndian(dst[30..], FrameCount);
    }

    /// Parses a datagram. False for anything that isn't a well-formed packet
    /// of ours: wrong magic or version, a short read, or audio whose payload
    /// doesn't match its header.
    public static bool TryParse(ReadOnlySpan<byte> datagram, out PacketHeader header, out ReadOnlySpan<byte> payload)
    {
        header = default;
        payload = default;
        if (datagram.Length < Size) return false;
        if (BinaryPrimitives.ReadUInt32LittleEndian(datagram) != Magic) return false;
        if (datagram[4] != CurrentVersion) return false;
        int headerSize = BinaryPrimitives.ReadUInt16LittleEndian(datagram[6..]);
        if (headerSize < Size || headerSize > datagram.Length) return false;

        var type = (PacketType)datagram[5];
        if (type is not (PacketType.Audio or PacketType.Hello or PacketType.Beacon)) return false;

        header = new PacketHeader(
            type,
            BinaryPrimitives.ReadUInt32LittleEndian(datagram[8..]),
            BinaryPrimitives.ReadUInt32LittleEndian(datagram[12..]),
            BinaryPrimitives.ReadInt64LittleEndian(datagram[16..]),
            BinaryPrimitives.ReadInt32LittleEndian(datagram[24..]),
            datagram[28],
            (SampleFormat)datagram[29],
            BinaryPrimitives.ReadUInt16LittleEndian(datagram[30..]));

        if (type != PacketType.Audio) return true;

        if (header.Channels is < 1 or > 8) return false;
        if (header.SampleRate is < 8000 or > 384000) return false;
        if (BytesPerSample(header.Format) == 0) return false;
        if (header.FrameCount == 0 || header.FirstFrame < 0) return false;
        if (datagram.Length - headerSize < header.PayloadBytes) return false;

        payload = datagram.Slice(headerSize, header.PayloadBytes);
        return true;
    }
}
