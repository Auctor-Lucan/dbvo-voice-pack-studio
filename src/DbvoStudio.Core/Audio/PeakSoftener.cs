using DbvoStudio.Core.Tools;

namespace DbvoStudio.Core.Audio;

/// <summary>
/// Soften peaks: an offline look-ahead limiter for hard consonants (t, d, g, k bursts) that stand far above the speech
/// level and distort at a high in-game gain. Only the bursts are turned down, over a few milliseconds, then the line is
/// matched back to its own loudness. No delay: timing (and so the .lip) doesn't change.
/// Standard: limit at 8 dB above the speech level, 4 ms look-ahead, 60 ms release. Strong: 6 dB, 6 ms, 90 ms.
/// </summary>
public static class PeakSoftener
{
    public const string Standard = "standard";
    public const string Strong = "strong";

    public static bool IsMode(string? m) => m == Standard || m == Strong;
    public static string Name(string? m) => m == Strong ? "Soften peaks (strong)" : m == Standard ? "Soften peaks" : "";

    private static (double AboveDb, double LookMs, double ReleaseMs) Params(string mode) => mode == Strong ? (6, 6, 90) : (8, 4, 60);

    /// <summary>The speech level (linear, 0–1): the 90th percentile of 10 ms RMS blocks, ignoring silence.</summary>
    public static double SpeechLevel(short[] s, int rate, int channels)
    {
        channels = Math.Max(1, channels);
        int frames = s.Length / channels, block = Math.Max(1, rate / 100);
        var rms = new List<double>();
        for (int f = 0; f + block <= frames; f += block)
        {
            double sum = 0;
            for (int i = f * channels; i < (f + block) * channels; i++) { double x = s[i] / 32768.0; sum += x * x; }
            double r = Math.Sqrt(sum / (block * channels));
            if (r > 1e-3) rms.Add(r);
        }
        if (rms.Count == 0) return 0;
        rms.Sort();
        return rms[(int)Math.Min(rms.Count - 1, Math.Floor(rms.Count * 0.9))];
    }

    /// <summary>How far the loudest peak stands above the speech level (dB), or null for silence.</summary>
    public static double? PeakAboveSpeechDb(short[] s, int rate, int channels)
    {
        var sp = SpeechLevel(s, rate, channels);
        if (sp <= 0) return null;
        int peak = 0;
        foreach (var x in s) { int a = x == short.MinValue ? 32768 : Math.Abs(x); if (a > peak) peak = a; }
        return peak == 0 ? null : 20 * Math.Log10(peak / 32768.0 / sp);
    }

    /// <summary>Mono float samples (−1…1), softened and matched back to their speech power (for the book joiner).</summary>
    public static float[] Process(float[] x, int rate, string mode)
    {
        var s = new short[x.Length];
        for (int i = 0; i < x.Length; i++) s[i] = (short)Math.Clamp(Math.Round(x[i] * 32767), short.MinValue, short.MaxValue);
        var o = Process(s, rate, 1, mode);
        double a = Effects.ActivePower(s, rate), b = Effects.ActivePower(o, rate);
        double g = a > 1e-12 && b > 1e-12 ? Math.Clamp(Math.Sqrt(a / b), 1, 2) : 1;
        var y = new float[x.Length];
        for (int i = 0; i < y.Length; i++) y[i] = (float)(o[i] / 32767.0 * g);
        return y;
    }

    /// <summary>The limited samples (same length, rate and channels). Loudness isn't matched here.</summary>
    public static short[] Process(short[] s, int rate, int channels, string mode)
    {
        channels = Math.Max(1, channels);
        var (above, lookMs, relMs) = Params(mode);
        int n = s.Length / channels;
        if (n == 0) return (short[])s.Clone();
        double sp = SpeechLevel(s, rate, channels);
        if (sp <= 0) return (short[])s.Clone();
        double ceil = Math.Min(sp * Math.Pow(10, above / 20), Math.Pow(10, -1 / 20.0));
        // the gain each frame needs on its own
        var want = new double[n];
        for (int f = 0; f < n; f++)
        {
            int peak = 0;
            for (int c = 0; c < channels; c++) { int a = Math.Abs((int)s[f * channels + c]); if (a > peak) peak = a; }
            double x = peak / 32768.0;
            want[f] = x > ceil ? ceil / x : 1;
        }
        // start lowering lookahead frames before a peak: the minimum over a window of 2L+1 frames (a monotone queue)
        int L = Math.Max(1, (int)(rate * lookMs / 1000));
        var g = new double[n];
        var q = new LinkedList<int>();
        for (int i = 0, j = 0; i < n; i++)
        {
            for (; j < n && j <= i + L; j++)
            {
                while (q.Count > 0 && want[q.Last!.Value] >= want[j]) q.RemoveLast();
                q.AddLast(j);
            }
            while (q.First!.Value < i - L) q.RemoveFirst();
            g[i] = want[q.First.Value];
        }
        // a short fade into each dip (backwards, the attack) and a slower recovery after it (forwards, the release)
        double att = 1 - Math.Exp(-1.0 / L), rel = 1 - Math.Exp(-1.0 / Math.Max(1, rate * relMs / 1000));
        for (int i = n - 2; i >= 0; i--) g[i] = Math.Min(g[i], g[i + 1] + (1 - g[i + 1]) * att);
        for (int i = 1; i < n; i++) g[i] = Math.Min(g[i], g[i - 1] + (1 - g[i - 1]) * rel);
        var o = new short[s.Length];
        for (int f = 0; f < n; f++)
            for (int c = 0; c < channels; c++)
            {
                int k = f * channels + c;
                o[k] = (short)Math.Clamp(Math.Round(s[k] * g[f]), short.MinValue, short.MaxValue);
            }
        for (int k = n * channels; k < s.Length; k++) o[k] = s[k];
        return o;
    }

    /// <summary>
    /// Writes <paramref name="dest"/> as <paramref name="src"/> (a 16-bit WAV) with its peaks softened, at the same speech
    /// loudness. Pure C# (no ffmpeg), so thousands of takes take seconds, not minutes.
    /// </summary>
    public static Task ApplyAsync(string src, string dest, string mode, ExternalTools? tools = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var w = WavFile.Read(src);
        var o = Process(w.Samples, w.SampleRate, w.Channels, mode);
        Effects.Level(o, w.Samples, w.SampleRate * Math.Max(1, w.Channels), 0);
        var outTmp = dest + ".sp-" + Guid.NewGuid().ToString("N")[..6] + ".wav";
        try
        {
            new WavFile { SampleRate = w.SampleRate, Channels = w.Channels, Samples = o }.Write(outTmp);
            File.Move(outTmp, dest, true);
        }
        finally { try { if (File.Exists(outTmp)) File.Delete(outTmp); } catch { } }
        return Task.CompletedTask;
    }
}
