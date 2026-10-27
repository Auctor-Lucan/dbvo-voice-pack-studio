using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using DbvoStudio.Core.Audio;
using DbvoStudio.Core.Tools;

namespace DbvoStudio.Core.Grunts;

/// <summary>Reverb on a line's file, the project subtitles .toml, and quick WAV probing.</summary>
public static partial class GruntService
{
    // ------------------------------------------------------------------ reverb

    /// <summary>Thrown when taking the reverb off would replace a file that was changed after the reverb was added.</summary>
    public sealed class FileChangedException(string message) : InvalidOperationException(message);

    public static string Stamp(string path)
    {
        var fi = new FileInfo(path);
        return fi.Exists ? $"{fi.Length}|{fi.LastWriteTimeUtc.Ticks}" : "";
    }

    /// <summary>Where the original of a reverbed file is kept (one per file, shared by lines that use the same file).</summary>
    public static string ReverbOriginal(string projectFolder, string sourcePath) =>
        Path.Combine(projectFolder, "reverb-originals", Hash(Path.GetFullPath(sourcePath).ToLowerInvariant()) + ".wav");

    /// <summary>The project's effects: its own list (without the retired ones, with the three custom slots), or the built-in ones.</summary>
    public static List<EffectPreset> EffectsOf(GruntProject p)
    {
        if (p.Effects.Count == 0) return Effects.BuiltIn();
        var list = p.Effects.Where(x => !Effects.Retired.Contains(x.Id)).ToList();
        foreach (var slot in Effects.BuiltIn().Where(x => x.Id.StartsWith("custom")))
            if (!list.Any(x => x.Id == slot.Id))
            {
                // after the last filter, so the slots sit with them
                int at = list.FindLastIndex(x => x.Group == "Filter");
                list.Insert(at < 0 ? list.Count : at + 1, slot);
            }
        if (Effects.Retired.Contains(p.DefaultEffect)) p.DefaultEffect = "inner";
        return list;
    }

