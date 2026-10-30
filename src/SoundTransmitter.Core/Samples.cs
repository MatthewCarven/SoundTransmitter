using System.Runtime.InteropServices;

namespace SoundTransmitter.Core;

/// Sample conversions between the capture format, the wire and the receiver.
/// Everything inside the program is interleaved stereo float.
public static class Samples
{
    const float Minus3dB = 0.70710678f;

    /// Folds interleaved audio of any channel count down to stereo. Layouts are
    /// the WAVEFORMATEXTENSIBLE defaults for each count (quad, 5.1, 7.1); LFE is
    /// dropped. Odd counts outside those just keep the first two channels.
    public static void ToStereo(ReadOnlySpan<float> src, int channels, Span<float> stereo)
    {
        int frames = src.Length / channels;
        if (stereo.Length < frames * 2) throw new ArgumentException("stereo buffer too small");

        switch (channels)
        {
            case 1:
                for (int i = 0; i < frames; i++) stereo[2 * i] = stereo[2 * i + 1] = src[i];
                break;
            case 2:
                src[..(frames * 2)].CopyTo(stereo);
                break;
            case 4: // FL FR BL BR
                for (int i = 0, s = 0; i < frames; i++, s += 4)
                {
                    stereo[2 * i] = src[s] + Minus3dB * src[s + 2];
                    stereo[2 * i + 1] = src[s + 1] + Minus3dB * src[s + 3];
                }
                break;
            case 6: // FL FR FC LFE BL BR
            case 8: // FL FR FC LFE BL BR SL SR
                for (int i = 0, s = 0; i < frames; i++, s += channels)
                {
                    float c = Minus3dB * src[s + 2];
                    float l = src[s] + c + Minus3dB * src[s + 4];
                    float r = src[s + 1] + c + Minus3dB * src[s + 5];
                    if (channels == 8)
                    {
                        l += Minus3dB * src[s + 6];
                        r += Minus3dB * src[s + 7];
                    }
                    stereo[2 * i] = l;
                    stereo[2 * i + 1] = r;
                }
                break;
            default:
                for (int i = 0, s = 0; i < frames; i++, s += channels)
                {
                    stereo[2 * i] = src[s];
                    stereo[2 * i + 1] = src[s + 1];
                }
                break;
        }
    }

    /// Float to 16-bit with triangular dither of +-1 LSB, clamped.
    /// <paramref name="rng"/> is xorshift32 state; any non-zero seed.
    public static void EncodeS16(ReadOnlySpan<float> src, Span<byte> dst, ref uint rng)
    {
        var outp = MemoryMarshal.Cast<byte, short>(dst);
        for (int i = 0; i < src.Length; i++)
        {
            float dither = (NextUnit(ref rng) - NextUnit(ref rng));
            float v = MathF.Round(src[i] * 32768f + dither);
            outp[i] = (short)Math.Clamp(v, -32768f, 32767f);
        }
    }

    public static void DecodeS16(ReadOnlySpan<byte> src, Span<float> dst)
    {
        var inp = MemoryMarshal.Cast<byte, short>(src);
        for (int i = 0; i < inp.Length; i++) dst[i] = inp[i] * (1f / 32768f);
    }

    public static void EncodeF32(ReadOnlySpan<float> src, Span<byte> dst) =>
        MemoryMarshal.AsBytes(src).CopyTo(dst);

    public static void DecodeF32(ReadOnlySpan<byte> src, Span<float> dst) =>
        MemoryMarshal.Cast<byte, float>(src).CopyTo(dst);

    /// Uniform in [0, 1).
    static float NextUnit(ref uint state)
    {
        uint x = state;
        x ^= x << 13;
        x ^= x >> 17;
        x ^= x << 5;
        state = x;
        return (x >> 8) * (1f / (1 << 24));
    }
}
