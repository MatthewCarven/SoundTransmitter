namespace SoundTransmitter.Core;

/// Holds the receiver's buffer at its target by nudging the playback rate.
///
/// The sender's clock and the receiver's sound card never agree exactly; a
/// difference of d (say 50 ppm) fills or drains the buffer at d seconds per
/// second. With e the buffer's excess over target in seconds and c the rate
/// correction, e' = d - c. A PI controller c = Kp e + Ki integral(e) makes that
/// a damped second-order loop with natural frequency sqrt(Ki). In steady state
/// the integral term carries the drift (c = d) and e returns to zero.
///
/// The level is smoothed first (1 s time constant): it is a sawtooth at packet
/// rate plus network jitter, and the loop is far slower than either, at about
/// 0.01 Hz, so the correction never wobbles audibly. The correction is clamped
/// to +-0.1% (under 2 cents of pitch).
public sealed class DriftController
{
    const double NaturalHz = 0.01;
    const double Damping = 1.0;
    const double SmoothingSeconds = 1.0;
    public const double MaxCorrection = 1e-3;

    static readonly double Ki = Math.Pow(2 * Math.PI * NaturalHz, 2);
    static readonly double Kp = 2 * Damping * 2 * Math.PI * NaturalHz;

    double smoothed = double.NaN;
    double integral;

    public double TargetSeconds { get; set; }
    /// Smoothed buffer level, seconds.
    public double Level => double.IsNaN(smoothed) ? 0 : smoothed;
    /// The current rate correction: positive means "consume input faster".
    public double Correction { get; private set; }

    public DriftController(double targetSeconds) => TargetSeconds = targetSeconds;

    /// Restarts smoothing from the next reading but keeps the learned drift.
    public void Resync() => smoothed = double.NaN;

    public void Reset()
    {
        smoothed = double.NaN;
        integral = 0;
        Correction = 0;
    }

    /// Feed one reading of the buffer level, taken every <paramref name="dt"/>
    /// seconds of output. Returns the correction to apply until the next one.
    public double Update(double levelSeconds, double dt)
    {
        if (double.IsNaN(smoothed)) smoothed = levelSeconds;
        else smoothed += (levelSeconds - smoothed) * Math.Min(1.0, dt / SmoothingSeconds);

        double error = smoothed - TargetSeconds;
        double c = Kp * error + Ki * (integral + error * dt);
        bool saturated = Math.Abs(c) > MaxCorrection;
        // Anti-windup: stop integrating while clamped and still pushing outward.
        if (!saturated || Math.Sign(error) != Math.Sign(c)) integral += error * dt;
        Correction = Math.Clamp(Kp * error + Ki * integral, -MaxCorrection, MaxCorrection);
        return Correction;
    }
}
