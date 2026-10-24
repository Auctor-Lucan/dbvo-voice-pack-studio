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
    private sealed record Rule(double MinGapMs, double GapDb, double PeakMarginDb);

    private static Rule RuleFor(string sensitivity) => sensitivity switch
    {
        "strict" => new Rule(50, -45, -8),   // shorter gaps and quieter blips count
        "relaxed" => new Rule(120, -55, 0),  // only an obvious blip, at least as loud as the speech
        _ => new Rule(80, -50, -4),
    };

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
        if (blipMs < 15 || blipMs > 200) return none;

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
        return new OnsetResult(flagged, blipMs, gapMs, Math.Round(blipPeak, 1), Math.Round(speechPeak, 1));
    }
}