    /// <summary>
    /// Puts an effect (reverb, filter, voice) on the line's WAV (the file itself changes, so it sounds that way wherever it is
    /// opened), or takes it off by putting the original back (effectId null). Changing to another effect starts again from the
    /// original. Every line of the project that uses the same file follows. Returns the new length.
    /// </summary>
    public static async Task<double> SetEffectAsync(GruntProject p, GruntLine line, string projectFolder, string? effectId, ExternalTools? tools, bool force = false, CancellationToken ct = default, bool? soften = null)
    {
        bool newSoften = soften ?? line.Soften;
        var src = SourcePath(line, projectFolder);
        if (!File.Exists(src)) throw new FileNotFoundException($"The audio file is missing: {src}");
        var same = p.Events.Values.SelectMany(s => s.Lines).Where(l => string.Equals(SourcePath(l, projectFolder), src, StringComparison.OrdinalIgnoreCase)).ToList();
        var original = ReverbOriginal(projectFolder, src);
        var current = line.Reverb ? (line.EffectId.Length > 0 ? line.EffectId : "inner") : null;
        bool processed = current != null || line.Soften;
        if (effectId == current && newSoften == line.Soften && (!processed || File.Exists(original))) return line.DurationSeconds;
        double seconds = 0;
        if (processed)
        {
            // Back to the original first (also when switching to another effect).
            if (!File.Exists(original))
            {
                foreach (var l in same) { l.Reverb = false; l.ReverbStamp = ""; l.EffectId = ""; l.Soften = false; }
                throw new InvalidOperationException("The original (without the effect) isn't in the project folder any more, so the effect can't be taken off. Replace the file with your original instead.");
            }
            if (!force && line.ReverbStamp.Length > 0 && Stamp(src) != line.ReverbStamp)
                throw new FileChangedException($"{Path.GetFileName(src)} was changed after the effect was added. Taking it off puts back the original from before, which replaces those changes.");
            File.Copy(original, src, true);
            File.Delete(original);
            try { seconds = WavFile.Read(src).DurationSeconds; } catch { seconds = ProbeWavHeader(src)?.Seconds ?? 0; }
            foreach (var l in same) { l.Reverb = false; l.ReverbStamp = ""; l.EffectId = ""; l.Soften = false; if (seconds > 0) l.DurationSeconds = seconds; l.Lufs = null; l.PeakDb = null; }
        }
        if (effectId == null && !newSoften) return seconds;
        var fx = effectId == null ? null : Effects.Find(EffectsOf(p), effectId) ?? throw new InvalidOperationException("That effect preset doesn't exist any more.");
        if (!src.EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Effects and Soften peaks work on WAV files; this line is {Path.GetExtension(src).TrimStart('.').ToUpperInvariant()}. Replace it with a WAV (⋯ → Replace the file) first.");
        Directory.CreateDirectory(Path.GetDirectoryName(original)!);
        File.Copy(src, original, true);
        try
        {
            if (fx != null) await Effects.ApplyAsync(original, src, fx, tools, ct);
            if (newSoften) await PeakSoftener.ApplyAsync(fx != null ? src : original, src, PeakSoftener.Standard, tools, ct);
        }
        catch { File.Copy(original, src, true); File.Delete(original); throw; }
        seconds = ProbeWavHeader(src)?.Seconds ?? 0;
        var stamp = Stamp(src);
        foreach (var l in same) { l.Reverb = fx != null; l.EffectId = fx?.Id ?? ""; l.Soften = newSoften; l.ReverbStamp = stamp; l.DurationSeconds = seconds; l.Lufs = null; l.PeakDb = null; }
        return seconds;
    }

    /// <summary>8.2.0's reverb toggle: on = the project's default effect.</summary>
    public static Task<double> SetReverbAsync(GruntProject p, GruntLine line, string projectFolder, bool on, ExternalTools? tools, bool force = false, CancellationToken ct = default) =>
        SetEffectAsync(p, line, projectFolder, on ? (p.DefaultEffect.Length > 0 ? p.DefaultEffect : "inner") : null, tools, force, ct);

    /// <summary>
    /// Before a line leaves the project (removed, replaced): a reverbed file gets its original back, unless another line still
    /// uses it or it was changed since. Never throws.
    /// </summary>
    public static void ReleaseReverb(GruntProject p, GruntLine line, string projectFolder)
    {
        if (!line.Reverb && !line.Soften) return;
        try
        {
            var src = SourcePath(line, projectFolder);
            var original = ReverbOriginal(projectFolder, src);
            bool shared = p.Events.Values.SelectMany(s => s.Lines).Any(l => l != line && string.Equals(SourcePath(l, projectFolder), src, StringComparison.OrdinalIgnoreCase));
            if (!shared && File.Exists(original))
            {
                if (File.Exists(src) && Stamp(src) == line.ReverbStamp) File.Copy(original, src, true);
                File.Delete(original);
            }
        }
        catch { /* the file stays as it is */ }
        line.Reverb = false; line.ReverbStamp = ""; line.EffectId = ""; line.Soften = false;
    }

    // ------------------------------------------------------------------ trim and fades (not destructive)

    public static bool HasTrim(GruntLine l) => l.TrimStart > 0.0005 || l.TrimEnd > 0.0005 || l.FadeInMs > 0.5 || l.FadeOutMs > 0.5;
    public static string TrimKey(GruntLine l) => HasTrim(l) ? string.Format(CultureInfo.InvariantCulture, "{0:0.###}-{1:0.###}-{2:0}-{3:0}", l.TrimStart, l.TrimEnd, l.FadeInMs, l.FadeOutMs) : "";

    /// <summary>The line's audio cut to its Start–End with its fades, as a mono-or-stereo 16-bit WAV at the given rate.</summary>
    public static async Task TrimmedAsync(ExternalTools tools, string input, GruntLine l, string outWav, int rate, CancellationToken ct)
    {
        var args = new List<string> { "-y", "-hide_banner", "-loglevel", "error", "-i", input };
        string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
        if (l.TrimStart > 0.0005) { args.Add("-ss"); args.Add(F(l.TrimStart)); }
        if (l.TrimEnd > l.TrimStart + 0.02) { args.Add("-to"); args.Add(F(l.TrimEnd)); }
        var af = new List<string>();
        if (l.FadeInMs > 0.5) af.Add($"afade=t=in:d={F(l.FadeInMs / 1000)}");
        if (l.FadeOutMs > 0.5) af.Add($"areverse,afade=t=in:d={F(l.FadeOutMs / 1000)},areverse");
        if (af.Count > 0) { args.Add("-af"); args.Add(string.Join(",", af)); }
        args.AddRange(new[] { "-ar", rate.ToString(CultureInfo.InvariantCulture), "-acodec", "pcm_s16le", "-map_metadata", "-1", outWav });
        var r = await ExternalTools.RunAsync(tools.FfmpegPath, args, ct);
        if (r.ExitCode != 0 || !File.Exists(outWav)) throw new ToolException($"Couldn't trim {Path.GetFileName(input)}: {r.StdErr.Trim().Split('\n').LastOrDefault()}");
    }

    // ------------------------------------------------------------------ variants

    /// <summary>
    /// New lines made from one: each a little higher or lower and faster or slower (pitch and speed spread evenly from -range
    /// to +range, never the original itself), written to the project's audio folder. Returns the WAV paths (relative).
    /// </summary>
    public static async Task<List<(string Rel, double Pitch, double Speed)>> MakeVariantsAsync(GruntProject p, GruntEventDef ev, GruntLine line, string projectFolder,
        int count, double pitchPct, double speedPct, ExternalTools tools, CancellationToken ct = default)
    {
        count = Math.Clamp(count, 1, 12);
        var src = SourcePath(line, projectFolder);
        if (!File.Exists(src)) throw new FileNotFoundException($"The audio file is missing: {src}");
        var dir = Path.Combine("audio", Regex.Replace(ev.Id, @"[^A-Za-z0-9]+", "_").Trim('_'));
        Directory.CreateDirectory(Path.Combine(projectFolder, dir));
        string input = src; string? tmp = null;
        var made = new List<(string, double, double)>();
        try
        {
            if (src.EndsWith(".fuz", StringComparison.OrdinalIgnoreCase)) { tmp = UnpackFuz(src); input = tmp; }
            int rate = ProbeWavHeader(input)?.SampleRate ?? (await ProbeAsync(tools, input, ct))?.SampleRate ?? 44100;
            if (rate <= 0) rate = 44100;
            for (int i = 0; i < count; i++)
            {
                // Spread evenly from -1 to +1 (the middle one of an odd count gets +0.5, so none is the original); speed alternates direction.
                double t = count == 1 ? 1 : -1 + 2.0 * i / (count - 1);
                if (Math.Abs(t) < 1e-9) t = 0.5;
                double pitch = 1 + pitchPct / 100 * t, speed = 1 + speedPct / 100 * (i % 2 == 0 ? -t : t);
                string F(double v) => v.ToString("0.#####", CultureInfo.InvariantCulture);
                // pitch without changing length (asetrate + atempo back), then the speed change
                var af = $"asetrate={rate}*{F(pitch)},aresample={rate},atempo={F(speed / pitch)}";
                var rel = Path.Combine(dir, $"{Regex.Replace(Path.GetFileNameWithoutExtension(src), @"[^A-Za-z0-9_-]+", "_")}-v{i + 1}-{Projects.NewId.Short(3)}.wav");
                var outPath = Path.Combine(projectFolder, rel);
                var tl = new GruntLine { TrimStart = line.TrimStart, TrimEnd = line.TrimEnd, FadeInMs = line.FadeInMs, FadeOutMs = line.FadeOutMs };
                string inp = input;
                if (HasTrim(tl)) { inp = outPath + ".t.wav"; await TrimmedAsync(tools, input, tl, inp, rate, ct); }
                var r = await ExternalTools.RunAsync(tools.FfmpegPath, new[] { "-y", "-hide_banner", "-loglevel", "error", "-i", inp, "-af", af, "-ar", rate.ToString(CultureInfo.InvariantCulture), "-acodec", "pcm_s16le", "-map_metadata", "-1", outPath }, ct);
                if (inp != input) try { File.Delete(inp); } catch { }
                if (r.ExitCode != 0 || !File.Exists(outPath)) throw new ToolException("ffmpeg couldn't make the variant: " + r.StdErr.Trim().Split('\n').LastOrDefault());
                made.Add((rel, pitch, speed));
            }
        }
        finally { if (tmp != null) try { File.Delete(tmp); } catch { } }
        return made;
    }

    // ------------------------------------------------------------------ loudness

    /// <summary>Loudness (LUFS) more than this far from the pack's middle value is flagged before export.</summary>
    public const double LoudnessSpread = 4;

    /// <summary>Measures loudness and true peak of lines (all, or only those not measured yet), several at a time.</summary>
    public static async Task<int> MeasureAsync(GruntProject p, string projectFolder, ExternalTools tools, bool onlyMissing, IProgress<(int Done, int Total)>? progress, CancellationToken ct)
    {
        var todo = p.Events.Values.SelectMany(s => s.Lines).Where(l => !onlyMissing || l.Lufs == null).Where(l => File.Exists(SourcePath(l, projectFolder))).ToList();
        int done = 0;
        await Parallel.ForEachAsync(todo, new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount / 2, 1, 6), CancellationToken = ct }, async (l, token) =>
        {
            var src = SourcePath(l, projectFolder);
            string? tmp = null;
            try
            {
                string input = src;
                if (src.EndsWith(".fuz", StringComparison.OrdinalIgnoreCase)) { tmp = UnpackFuz(src); input = tmp; }
                if (HasTrim(l)) { var t = Path.Combine(Path.GetTempPath(), "dbvo-m-" + Guid.NewGuid().ToString("N")[..8] + ".wav"); await TrimmedAsync(tools, input, l, t, 44100, token); if (tmp != null) File.Delete(tmp); tmp = t; input = t; }
                var (lufs, peak) = await tools.MeasureAsync(input, token);
                l.Lufs = lufs is { } x && !double.IsInfinity(x) ? Math.Round(x, 1) : -99;
                l.PeakDb = peak is { } y && !double.IsInfinity(y) ? Math.Round(y, 1) : null;
            }
            catch (OperationCanceledException) { throw; }
            catch { /* stays unmeasured */ }
            finally { if (tmp != null) try { File.Delete(tmp); } catch { } }
            progress?.Report((Interlocked.Increment(ref done), todo.Count));
        });
        return todo.Count;
    }

    /// <summary>The pack's middle loudness (median of the measured lines), or null.</summary>
    public static double? PackLoudness(GruntProject p)
    {
        var v = p.Events.Values.SelectMany(s => s.Lines).Select(l => l.Lufs).Where(x => x is > -70).Select(x => x!.Value).OrderBy(x => x).ToList();
        return v.Count < 3 ? null : v[v.Count / 2];
    }

    /// <summary>Lines much louder or quieter than the rest of the pack (and clipping ones), for Check.</summary>
    public static List<GruntIssue> LoudnessIssues(GruntProject p, GruntCatalog cat)
    {
        var list = new List<GruntIssue>();
        var mid = PackLoudness(p);
        foreach (var ev in cat.Events)
        {
            if (!p.Events.TryGetValue(ev.Id, out var st) || st.Skipped) continue;
            foreach (var l in st.Lines)
            {
                var name = Leaf(l.Source.Replace('/', '\\'));
                if (l.PeakDb is > -0.1) list.Add(new GruntIssue("warning", $"{ev.Label}: {name} clips (peak {l.PeakDb:0.0} dBTP).", ev.Id, l.Id));
                if (mid is not { } m || l.Lufs is not { } x || x <= -70) continue;
                var d = x - m;
                if (Math.Abs(d) > LoudnessSpread)
                    list.Add(new GruntIssue("warning", $"{ev.Label}: {name} is {(d > 0 ? "too loud" : "too quiet")} compared with the pack ({x:0.0} LUFS, the pack's middle is {m:0.0}; {(p.Audio.Normalize ? "loudness matching on export evens it out" : "loudness matching is off")}).", ev.Id, l.Id));
            }
        }
        return list;
    }

    // ------------------------------------------------------------------ quick probe

    /// <summary>Length, rate and channels from a WAV header (no ffmpeg); null when it isn't a readable WAV.</summary>
    public static AudioInfo? ProbeWavHeader(string path)
    {
        try
        {
            using var f = File.OpenRead(path);
            using var r = new BinaryReader(f);
            if (f.Length < 44 || new string(r.ReadChars(4)) != "RIFF") return null;
            r.ReadInt32();
            if (new string(r.ReadChars(4)) != "WAVE") return null;
            int rate = 0, ch = 0, blockAlign = 0; string codec = "";
            while (f.Position + 8 <= f.Length)
            {
                var id = new string(r.ReadChars(4));
                long size = r.ReadUInt32();
                long next = f.Position + size + (size & 1);
                if (id == "fmt ")
                {
                    int fmt = r.ReadUInt16(); ch = r.ReadUInt16(); rate = r.ReadInt32(); r.ReadInt32(); blockAlign = r.ReadUInt16(); int bits = r.ReadUInt16();
                    codec = fmt switch { 1 => $"pcm_s{bits}le", 3 => "pcm_f32le", _ => "wav" };
                }
                else if (id == "data")
                {
                    if (rate <= 0 || blockAlign <= 0) return null;
                    size = Math.Min(size, f.Length - f.Position);
                    return new AudioInfo(size / (double)blockAlign / rate, rate, ch, codec);
                }
                if (next > f.Length) break;
                f.Position = next;
            }
        }
        catch { }
        return null;
    }

    // ------------------------------------------------------------------ subtitles .toml

    public static string SubtitlesTomlPath(string projectFolder) => Path.Combine(projectFolder, "subtitles.toml");

    /// <summary>One file with every line's subtitle, to edit in any text editor; read back with <see cref="ReadSubtitlesToml"/>.</summary>
    public static string BuildSubtitlesToml(GruntProject p, GruntCatalog cat, string projectFolder)
    {
        var sb = new StringBuilder();
        sb.Append("# DBVO Studio · Player Grunts Tool · ").Append(p.Name).Append(" · subtitles\r\n");
        sb.Append("# Edit the text between the quotes, save, and go back to DBVO Studio: the changes are read in.\r\n");
        sb.Append(p.Target == GruntTargets.Spvs
            ? $"# The export writes each subtitle as a .toml next to its audio file (language: {p.Spvs.SubtitleLanguage}).\r\n"
            : "# PC Head Tracking shows no subtitles; the text is kept with the line (and gives .fuz files their lip sync).\r\n");
        sb.Append("# Keep the IDs on the left as they are. \\\" is a quote, \\n a new line.\r\n");
        foreach (var ev in cat.Events)
        {
            if (!p.Events.TryGetValue(ev.Id, out var st) || st.Lines.Count == 0) continue;
            sb.Append("\r\n[\"").Append(TomlEscape(ev.Id)).Append("\"] # ").Append(ev.Category).Append(" · ").Append(ev.Label).Append("\r\n");
            foreach (var l in st.Lines)
            {
                var slot = ev.Slots.FirstOrDefault(s => s.Id == l.Slot);
                sb.Append(l.Id).Append(" = \"").Append(TomlEscape(l.Text)).Append("\" # ")
                  .Append(slot != null ? slot.Label + " · " : "").Append(Leaf(l.Source.Replace('/', '\\'))).Append("\r\n");
            }
        }
        return sb.ToString();
    }

    [GeneratedRegex(@"^\s*""?([A-Za-z0-9_-]+)""?\s*=\s*""((?:[^""\\]|\\.)*)""")] private static partial Regex TomlLineRx();

    /// <summary>Applies an edited subtitles .toml: returns how many lines got a new text, and the IDs it didn't know.</summary>
    public static (int Changed, List<string> Unknown) ReadSubtitlesToml(GruntProject p, string text)
    {
        var lines = p.Events.Values.SelectMany(s => s.Lines).ToDictionary(l => l.Id);
        int changed = 0;
        var unknown = new List<string>();
        foreach (var raw in text.Split('\n'))
        {
            var t = raw.TrimStart('\uFEFF');
            if (t.TrimStart().StartsWith('#') || t.TrimStart().StartsWith('[')) continue;
            var m = TomlLineRx().Match(t);
            if (!m.Success) continue;
            if (!lines.TryGetValue(m.Groups[1].Value, out var l)) { unknown.Add(m.Groups[1].Value); continue; }
            string val;
            try { val = Regex.Unescape(m.Groups[2].Value); } catch { val = m.Groups[2].Value; }
            if (val != l.Text) { l.Text = val; changed++; }
        }
        return (changed, unknown);
    }

    /// <summary>Converts the lines an export needs, several at a time (ffmpeg runs one file per process).</summary>
    private static async Task<Dictionary<string, string>> ConvertAllAsync(GruntProject p, GruntExportPlan plan, string projectFolder, string cache, ExternalTools tools,
        IProgress<(int Done, int Total, string Text)>? progress, CancellationToken ct)
    {
        var jobs = plan.Files.Where(f => f.Bytes == null && f.Line != null).GroupBy(f => f.Line!.Id + "|" + f.Format).Select(g => g.First()).ToList();
        var made = new System.Collections.Concurrent.ConcurrentDictionary<string, string>();
        int done = 0;
        await Parallel.ForEachAsync(jobs, new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount / 2, 1, 6), CancellationToken = ct }, async (f, token) =>
        {
            made[f.Line!.Id + "|" + f.Format] = await ConvertAsync(p, f.Line!, f.Format, projectFolder, cache, tools, token);
            progress?.Report((Interlocked.Increment(ref done), jobs.Count, "Converting " + Leaf(f.Rel)));
        });
        return new Dictionary<string, string>(made);
    }
}
