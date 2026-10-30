using SoundTransmitter.Core;

namespace SoundTransmitter.Tests;

public class ResamplerTests
{
    /// Resamples a sine at <paramref name="hz"/> from inRate to outRate and
    /// returns (output amplitude relative to input, worst error against the
    /// ideal sine at the output times, dB re full tone).
    static (double gainDb, double errorDb) Run(double hz, int inRate, int outRate)
    {
        const int inFrames = 8192;
        var ring = new FrameRing(1 << 14);
        var src = new float[inFrames * 2];
        for (int i = 0; i < inFrames; i++)
            src[2 * i] = src[2 * i + 1] = (float)(0.5 * Math.Sin(2 * Math.PI * hz * i / inRate));
        ring.Write(0, src);

        double step = (double)inRate / outRate;
        var rs = new Resampler(Resampler.CutoffFor(step));
        double peak = 0, worst = 0;
        long index = 200;
        double frac = 0;
        while (index < inFrames - 200)
        {
            rs.Sample(ring, index, frac, out float l, out _);
            double ideal = 0.5 * Math.Sin(2 * Math.PI * hz * (index + frac) / inRate);
            peak = Math.Max(peak, Math.Abs(l));
            worst = Math.Max(worst, Math.Abs(l - ideal));
            frac += step;
            long whole = (long)frac;
            index += whole;
            frac -= whole;
        }
        return (20 * Math.Log10(peak / 0.5), 20 * Math.Log10(worst / 0.5));
    }

    [Theory]
    [InlineData(44100, 48000)]
    [InlineData(48000, 48000)]
    [InlineData(48000, 44100)]
    public void MidBandToneComesThroughClean(int inRate, int outRate)
    {
        var (gain, error) = Run(1000, inRate, outRate);
        Assert.InRange(gain, -0.05, 0.05);
        Assert.True(error < -70, $"error {error:F1} dB");
    }

    [Fact]
    public void PassbandIsFlatTo16kHz()
    {
        var (gain, _) = Run(16000, 44100, 48000);
        Assert.InRange(gain, -0.2, 0.2);
    }

    [Fact]
    public void ToneAboveOutputNyquistIsRemovedWhenDownsampling()
    {
        // 23.5 kHz exists at 48 kHz but would alias at 44.1 kHz.
        var (gain, _) = Run(23500, 48000, 44100);
        Assert.True(gain < -50, $"leak {gain:F1} dB");
    }
}
