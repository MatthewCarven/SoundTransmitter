namespace SoundTransmitter.Core;

/// Band-limited interpolation at any fractional position: a Kaiser-windowed
/// sinc of 64 taps, tabulated at 256 phases with linear interpolation between
/// neighbouring phases. One instance covers both the fixed rate change (say
/// 44.1 kHz in, 48 kHz out) and the tiny, varying correction for clock drift,
/// since the caller just steps through the input by whatever ratio it likes.
public sealed class Resampler
{
    public const int HalfTaps = 32;
    const int Taps = 2 * HalfTaps;
    const int Phases = 256;
    const double KaiserBeta = 8.0;

    readonly float[] table; // (Phases + 1) rows of Taps coefficients

    /// Low-pass corner as a fraction of the input's Nyquist frequency.
    public double Cutoff { get; }

    public Resampler(double cutoff)
    {
        if (cutoff is <= 0 or > 1) throw new ArgumentOutOfRangeException(nameof(cutoff));
        Cutoff = cutoff;
        table = new float[(Phases + 1) * Taps];
        double i0Beta = BesselI0(KaiserBeta);
        var row = new double[Taps];
        for (int p = 0; p <= Phases; p++)
        {
            double frac = (double)p / Phases;
            double sum = 0;
            for (int j = 0; j < Taps; j++)
            {
                // Distance from tap j to the output point, in input frames.
                double x = frac + HalfTaps - 1 - j;
                row[j] = Kernel(x, cutoff, i0Beta);
                sum += row[j];
            }
            // Unity gain at DC for every phase, so a steady level doesn't ripple
            // as the phase moves.
            for (int j = 0; j < Taps; j++) table[p * Taps + j] = (float)(row[j] / sum);
        }
    }

    /// The corner for a given nominal step (input frames per output frame):
    /// a little under the lower of the two Nyquist frequencies.
    public static double CutoffFor(double step) => 0.95 * Math.Min(1.0, 1.0 / step);

    /// The stereo frame at input position <paramref name="index"/> + <paramref name="frac"/>
    /// (0 &lt;= frac &lt; 1). Reads frames index-31 .. index+32.
    public void Sample(FrameRing ring, long index, double frac, out float left, out float right)
    {
        double pf = frac * Phases;
        int p = Math.Min((int)pf, Phases - 1);
        float a = (float)(pf - p);
        int row0 = p * Taps;
        int row1 = row0 + Taps;
        long first = index - HalfTaps + 1;
        float l = 0f, r = 0f;
        for (int j = 0; j < Taps; j++)
        {
            float w = table[row0 + j] + a * (table[row1 + j] - table[row0 + j]);
            ring.Read(first + j, out float sl, out float sr);
            l += w * sl;
            r += w * sr;
        }
        left = l;
        right = r;
    }

    static double Kernel(double x, double cutoff, double i0Beta)
    {
        double u = x / HalfTaps;
        if (Math.Abs(u) >= 1) return 0;
        double t = Math.PI * cutoff * x;
        double sinc = Math.Abs(t) < 1e-12 ? 1 : Math.Sin(t) / t;
        return cutoff * sinc * BesselI0(KaiserBeta * Math.Sqrt(1 - u * u)) / i0Beta;
    }

    static double BesselI0(double x)
    {
        double sum = 1, term = 1, half = x / 2;
        for (int k = 1; k < 200; k++)
        {
            term *= half / k;
            double t2 = term * term;
            sum += t2;
            if (t2 < sum * 1e-16) break;
        }
        return sum;
    }
}
