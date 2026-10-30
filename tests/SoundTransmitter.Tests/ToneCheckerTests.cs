using SoundTransmitter.Core;

namespace SoundTransmitter.Tests;

public class ToneCheckerTests
{
    const int Rate = 48000;

    static float[] Tone(int frames, double hz = 440, double amp = 0.3)
    {
        var s = new float[frames * 2];
        for (int i = 0; i < frames; i++) s[2 * i] = s[2 * i + 1] = (float)(amp * Math.Sin(2 * Math.PI * hz * i / Rate));
        return s;
    }

    [Fact]
    public void ACleanToneHasNoGlitches()
    {
        var c = new ToneChecker(440, Rate);
        c.Feed(Tone(Rate * 2), 1.0, Rate);
        Assert.Equal(0, c.Glitches);
        Assert.True(c.Blocks > 180);
        Assert.True(c.WorstCleanResidualDb < -80, $"{c.WorstCleanResidualDb:F1} dB");
    }

    [Fact]
    public void ADropoutIsAGlitch()
    {
        var s = Tone(Rate * 2);
        Array.Clear(s, 2 * Rate, 2 * 240); // 5 ms of silence a second in
        var c = new ToneChecker(440, Rate);
        c.Feed(s, 1.0, Rate);
        Assert.InRange(c.Glitches, 1, 2);
    }

    [Fact]
    public void AOneSampleClickIsAGlitch()
    {
        var s = Tone(Rate * 2);
        s[2 * Rate + 1000] += 0.05f;
        var c = new ToneChecker(440, Rate);
        c.Feed(s, 1.0, Rate);
        Assert.Equal(1, c.Glitches);
    }

    [Fact]
    public void ResampledToneIsFollowedThroughTheStep()
    {
        // A 44.1 kHz tone played at 48 kHz: each output frame steps 0.91875 input frames.
        double step = 44100.0 / 48000;
        var s = new float[Rate * 2];
        for (int i = 0; i < Rate; i++) s[2 * i] = (float)(0.3 * Math.Sin(2 * Math.PI * 440 * i * step / 44100));
        var c = new ToneChecker(440, Rate);
        c.Feed(s, step, 44100);
        Assert.Equal(0, c.Glitches);
    }
}
