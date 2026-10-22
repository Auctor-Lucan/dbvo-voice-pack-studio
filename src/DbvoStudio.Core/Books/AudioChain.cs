namespace DbvoStudio.Core.Books;

/// <summary>Settings of the book audio chain. The defaults are the IVBOS release settings (the "LOUD" Audacity macro).</summary>
public sealed class BookAudioSettings
{
    /// <summary>Add reverb to every part (keeping its tail, which overlaps the next part at the join).</summary>
    public bool Reverb { get; set; } = true;
    public double ReverbPreDelayMs { get; set; } = 99;
    public double ReverbRoomSize { get; set; } = 81;
    public double ReverbReverberance { get; set; } = 23;
    public double ReverbHfDamping { get; set; } = 62;
    public double ReverbToneLow { get; set; } = 16;
    public double ReverbToneHigh { get; set; } = 19;
    public double ReverbWetGainDb { get; set; } = -4;
    public double ReverbDryGainDb { get; set; } = 0;
    /// <summary>Pause added at a paragraph boundary between two parts.</summary>
    public int ParagraphPauseMs { get; set; } = 300;
    /// <summary>Pause added where two parts meet inside a paragraph (after a sentence).</summary>
    public int SentencePauseMs { get; set; } = 150;
    /// <summary>Silence added at the end of the book (the Audacity macros add 1 second).</summary>
    public int EndSilenceMs { get; set; } = 1000;
    /// <summary>Loudness of the whole book (integrated, ITU BS.1770). 0 = leave as it is.</summary>
    public double LoudnessLufs { get; set; } = -12;
    public bool Limiter { get; set; } = true;
    public double LimiterThresholdDb { get; set; } = -3;
    /// <summary>Peak level after the limiter ("make-up target" in Audacity).</summary>
    public double LimiterOutputDb { get; set; } = -1;
    public double LimiterLookaheadMs { get; set; } = 5;
    public double LimiterReleaseMs { get; set; } = 50;
    /// <summary>Extra gain after everything else (Normal female preset: +1.8 dB).</summary>
    public double ExtraGainDb { get; set; } = 0;
    /// <summary>xWMA bit rate in bits per second.</summary>
    public int XwmBitrate { get; set; } = 48000;

    public BookAudioSettings Clone() => (BookAudioSettings)MemberwiseClone();

    /// <summary>A preset: "loud" (IVBOS release, the default) or "normal" (no loudness push, limiter only), male or female.</summary>
    public static BookAudioSettings Preset(string level, bool female)
    {
        var s = new BookAudioSettings();
        if (string.Equals(level, "normal", StringComparison.OrdinalIgnoreCase))
        {
            s.LoudnessLufs = 0;
            s.LimiterThresholdDb = -2;
            s.LimiterOutputDb = -2;
            if (female) s.ExtraGainDb = 1.8;
        }
        return s;
    }

    /// <summary>Text that changes whenever a setting that affects the sound changes (joins are rebuilt when it does).</summary>
    public string Fingerprint() => string.Join("|", Reverb, ReverbPreDelayMs, ReverbRoomSize, ReverbReverberance, ReverbHfDamping, ReverbToneLow, ReverbToneHigh,
        ReverbWetGainDb, ReverbDryGainDb, ParagraphPauseMs, SentencePauseMs, EndSilenceMs, LoudnessLufs, Limiter, LimiterThresholdDb, LimiterOutputDb, LimiterLookaheadMs,
        LimiterReleaseMs, ExtraGainDb, XwmBitrate);
}

/// <summary>
/// Audacity's Reverb effect (the SoX reverb, freeverb style: 8 damped combs, 4 allpasses, pre-delay, wet tone filters), for one mono channel.
/// Ported so books get the same reverb as the IVBOS release without Audacity.
/// </summary>
public sealed class SoxReverb
{
    private static readonly int[] CombLengths = { 1116, 1188, 1277, 1356, 1422, 1491, 1557, 1617 };
    private static readonly int[] AllpassLengths = { 225, 341, 441, 556 };

    private sealed class Line
    {
        public readonly float[] Buf; public int Pos; public float Store;
        public Line(int n) { Buf = new float[Math.Max(1, n)]; }
    }

