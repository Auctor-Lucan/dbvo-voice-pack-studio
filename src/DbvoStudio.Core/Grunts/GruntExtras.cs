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

    /// <summary>
    /// Puts reverb on the line's WAV (the file itself changes, so it sounds that way wherever it is opened) or takes it off by
    /// putting the original back. Every line of the project that uses the same file follows. Returns the new length.
    /// </summary>
    public static async Task<double> SetReverbAsync(GruntProject p, GruntLine line, string projectFolder, bool on, ExternalTools? tools, bool force = false, CancellationToken ct = default)
    {
        var src = SourcePath(line, projectFolder);
        if (!File.Exists(src)) throw new FileNotFoundException($"The audio file is missing: {src}");
        var same = p.Events.Values.SelectMany(s => s.Lines).Where(l => string.Equals(SourcePath(l, projectFolder), src, StringComparison.OrdinalIgnoreCase)).ToList();
        var original = ReverbOriginal(projectFolder, src);
        double seconds;
        if (on)
        {
            if (line.Reverb && File.Exists(original)) return line.DurationSeconds;
            if (!src.EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Reverb works on WAV files; this line is {Path.GetExtension(src).TrimStart('.').ToUpperInvariant()}. Replace it with a WAV (⋯ → Replace the file) first.");
            WavFile w;
            try { w = WavFile.Read(src); }
            catch (InvalidDataException)
            {
                // 24-bit, 32-bit float or another WAV flavour: read it through ffmpeg as 16-bit at its own rate.
                if (tools == null) throw new InvalidOperationException("This WAV isn't 16-bit; ffmpeg is needed to read it (Global Settings → Tools).");
                var info = await ProbeAsync(tools, src, ct);
                var tmp = Path.Combine(Path.GetTempPath(), "dbvo-rv-" + Guid.NewGuid().ToString("N")[..10] + ".wav");
                try { await tools.ToWavAsync(src, tmp, info?.SampleRate is > 0 ? info.SampleRate : 44100, false, 0, ct); w = WavFile.Read(tmp); }
                finally { try { File.Delete(tmp); } catch { } }
            }
            Directory.CreateDirectory(Path.GetDirectoryName(original)!);
            File.Copy(src, original, true);
            var outW = new WavFile { SampleRate = w.SampleRate, Channels = w.Channels, Samples = Reverb.Apply(w.Samples, w.SampleRate, w.Channels, p.Reverb) };
            var tmpOut = src + ".dbvo-tmp";
            outW.Write(tmpOut);
            File.Move(tmpOut, src, true);
            seconds = outW.DurationSeconds;
            var stamp = Stamp(src);
            foreach (var l in same) { l.Reverb = true; l.ReverbStamp = stamp; l.DurationSeconds = seconds; l.Lufs = null; l.PeakDb = null; }
        }
        else
        {
            if (!File.Exists(original))
            {
                foreach (var l in same) { l.Reverb = false; l.ReverbStamp = ""; }
                throw new InvalidOperationException("The original (without reverb) isn't in the project folder any more, so the reverb can't be taken off. Replace the file with your original instead.");
            }
            if (!force && line.ReverbStamp.Length > 0 && Stamp(src) != line.ReverbStamp)
                throw new FileChangedException($"{Path.GetFileName(src)} was changed after the reverb was added. Taking the reverb off puts back the original from before, which replaces those changes.");
            File.Copy(original, src, true);
            File.Delete(original);
            try { seconds = WavFile.Read(src).DurationSeconds; } catch { seconds = 0; }
            foreach (var l in same) { l.Reverb = false; l.ReverbStamp = ""; if (seconds > 0) l.DurationSeconds = seconds; l.Lufs = null; l.PeakDb = null; }
        }
        return seconds;
    }

    /// <summary>
    /// Before a line leaves the project (removed, replaced): a reverbed file gets its original back, unless another line still
    /// uses it or it was changed since. Never throws.
    /// </summary>
    public static void ReleaseReverb(GruntProject p, GruntLine line, string projectFolder)
    {
        if (!line.Reverb) return;
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
        line.Reverb = false; line.ReverbStamp = "";
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
