namespace DbvoStudio.Core.Audio;

/// <summary>Result of the start-of-take check (all levels in dBFS, times in ms).</summary>
public sealed record OnsetResult(bool Flagged, double BlipMs, double GapMs, double BlipPeakDb, double SpeechPeakDb);

/// <summary>
/// Finds the stray burst some ElevenLabs v4 takes open with: a short sound right at the start of the file (about 70 to
/// 80 ms in the samples studied, louder than the speech and pitched differently), then near silence, then the line.
/// It only reports; the audio is never changed. Sensitivity: "strict", "normal" or "relaxed".
/// </summary>
public static class OnsetCheck
{
    // MaxBlipMs and MinSemitones keep a short first word ("What… brings you here") from counting: in the takes studied
    // the real burst lasted 60 to 95 ms and sat 8+ semitones away from the voice, while first words followed by a pause
    // (common when tags are sent as context, which slows the delivery) ran 100 to 200 ms at the voice's own pitch.
    private sealed record Rule(double MinGapMs, double GapDb, double PeakMarginDb, double MaxBlipMs, double MinSemitones);

    private static Rule RuleFor(string sensitivity) => sensitivity switch
    {
        "strict" => new Rule(50, -45, -8, 130, 4),   // shorter gaps and quieter blips count
        "relaxed" => new Rule(120, -55, 0, 100, 8),  // only an obvious blip, at least as loud as the speech
        _ => new Rule(80, -50, -4, 110, 6),
    };

    /// <summary>Median pitch (Hz) of 40 ms frames from..to (sample indexes), or 0 when none of them is voiced.</summary>
    private static double Pitch(Func<int, float> s, int sr, int from, int to)
    {
        int win = sr / 25, step = sr / 100, lo = sr / 400, hi = sr / 70;
        var found = new List<double>();
        var x = new double[win];
        for (int start = from; start + win <= to; start += step)
        {
            double mean = 0, e = 0;
            for (int i = 0; i < win; i++) { x[i] = s(start + i); mean += x[i]; }
            mean /= win;
            for (int i = 0; i < win; i++) { x[i] -= mean; e += x[i] * x[i]; }
            if (e / win < 1e-6) continue;
            double best = 0; int lag = 0;
            for (int k = lo; k <= hi && k < win; k++)
            {
                double c = 0;
                for (int i = 0; i + k < win; i++) c += x[i] * x[i + k];
                if (c > best) { best = c; lag = k; }
            }
            if (lag > 0 && best / e > 0.45) found.Add((double)sr / lag);
        }
        if (found.Count == 0) return 0;
        found.Sort();
        return found[found.Count / 2];
    }

    public static OnsetResult Analyze(WavFile w, string sensitivity = "normal")
    {
        var none = new OnsetResult(false, 0, 0, double.NegativeInfinity, double.NegativeInfinity);
        int ch = Math.Max(1, w.Channels), sr = w.SampleRate;
        if (sr <= 0 || w.Samples.Length < ch * sr / 5) return none;
        int n = w.Samples.Length / ch;
        float S(int i) { int s = 0; for (int c = 0; c < ch; c++) s += w.Samples[i * ch + c]; return s / (32768f * ch); }

        // 5 ms frames: RMS and peak in dBFS.
        int win = Math.Max(1, sr / 200);
        int frames = n / win;
        var rms = new double[frames];
        var peak = new double[frames];
        for (int f = 0; f < frames; f++)
        {
            double sum = 0, pk = 0;
            for (int i = f * win; i < (f + 1) * win; i++) { double v = S(i); sum += v * v; pk = Math.Max(pk, Math.Abs(v)); }
            rms[f] = 10 * Math.Log10(sum / win + 1e-12);
            peak[f] = 20 * Math.Log10(pk + 1e-9);
        }
        const double Ms = 5;
        var rule = RuleFor(sensitivity);
        const double SoundDb = -40;

        // The blip: sound that starts within the first 60 ms.
        int a = 0;
        while (a < frames && rms[a] < SoundDb) a++;
        if (a >= frames || a * Ms > 60) return none;
        int b = a;
        while (b < frames && rms[b] >= SoundDb) b++;
        double blipMs = (b - a) * Ms;
        if (blipMs < 15 || blipMs > rule.MaxBlipMs) return none;

        // Then near silence of at least MinGapMs (the blip's tail decays into it).
        int g = b;
        while (g < frames && rms[g] >= rule.GapDb && (g - b) * Ms < 60) g++; // let the tail fall
        int gs = g;
        while (g < frames && rms[g] < rule.GapDb) g++;
        double gapMs = (g - gs) * Ms;
        if (gapMs < rule.MinGapMs || g >= frames) return none;

        // Then the line itself: at least 200 ms of sound after the gap.
        int sEnd = g, loud = 0;
        double speechPeak = double.NegativeInfinity;
        for (int f = g; f < frames; f++) { if (rms[f] >= SoundDb) { loud++; speechPeak = Math.Max(speechPeak, peak[f]); } sEnd = f; }
        if (loud * Ms < 200) return none;
        double blipPeak = double.NegativeInfinity;
        for (int f = a; f < b; f++) blipPeak = Math.Max(blipPeak, peak[f]);
        bool flagged = blipPeak >= speechPeak + rule.PeakMarginDb;
        if (flagged)
        {
            // A blip at the voice's own pitch is the first word, not a burst (an unpitched one still counts).
            double pb = Pitch(S, sr, a * win, b * win);
            double ps = Pitch(S, sr, g * win, Math.Min(n, g * win + sr * 6 / 10));
            if (pb > 0 && ps > 0 && Math.Abs(12 * Math.Log2(pb / ps)) < rule.MinSemitones) flagged = false;
        }
        return new OnsetResult(flagged, blipMs, gapMs, Math.Round(blipPeak, 1), Math.Round(speechPeak, 1));
    }
}
