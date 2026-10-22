using System.Security.Cryptography;
using System.Text;
using DbvoStudio.Core.Audio;
using DbvoStudio.Core.Tools;

namespace DbvoStudio.Core.Books;

/// <summary>
/// Joins a book's part takes into one file and finishes it the IVBOS way: reverb on every part (its tail rings on into
/// the next part), a pause at paragraph joins, one second of silence at the end, one loudness pass over the whole book,
/// the limiter, then xWMA. A join is rebuilt only when a take, a part's text or an audio setting changed.
/// </summary>
public static class BookJoiner
{
    public const int Rate = 44100;

    /// <summary>Fingerprint of what a join is made of: take ids and files, part texts, audio settings.</summary>
    public static string Fingerprint(string projectFolder, IReadOnlyList<BookLine> parts, BookAudioSettings audio)
    {
        var sb = new StringBuilder(audio.Fingerprint());
        foreach (var l in parts)
        {
            var t = l.SelectedTake;
            sb.Append('|').Append(l.Part).Append(':').Append(t?.Id).Append(':').Append(l.ParagraphEnd ? 'p' : 's').Append(':').Append(l.Spoken.GetHashCode(StringComparison.Ordinal));
            if (t != null)
            {
                var f = Path.Combine(projectFolder, t.WavPath);
                if (File.Exists(f)) sb.Append(':').Append(new FileInfo(f).Length).Append(':').Append(File.GetLastWriteTimeUtc(f).Ticks);
            }
        }
        return Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(sb.ToString())))[..20];
    }

    public static float[] ReadMono(string path, out int rate)
    {
        var w = WavFile.Read(path);
        rate = w.SampleRate;
        int ch = Math.Max(1, w.Channels), n = w.Samples.Length / ch;
        var x = new float[n];
        for (int i = 0; i < n; i++)
        {
            float s = 0;
            for (int c = 0; c < ch; c++) s += w.Samples[i * ch + c];
            x[i] = s / ch / 32768f;
        }
        return x;
    }

    /// <summary>Process and join the parts' WAV files (one per part, in order). Returns mono float samples at 44.1 kHz.</summary>
    public static float[] Process(IReadOnlyList<(string Wav, bool ParagraphEnd)> parts, BookAudioSettings s, out double lufs, out double peakDb)
    {
        var clips = new List<float[]>();
        foreach (var (wav, _) in parts)
        {
            var x = ReadMono(wav, out int rate);
            if (rate != Rate) throw new InvalidDataException($"{Path.GetFileName(wav)} is {rate} Hz; takes are expected at 44.1 kHz.");
            clips.Add(x);
        }
        int pauseP = s.ParagraphPauseMs * Rate / 1000, pauseS = s.SentencePauseMs * Rate / 1000, end = s.EndSilenceMs * Rate / 1000;
        var starts = new int[clips.Count];
        int t = 0;
        for (int i = 0; i < clips.Count; i++)
        {
            starts[i] = t;
            t += clips[i].Length;
            if (i < clips.Count - 1) t += parts[i].ParagraphEnd ? pauseP : pauseS;
        }
        int total = t + end;
        var outp = new float[total];
        int tail = s.Reverb ? (int)(SoxReverb.TailSeconds(s) * Rate) : 0;
        for (int i = 0; i < clips.Count; i++)
        {
            // The reverb is linear: processing each part with its tail and adding them up equals processing the whole book.
            var y = s.Reverb ? new SoxReverb(Rate, s).Process(clips[i], tail, s.ReverbDryGainDb) : clips[i];
            int n = Math.Min(y.Length, total - starts[i]);
            for (int k = 0; k < n; k++) outp[starts[i] + k] += y[k];
        }
        if (s.LoudnessLufs < 0)
        {
            double l = Loudness.IntegratedLufs(outp, Rate);
            if (!double.IsNegativeInfinity(l))
            {
                float g = (float)Math.Pow(10, (s.LoudnessLufs - l) / 20);
                for (int i = 0; i < outp.Length; i++) outp[i] *= g;
            }
        }
        if (s.Limiter) PeakLimiter.Apply(outp, Rate, s.LimiterThresholdDb, s.LimiterOutputDb, s.LimiterLookaheadMs, s.LimiterReleaseMs);
        if (s.ExtraGainDb != 0)
        {
            float g = (float)Math.Pow(10, s.ExtraGainDb / 20);
            for (int i = 0; i < outp.Length; i++) outp[i] *= g;
        }
        float peak = 0;
        for (int i = 0; i < outp.Length; i++) { var a = Math.Abs(outp[i]); if (a > peak) peak = a; }
        // Never clip: a level above full scale (from extra gain) is brought back under it.
        if (peak > 0.999f) { float g = 0.999f / peak; for (int i = 0; i < outp.Length; i++) outp[i] *= g; peak = 0.999f; }
        lufs = Loudness.IntegratedLufs(outp, Rate);
        peakDb = peak > 0 ? 20 * Math.Log10(peak) : double.NegativeInfinity;
        return outp;
    }

    public static void WriteWav(string path, float[] x)
    {
        var w = new WavFile { SampleRate = Rate, Channels = 1, Samples = new short[x.Length] };
        for (int i = 0; i < x.Length; i++) w.Samples[i] = (short)Math.Clamp(Math.Round(x[i] * 32767), -32768, 32767);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        w.Write(tmp);
        File.Move(tmp, path, true);
    }

    /// <summary>Build (or reuse) the joined WAV and XWM of one book for one voice. Returns null when a part has no take yet.</summary>
    public static async Task<BookJoin?> JoinAsync(BooksProject p, string projectFolder, string voice, Book b, string variant, ExternalTools tools, bool encode, CancellationToken ct)
    {
        var parts = BooksService.LinesOf(p, voice, b.Stem, variant);
        if (parts.Count == 0 || parts.Any(l => l.SelectedTake == null)) return null;
        var audio = BooksService.AudioFor(p, voice);
        var key = JoinKey(voice, b.Stem, variant);
        var fp = Fingerprint(projectFolder, parts, audio);
        var folder = Path.Combine("joined", Safe(voice) + (variant.Length > 0 ? "@" + variant : ""));
        var wavRel = Path.Combine(folder, b.Stem + ".wav");
        var xwmRel = Path.Combine(folder, b.Stem + ".xwm");
        if (p.Joins.TryGetValue(key, out var j) && j.Fingerprint == fp && File.Exists(Path.Combine(projectFolder, j.Wav))
            && (!encode || File.Exists(Path.Combine(projectFolder, j.Xwm))))
            return j;
        var files = parts.Select(l => (Path.Combine(projectFolder, l.SelectedTake!.WavPath), l.ParagraphEnd)).ToList();
        foreach (var (f, _) in files) if (!File.Exists(f)) throw new FileNotFoundException("A take's WAV file is missing.", f);
        float[] samples = Array.Empty<float>();
        double lu = 0, pk = 0;
        await Task.Run(() => { samples = Process(files, audio, out lu, out pk); }, ct);
        var wavAbs = Path.Combine(projectFolder, wavRel);
        WriteWav(wavAbs, samples);
        var join = new BookJoin
        {
            Voice = voice, Stem = b.Stem, Variant = variant, Takes = parts.Select(l => l.SelectedTake!.Id).ToList(), Fingerprint = fp,
            Wav = wavRel, Xwm = "", Seconds = samples.Length / (double)Rate, Lufs = Math.Round(lu, 2), PeakDb = Math.Round(pk, 2),
        };
        if (encode)
        {
            var xwmAbs = Path.Combine(projectFolder, xwmRel);
            // xWMAEncode fails on long paths: encode in a short temp folder, then move.
            var work = Path.Combine(Path.GetTempPath(), "dbvo", "vb-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(work);
            try
            {
                var tw = Path.Combine(work, "a.wav"); var tx = Path.Combine(work, "a.xwm");
                File.Copy(wavAbs, tw, true);
                await tools.ToXwmaAsync(tw, tx, audio.XwmBitrate, ct);
                File.Copy(tx, xwmAbs, true);
            }
            finally { try { Directory.Delete(work, true); } catch { } }
            join.Xwm = xwmRel;
        }
        p.Joins[key] = join;
        return join;
    }

    public static string JoinKey(string voice, string stem, string variant) => $"{voice}|{stem}|{variant}";

    public static string Safe(string voice) => string.Concat(voice.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_'));
}