    private readonly Line[] _combs, _allpasses;
    private readonly float _feedback, _damping, _gain;
    private readonly int _delay;
    private readonly double _hpB0, _hpB1, _hpA1, _lpB0, _lpB1, _lpA1;

    public SoxReverb(int rate, BookAudioSettings s)
    {
        double r = rate / 44100.0;
        double scale = s.ReverbRoomSize / 100 * .9 + .1;
        _combs = CombLengths.Select(l => new Line((int)(scale * r * l + .5))).ToArray();
        _allpasses = AllpassLengths.Select(l => new Line((int)(r * l + .5))).ToArray();
        double a = -1 / Math.Log(1 - .3), b = 100 / (Math.Log(1 - .98) * a + 1);
        _feedback = (float)(1 - Math.Exp((s.ReverbReverberance - b) / (a * b)));
        _damping = (float)(s.ReverbHfDamping / 100 * .3 + .2);
        _gain = (float)(Math.Pow(10, s.ReverbWetGainDb / 20) * .015);
        _delay = (int)(s.ReverbPreDelayMs / 1000 * rate + .5);
        double fcHigh = MidiToFreq(72 - s.ReverbToneLow / 100 * 48);
        double fcLow = MidiToFreq(72 + s.ReverbToneHigh / 100 * 48);
        // One-pole high-pass (removes the low end of the wet signal) and low-pass (removes its high end).
        _hpA1 = -Math.Exp(-2 * Math.PI * fcHigh / rate); _hpB0 = (1 - _hpA1) / 2; _hpB1 = -_hpB0;
        _lpA1 = -Math.Exp(-2 * Math.PI * fcLow / rate); _lpB0 = 1 + _lpA1; _lpB1 = 0;
    }

    private static double MidiToFreq(double n) => 440 * Math.Pow(2, (n - 69) / 12);

    /// <summary>Length of the reverb tail worth keeping after the input ends (seconds).</summary>
    public static double TailSeconds(BookAudioSettings s) => s.ReverbPreDelayMs / 1000 + 1.5;

    /// <summary>Dry + wet for <paramref name="input"/> followed by <paramref name="tailSamples"/> of tail.</summary>
    public float[] Process(float[] input, int tailSamples, double dryGainDb)
    {
        float dry = (float)Math.Pow(10, dryGainDb / 20);
        int n = input.Length + tailSamples;
        var output = new float[n];
        var pre = new float[_delay + 1];
        int prePos = 0;
        double hpI = 0, hpO = 0, lpI = 0, lpO = 0;
        for (int t = 0; t < n; t++)
        {
            float x = t < input.Length ? input[t] : 0f;
            // pre-delay
            float inp;
            if (_delay > 0) { inp = pre[prePos]; pre[prePos] = x; prePos = (prePos + 1) % _delay; }
            else inp = x;
            float o = 0;
            for (int i = _combs.Length - 1; i >= 0; i--)
            {
                var c = _combs[i];
                float y = c.Buf[c.Pos];
                c.Store = y + (c.Store - y) * _damping;
                c.Buf[c.Pos] = inp + c.Store * _feedback;
                if (++c.Pos >= c.Buf.Length) c.Pos = 0;
                o += y;
            }
            for (int i = _allpasses.Length - 1; i >= 0; i--)
            {
                var p = _allpasses[i];
                float y = p.Buf[p.Pos];
                p.Buf[p.Pos] = o + y * .5f;
                if (++p.Pos >= p.Buf.Length) p.Pos = 0;
                o = y - o;
            }
            double h = o * _hpB0 + hpI * _hpB1 - hpO * _hpA1; hpI = o; hpO = h;
            double l = h * _lpB0 + lpI * _lpB1 - lpO * _lpA1; lpI = h; lpO = l;
            output[t] = (float)(l * _gain) + (t < input.Length ? x * dry : 0f);
        }
        return output;
    }
}

