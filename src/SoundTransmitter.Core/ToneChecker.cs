namespace SoundTransmitter.Core;

/// Listens to the receiver's output for a known test tone and counts glitches,
/// so a long run can be judged without anyone listening.
///
/// Every 10 ms block of the left channel gets a least-squares fit of
/// a sin(wt) + b cos(wt) + c at the tone's frequency as it should appear in the
/// output. A clean block leaves a residual far below the tone; a dropout, a
/// click or a repeated chunk leaves a big one. Checking starts once the tone has
/// been seen steadily for 100 ms, so the run-in doesn't count.
public sealed class ToneChecker
{
    const double GlitchResidualDb = -40;
    const double GlitchLevelDb = 1.0;
    const int ArmBlocks = 10;

    readonly double toneHz;
    readonly int blockFrames;
    readonly double[] block;
    int filled;
    double referenceAmp;
    int goodRun;
    bool armed;

    public long Blocks { get; private set; }
    public long Glitches { get; private set; }
    /// Worst residual among blocks that passed, dB below the tone.
    public double WorstCleanResidualDb { get; private set; } = double.NegativeInfinity;
    public double LastResidualDb { get; private set; }

    public ToneChecker(double toneHz, int outputRate)
    {
        this.toneHz = toneHz;
        blockFrames = outputRate / 100;
        block = new double[blockFrames];
    }

    /// Feed output frames. <paramref name="step"/> and <paramref name="inputRate"/>
    /// fix where the tone lands: the sender's tone advances 2 pi f / inputRate
    /// per input frame, and each output frame moves <paramref name="step"/> input frames.
    public void Feed(ReadOnlySpan<float> stereo, double step, int inputRate)
    {
        if (step <= 0 || inputRate <= 0) return;
        double omega = 2 * Math.PI * toneHz * step / inputRate;
        for (int i = 0; i + 1 < stereo.Length; i += 2)
        {
            block[filled++] = stereo[i];
            if (filled == blockFrames)
            {
                Check(omega);
                filled = 0;
            }
        }
    }

    void Check(double omega)
    {
        int n = blockFrames;
        double sss = 0, ssc = 0, scc = 0, ss = 0, sc = 0, xs = 0, xc = 0, x1 = 0, xx = 0;
        for (int i = 0; i < n; i++)
        {
            double s = Math.Sin(omega * i), c = Math.Cos(omega * i), x = block[i];
            sss += s * s; ssc += s * c; scc += c * c; ss += s; sc += c;
            xs += x * s; xc += x * c; x1 += x; xx += x * x;
        }
        // Normal equations for [a b d]: M v = y.
        double[,] m = { { sss, ssc, ss }, { ssc, scc, sc }, { ss, sc, n } };
        double[] y = { xs, xc, x1 };
        var v = Solve3(m, y);
        double a = v[0], b = v[1], d = v[2];
        double residual = Math.Max(0, xx - (a * xs + b * xc + d * x1));
        double amp = Math.Sqrt(a * a + b * b);
        double toneRms = amp / Math.Sqrt(2);
        double residualRms = Math.Sqrt(residual / n);
        double residualDb = toneRms > 0 ? 20 * Math.Log10(Math.Max(residualRms, 1e-12) / toneRms) : 0;
        LastResidualDb = residualDb;

        bool clean = amp > 1e-4 && residualDb < GlitchResidualDb &&
            (referenceAmp == 0 || Math.Abs(20 * Math.Log10(amp / referenceAmp)) < GlitchLevelDb);

        if (!armed)
        {
            if (clean)
            {
                referenceAmp = referenceAmp == 0 ? amp : referenceAmp * 0.9 + amp * 0.1;
                if (++goodRun >= ArmBlocks) armed = true;
            }
            else
            {
                goodRun = 0;
                referenceAmp = 0;
            }
            return;
        }

        Blocks++;
        if (clean)
        {
            referenceAmp = referenceAmp * 0.99 + amp * 0.01;
            WorstCleanResidualDb = Math.Max(WorstCleanResidualDb, residualDb);
        }
        else
        {
            Glitches++;
        }
    }

    static double[] Solve3(double[,] m, double[] y)
    {
        // Gaussian elimination with partial pivoting; 3x3 is all we ever need.
        var a = (double[,])m.Clone();
        var b = (double[])y.Clone();
        for (int col = 0; col < 3; col++)
        {
            int pivot = col;
            for (int r = col + 1; r < 3; r++) if (Math.Abs(a[r, col]) > Math.Abs(a[pivot, col])) pivot = r;
            if (pivot != col)
            {
                for (int k = 0; k < 3; k++) (a[col, k], a[pivot, k]) = (a[pivot, k], a[col, k]);
                (b[col], b[pivot]) = (b[pivot], b[col]);
            }
            if (Math.Abs(a[col, col]) < 1e-300) return new double[3];
            for (int r = col + 1; r < 3; r++)
            {
                double f = a[r, col] / a[col, col];
                for (int k = col; k < 3; k++) a[r, k] -= f * a[col, k];
                b[r] -= f * b[col];
            }
        }
        var x = new double[3];
        for (int r = 2; r >= 0; r--)
        {
            double s = b[r];
            for (int k = r + 1; k < 3; k++) s -= a[r, k] * x[k];
            x[r] = s / a[r, r];
        }
        return x;
    }
}
