namespace DbvoStudio.Core.Audio;

/// <summary>
/// Noise-reduced pauses: removes rumble below 80 Hz (a 4th-order high-pass, where no voice has anything) and turns quiet
/// stretches down: anything at least 30 dB below the line's speech level is lowered by up to 15 dB (never muted), with a
/// 6 dB soft knee. It opens 10 ms before the voice comes back and holds 120 ms after it stops (3 ms attack, 200 ms
/// release), so words, breaths next to words and fading endings are left alone. Length and timing don't change.
/// </summary>
public static class PauseNoiseReduction
{
    public static short[] Process(short[] s, int rate, int channels = 1)
    {
        channels = Math.Max(1, channels);
        int n = s.Length / channels;
        if (n < rate / 20) return (short[])s.Clone();
        // mono analysis signal, per channel filtering
        var ch = new double[channels][];
        for (int c = 0; c < channels; c++)
        {
            var x = new double[n];
            for (int i = 0; i < n; i++) x[i] = s[i * channels + c] / 32768.0;
            HighPass(x, rate, 80); HighPass(x, rate, 80);
            ch[c] = x;
        }
        var m = new double[n];
        for (int i = 0; i < n; i++) { double v = 0; for (int c = 0; c < channels; c++) v += ch[c][i] * ch[c][i]; m[i] = v / channels; }
        // envelope: RMS over 5 ms (centred), and the speech level: 90th percentile of 5 ms blocks
        int win = Math.Max(1, rate / 200);
        var pre = new double[n + 1];
        for (int i = 0; i < n; i++) pre[i + 1] = pre[i] + m[i];
        var db = new double[n];
        for (int i = 0; i < n; i++)
        {
            int a = Math.Max(0, i - win / 2), b = Math.Min(n, a + win);
            db[i] = 10 * Math.Log10(Math.Max((pre[b] - pre[a]) / Math.Max(1, b - a), 1e-18));
        }
        var blocks = new List<double>();
        for (int i = 0; i + win <= n; i += win) blocks.Add(10 * Math.Log10(Math.Max((pre[i + win] - pre[i]) / win, 1e-18)));
        if (blocks.Count == 0) return Join(ch, n, channels);
        blocks.Sort();
        double speech = blocks[(int)Math.Min(blocks.Count - 1, Math.Floor(blocks.Count * 0.9))];
        double thr = speech - 30, range = 15;
        var target = new double[n];
        for (int i = 0; i < n; i++) target[i] = -range * Math.Clamp((thr - db[i]) / 6, 0, 1);
        // least attenuation over [i - hold, i + look]: open before the voice, hold after it (monotone deque)
        int look = rate * 10 / 1000, hold = rate * 120 / 1000;
        var open = new double[n];
        var q = new LinkedList<int>();
        for (int i = 0, j = 0; i < n; i++)
        {
            for (; j < n && j <= i + look; j++)
            {
                while (q.Count > 0 && target[q.Last!.Value] <= target[j]) q.RemoveLast();
                q.AddLast(j);
            }
            while (q.First!.Value < i - hold) q.RemoveFirst();
            open[i] = target[q.First.Value];
        }
        double ga = Math.Exp(-1.0 / (rate * 0.003)), gr = Math.Exp(-1.0 / (rate * 0.200)), cur = 0;
        for (int i = 0; i < n; i++)
        {
            double t = open[i], k = t > cur ? ga : gr;
            cur = k * cur + (1 - k) * t;
            double g = Math.Pow(10, cur / 20);
            for (int c = 0; c < channels; c++) ch[c][i] *= g;
        }
        return Join(ch, n, channels, s);
    }

    private static short[] Join(double[][] ch, int n, int channels, short[]? orig = null)
    {
        var o = new short[orig?.Length ?? n * channels];
        for (int i = 0; i < n; i++)
            for (int c = 0; c < channels; c++)
                o[i * channels + c] = (short)Math.Clamp(Math.Round(ch[c][i] * 32767), short.MinValue, short.MaxValue);
        if (orig != null) for (int k = n * channels; k < orig.Length; k++) o[k] = orig[k];
        return o;
    }

    /// <summary>2nd-order Butterworth high-pass (RBJ biquad), in place.</summary>
    private static void HighPass(double[] x, int rate, double fc)
    {
        double w0 = 2 * Math.PI * fc / rate, al = Math.Sin(w0) / (2 * Math.Sqrt(0.5)), c = Math.Cos(w0);
        double a0 = 1 + al, b0 = (1 + c) / 2 / a0, b1 = -(1 + c) / a0, b2 = b0, a1 = -2 * c / a0, a2 = (1 - al) / a0;
        double x1 = 0, x2 = 0, y1 = 0, y2 = 0;
        for (int i = 0; i < x.Length; i++)
        {
            double v = x[i], o = b0 * v + b1 * x1 + b2 * x2 - a1 * y1 - a2 * y2;
            x2 = x1; x1 = v; y2 = y1; y1 = o; x[i] = o;
        }
    }
}
