namespace DbvoStudio.Core.Audio;

/// <summary>
/// v4 Turbo de-grain: ElevenLabs' v4 Turbo voices carry a fine noise ("fizz") between the harmonics of the voice from
/// about 3.5 to 10 kHz, and sound a little duller than regular v4 above 3 kHz. This turns down only the weak bins
/// between harmonics in that band (to 40 %) and lifts 2.5–6 kHz by 1.2 dB (6–16 kHz stays level with v4), measured on matched Turbo / v4
/// takes of the same lines. Speech below 3.5 kHz and the timing (so the .lip) are untouched.
/// </summary>
public static class TurboDegrain
{
    /// <summary>Only ElevenLabs v4 Turbo: on other models it would change a voice that doesn't have the grain.</summary>
    public static bool Applies(string? modelId) =>
        modelId != null && modelId.StartsWith("eleven_v4", StringComparison.OrdinalIgnoreCase) && modelId.Contains("turbo", StringComparison.OrdinalIgnoreCase);

    private const int N = 2048, Hop = 512;

    /// <summary>Mono samples in, de-grained samples out (same length).</summary>
    public static short[] Process(short[] input, int rate)
    {
        int len = input.Length;
        if (len < N) return (short[])input.Clone();
        var win = new double[N];
        for (int i = 0; i < N; i++) win[i] = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / N);
        // per-bin gain: the shelf (+1.2 dB, easing in from 2.5 to 4.5 kHz and back a little above 11 kHz)
        int bins = N / 2 + 1;
        var shelf = new double[bins];
        for (int k = 0; k < bins; k++)
        {
            double f = (double)k * rate / N;
            // +1.2 dB from 2.5 kHz (in by 4.5 kHz), easing back to +0.2 dB between 6 and 8 kHz and to -0.6 dB at 16 kHz,
            // so 6-16 kHz sits level with regular v4 instead of brightening what grain is left
            double db = 1.2 * Math.Clamp((f - 2500) / 2000, 0, 1);
            if (f > 6000) db -= 1.0 * Math.Clamp((f - 6000) / 2000, 0, 1);
            if (f > 11000) db -= 0.8 * Math.Clamp((f - 11000) / 5000, 0, 1);
            double g = Math.Pow(10, db / 20);
            shelf[k] = g;
        }
        int lo = (int)Math.Ceiling(3500.0 * N / rate), hi = (int)Math.Floor(10000.0 * N / rate);
        // pad so every sample is covered by full overlap
        int pad = N;
        var x = new double[len + 2 * pad];
        for (int i = 0; i < len; i++) x[pad + i] = input[i] / 32768.0;
        var y = new double[x.Length];
        var norm = new double[x.Length];
        var re = new double[N]; var im = new double[N]; var mag = new double[bins];
        for (int start = 0; start + N <= x.Length; start += Hop)
        {
            for (int i = 0; i < N; i++) { re[i] = x[start + i] * win[i]; im[i] = 0; }
            Fft(re, im, false);
            for (int k = 0; k < bins; k++) mag[k] = Math.Sqrt(re[k] * re[k] + im[k] * im[k]);
            for (int k = 0; k < bins; k++)
            {
                double g = shelf[k];
                if (k >= lo && k <= hi)
                {
                    double m = 0;
                    for (int j = Math.Max(0, k - 3); j <= Math.Min(bins - 1, k + 3); j++) if (mag[j] > m) m = mag[j];
                    if (mag[k] < 0.5 * m) g *= 0.4;
                }
                re[k] *= g; im[k] *= g;
                if (k > 0 && k < N / 2) { re[N - k] = re[k]; im[N - k] = -im[k]; }
            }
            Fft(re, im, true);
            for (int i = 0; i < N; i++) { y[start + i] += re[i] * win[i]; norm[start + i] += win[i] * win[i]; }
        }
        var o = new short[len];
        for (int i = 0; i < len; i++)
        {
            double n = norm[pad + i];
            double v = n > 1e-9 ? y[pad + i] / n : x[pad + i];
            o[i] = (short)Math.Clamp(Math.Round(v * 32767), short.MinValue, short.MaxValue);
        }
        return o;
    }

    /// <summary>In-place radix-2 FFT; inverse divides by N.</summary>
    private static void Fft(double[] re, double[] im, bool inverse)
    {
        int n = re.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j) { (re[i], re[j]) = (re[j], re[i]); (im[i], im[j]) = (im[j], im[i]); }
        }
        for (int size = 2; size <= n; size <<= 1)
        {
            double ang = 2 * Math.PI / size * (inverse ? 1 : -1);
            double wr = Math.Cos(ang), wi = Math.Sin(ang);
            for (int s = 0; s < n; s += size)
            {
                double cr = 1, ci = 0;
                for (int k = 0; k < size / 2; k++)
                {
                    int a = s + k, b = a + size / 2;
                    double tr = re[b] * cr - im[b] * ci, ti = re[b] * ci + im[b] * cr;
                    re[b] = re[a] - tr; im[b] = im[a] - ti; re[a] += tr; im[a] += ti;
                    double ncr = cr * wr - ci * wi; ci = cr * wi + ci * wr; cr = ncr;
                }
            }
        }
        if (inverse) for (int i = 0; i < n; i++) { re[i] /= n; im[i] /= n; }
    }
}
