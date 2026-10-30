using NAudio.Wave;

namespace SoundTransmitter.Core;

public static class AudioFormats
{
    static readonly Guid IeeeFloatSubtype = new("00000003-0000-0010-8000-00aa00389b71");

    /// Shared-mode mix formats are 32-bit float in practice; everything here
    /// assumes it and says so plainly if a device ever differs.
    public static bool IsFloat32(WaveFormat format) =>
        format.BitsPerSample == 32 &&
        (format.Encoding == WaveFormatEncoding.IeeeFloat ||
         format is WaveFormatExtensible ext && ext.SubFormat == IeeeFloatSubtype);

    public static void RequireFloat32(WaveFormat format, string what)
    {
        if (!IsFloat32(format))
            throw new InvalidOperationException(
                $"{what} uses {format.BitsPerSample}-bit {format.Encoding}; only 32-bit float mix formats are supported");
    }
}
