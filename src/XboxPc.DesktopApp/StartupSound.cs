using System.IO;
using System.Media;

namespace XboxPc.DesktopApp;

/// <summary>
/// The XBOX PC start-up sound: an original, procedurally synthesised chime (no samples, nothing derived from any
/// console's audio). A low sine swell opens it; at <see cref="ChimeAt"/>, when the logo's light sweep passes, two
/// bell tones (D5, A5) and a short F#6 shimmer ring out over a few delay taps for space. Rendered once to an
/// in-memory 16-bit stereo WAV scaled by the user's volume, then played asynchronously by <see cref="SoundPlayer"/>.
/// </summary>
public sealed class StartupSound : IDisposable
{
    public const int SampleRate = 44_100;
    public const double Length = 2.6;
    public static readonly TimeSpan ChimeAt = TimeSpan.FromMilliseconds(620);

    private readonly MemoryStream _wav;
    private readonly SoundPlayer _player;

    public StartupSound(double volume)
    {
        _wav = new MemoryStream(Render(Math.Clamp(volume, 0, 1)));
        _player = new SoundPlayer(_wav);
        _player.Load();
    }

    public void Play() => _player.Play();

    public void Stop() => _player.Stop();

    public void Dispose()
    {
        _player.Stop();
        _player.Dispose();
        _wav.Dispose();
    }

    /// <summary>Synthesises the sound as a complete WAV file.</summary>
    public static byte[] Render(double volume)
    {
        var n = (int)(SampleRate * Length);
        var dry = new double[n];
        var chime = ChimeAt.TotalSeconds;
        for (var i = 0; i < n; i++)
        {
            var t = (double)i / SampleRate;
            double s = 0;

            // Low swell: 73.4 Hz (D2) and its octave, slow attack, long release.
            var swell = Smooth(t / 0.55) * Math.Exp(-Math.Max(0, t - 0.55) * 1.6);
            s += 0.22 * swell * (Math.Sin(Tau * 73.42 * t) + (0.45 * Math.Sin(Tau * 146.83 * t)));

            // Bell tones: inharmonic partials with exponential decay, soft 6 ms attack.
            s += Bell(t - chime, 587.33, 0.30);
            s += Bell(t - chime - 0.075, 880.00, 0.24);
            s += Bell(t - chime - 0.16, 1479.98, 0.10, decay: 3.2);
            dry[i] = s;
        }

        // Space: two short delay taps per channel, slightly different so the image is wide but calm.
        var left = new double[n];
        var right = new double[n];
        int D(double ms) => (int)(SampleRate * ms / 1000);
        int l1 = D(97), l2 = D(211), r1 = D(113), r2 = D(239);
        for (var i = 0; i < n; i++)
        {
            left[i] = dry[i] + (0.28 * At(dry, i - l1)) + (0.14 * At(left, i - l2));
            right[i] = dry[i] + (0.28 * At(dry, i - r1)) + (0.14 * At(right, i - r2));
        }

        // Fade the tail to silence and normalise.
        var peak = 1e-9;
        for (var i = 0; i < n; i++)
        {
            var tail = Math.Clamp((n - i) / (SampleRate * 0.35), 0, 1);
            left[i] *= tail;
            right[i] *= tail;
            peak = Math.Max(peak, Math.Max(Math.Abs(left[i]), Math.Abs(right[i])));
        }
        var gain = 0.82 * volume / peak;

        using var ms = new MemoryStream(44 + (n * 4));
        using var w = new BinaryWriter(ms);
        w.Write("RIFF"u8);
        w.Write(36 + (n * 4));
        w.Write("WAVEfmt "u8);
        w.Write(16);
        w.Write((short)1);          // PCM
        w.Write((short)2);          // stereo
        w.Write(SampleRate);
        w.Write(SampleRate * 4);    // bytes per second
        w.Write((short)4);          // block align
        w.Write((short)16);         // bits per sample
        w.Write("data"u8);
        w.Write(n * 4);
        for (var i = 0; i < n; i++)
        {
            w.Write((short)Math.Clamp(left[i] * gain * short.MaxValue, short.MinValue, short.MaxValue));
            w.Write((short)Math.Clamp(right[i] * gain * short.MaxValue, short.MinValue, short.MaxValue));
        }
        w.Flush();
        return ms.ToArray();
    }

    private const double Tau = Math.PI * 2;

    private static double At(double[] a, int i) => i >= 0 ? a[i] : 0;

    private static double Smooth(double x)
    {
        x = Math.Clamp(x, 0, 1);
        return x * x * (3 - (2 * x));
    }

    private static double Bell(double t, double f, double amp, double decay = 1.45)
    {
        if (t < 0) return 0;
        var attack = Math.Min(1, t / 0.006);
        var e = Math.Exp(-t * decay);
        return amp * attack * e * (Math.Sin(Tau * f * t)
            + (0.32 * Math.Exp(-t * 2.5) * Math.Sin(Tau * f * 2.004 * t))
            + (0.12 * Math.Exp(-t * 4.0) * Math.Sin(Tau * f * 3.011 * t)));
    }
}
