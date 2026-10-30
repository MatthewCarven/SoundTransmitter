using SoundTransmitter.Core;

namespace SoundTransmitter.Tests;

public class SamplesTests
{
    [Fact]
    public void S16RoundTripsWithinDither()
    {
        var src = Enumerable.Range(0, 1000).Select(i => MathF.Sin(i * 0.01f) * 0.9f).ToArray();
        var bytes = new byte[src.Length * 2];
        uint rng = 12345;
        Samples.EncodeS16(src, bytes, ref rng);
        var back = new float[src.Length];
        Samples.DecodeS16(bytes, back);
        for (int i = 0; i < src.Length; i++) Assert.True(MathF.Abs(back[i] - src[i]) <= 1.6f / 32768f, $"sample {i}");
    }

    [Fact]
    public void S16ClampsOverRange()
    {
        var bytes = new byte[4];
        uint rng = 1;
        Samples.EncodeS16(new[] { 3f, -3f }, bytes, ref rng);
        var back = new float[2];
        Samples.DecodeS16(bytes, back);
        Assert.Equal(32767f / 32768f, back[0]);
        Assert.Equal(-1f, back[1]);
    }

    [Fact]
    public void F32IsExact()
    {
        var src = new[] { 0.1f, -0.7f, 1.5f };
        var bytes = new byte[12];
        Samples.EncodeF32(src, bytes);
        var back = new float[3];
        Samples.DecodeF32(bytes, back);
        Assert.Equal(src, back);
    }

    [Fact]
    public void MonoIsDuplicated()
    {
        var stereo = new float[4];
        Samples.ToStereo(new[] { 0.5f, -0.25f }, 1, stereo);
        Assert.Equal(new[] { 0.5f, 0.5f, -0.25f, -0.25f }, stereo);
    }

    [Fact]
    public void FivePointOneFoldsCentreAndSurroundsIn()
    {
        // FL FR FC LFE BL BR
        var src = new[] { 0.1f, 0.2f, 0.4f, 0.9f, 0.3f, 0.5f };
        var stereo = new float[2];
        Samples.ToStereo(src, 6, stereo);
        Assert.Equal(0.1f + 0.70710678f * (0.4f + 0.3f), stereo[0], 5);
        Assert.Equal(0.2f + 0.70710678f * (0.4f + 0.5f), stereo[1], 5);
    }
}
