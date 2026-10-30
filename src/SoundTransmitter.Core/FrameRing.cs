namespace SoundTransmitter.Core;

/// Stereo frames stored by their absolute index in the stream. Packets can land
/// in any order; a frame that never arrived (or was overwritten by one a ring's
/// length later) reads as silence.
public sealed class FrameRing
{
    readonly float[] samples;
    readonly long[] stamps;
    readonly int mask;

    public int Capacity { get; }

    public FrameRing(int capacityPowerOfTwo)
    {
        if (capacityPowerOfTwo < 2 || (capacityPowerOfTwo & (capacityPowerOfTwo - 1)) != 0)
            throw new ArgumentException("capacity must be a power of two", nameof(capacityPowerOfTwo));
        Capacity = capacityPowerOfTwo;
        mask = capacityPowerOfTwo - 1;
        samples = new float[capacityPowerOfTwo * 2];
        stamps = new long[capacityPowerOfTwo];
        Clear();
    }

    public void Clear() => Array.Fill(stamps, -1L);

    public void Write(long firstFrame, ReadOnlySpan<float> stereo)
    {
        int frames = stereo.Length / 2;
        for (int i = 0; i < frames; i++)
        {
            long index = firstFrame + i;
            int slot = (int)(index & mask);
            samples[2 * slot] = stereo[2 * i];
            samples[2 * slot + 1] = stereo[2 * i + 1];
            stamps[slot] = index;
        }
    }

    public bool Has(long index) => index >= 0 && stamps[(int)(index & mask)] == index;

    public void Read(long index, out float left, out float right)
    {
        int slot = (int)(index & mask);
        if (index >= 0 && stamps[slot] == index)
        {
            left = samples[2 * slot];
            right = samples[2 * slot + 1];
        }
        else
        {
            left = right = 0f;
        }
    }
}