/// <summary>Integrated loudness (ITU-R BS.1770-4, K-weighted, gated), for a mono signal.</summary>
public static class Loudness
{
    public static double IntegratedLufs(float[] x, int rate)
    {
        if (x.Length < rate / 10) return double.NegativeInfinity;
        var y = KWeight(x, rate);
        int block = (int)(0.4 * rate), step = (int)(0.1 * rate);
        var powers = new List<double>();
        for (int s = 0; s + block <= y.Length; s += step)
        {
            double sum = 0;
            for (int i = s; i < s + block; i++) sum += y[i] * y[i];
            powers.Add(sum / block);
        }
        if (powers.Count == 0) return double.NegativeInfinity;
        static double L(double p) => -0.691 + 10 * Math.Log10(Math.Max(p, 1e-20));
        var abs = powers.Where(p => L(p) > -70).ToList();
        if (abs.Count == 0) return double.NegativeInfinity;
        double rel = L(abs.Average()) - 10;
        var gated = abs.Where(p => L(p) > rel).ToList();
        return gated.Count == 0 ? double.NegativeInfinity : L(gated.Average());
    }

    private static double[] KWeight(float[] x, int rate)
    {
        // Stage 1: high shelf (+4 dB above ~1.5 kHz); stage 2: RLB high-pass. Coefficients by bilinear transform for any rate.
        double f0 = 1681.974450955533, g = 3.999843853973347, q = 0.7071752369554196;
        double k = Math.Tan(Math.PI * f0 / rate), vh = Math.Pow(10, g / 20), vb = Math.Pow(vh, 0.4996667741545416);
        double a0 = 1 + k / q + k * k;
        double b0 = (vh + vb * k / q + k * k) / a0, b1 = 2 * (k * k - vh) / a0, b2 = (vh - vb * k / q + k * k) / a0;
        double a1 = 2 * (k * k - 1) / a0, a2 = (1 - k / q + k * k) / a0;
        var s1 = Biquad(x.Select(v => (double)v).ToArray(), b0, b1, b2, a1, a2);
        f0 = 38.13547087602444; q = 0.5003270373238773;
        k = Math.Tan(Math.PI * f0 / rate);
        a0 = 1 + k / q + k * k;
        return Biquad(s1, 1, -2, 1, 2 * (k * k - 1) / a0, (1 - k / q + k * k) / a0);
    }

    private static double[] Biquad(double[] x, double b0, double b1, double b2, double a1, double a2)
    {
        var y = new double[x.Length];
        double x1 = 0, x2 = 0, y1 = 0, y2 = 0;
        for (int i = 0; i < x.Length; i++)
        {
            double v = b0 * x[i] + b1 * x1 + b2 * x2 - a1 * y1 - a2 * y2;
            x2 = x1; x1 = x[i]; y2 = y1; y1 = v; y[i] = v;
        }
        return y;
    }
}

/// <summary>Look-ahead peak limiter like Audacity's Limiter (hard knee): nothing passes the threshold, then make-up gain lifts the result to the output level.</summary>
public static class PeakLimiter
{
    public static void Apply(float[] x, int rate, double thresholdDb, double outputDb, double lookaheadMs, double releaseMs)
    {
        double thr = Math.Pow(10, thresholdDb / 20), makeup = Math.Pow(10, (outputDb - thresholdDb) / 20);
        int la = Math.Max(1, (int)(lookaheadMs / 1000 * rate));
        double rel = Math.Exp(-1.0 / Math.Max(1, releaseMs / 1000 * rate));
        int n = x.Length;
        // Gain each sample needs so that it stays under the threshold, then the minimum over the look-ahead window.
        var need = new double[n];
        for (int i = 0; i < n; i++) { double a = Math.Abs(x[i]); need[i] = a > thr ? thr / a : 1; }
        var win = new double[n];
        var dq = new LinkedList<int>();
        for (int i = n - 1; i >= 0; i--)
        {
            while (dq.Count > 0 && need[dq.Last!.Value] >= need[i]) dq.RemoveLast();
            dq.AddLast(i);
            while (dq.First!.Value > i + la) dq.RemoveFirst();
            win[i] = need[dq.First.Value];
        }
        // Attack instantly to the look-ahead minimum (already early by the look-ahead), release smoothly.
        double gcur = 1;
        for (int i = 0; i < n; i++)
        {
            double target = win[i];
            gcur = target < gcur ? target : target + (gcur - target) * rel;
            if (gcur > need[i]) gcur = need[i];
            x[i] = (float)(x[i] * gcur * makeup);
        }
    }
}
