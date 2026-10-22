using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using DbvoStudio.Core.Audio;
using DbvoStudio.Core.ElevenLabs;
using DbvoStudio.Core.Naming;
using DbvoStudio.Core.Tools;

namespace DbvoStudio.Core.Projects;

/// <summary>
/// Built .fuz files stored by content: the key covers the WAV, the LIP and the encoder (xWMAEncode file + bitrate), so a
/// rebuild reuses a .fuz only when all three are the same, and a new encoder rebuilds everything.
/// </summary>
public sealed class FuzCache
{
    private readonly string _root;
    public FuzCache(string root) => _root = root;

    public static string EncoderId(string? xwmaEncodePath, int bitrate)
    {
        var fi = !string.IsNullOrWhiteSpace(xwmaEncodePath) && File.Exists(xwmaEncodePath) ? new FileInfo(xwmaEncodePath) : null;
        return $"xwma|{bitrate}|{fi?.Length}|{fi?.LastWriteTimeUtc.Ticks}";
    }

    public static string Key(byte[] wav, byte[] lip, string encoderId)
    {
        using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        h.AppendData(SHA256.HashData(wav));
        h.AppendData(SHA256.HashData(lip));
        h.AppendData(Encoding.UTF8.GetBytes(encoderId));
        return Convert.ToHexString(h.GetHashAndReset());
    }

    private string PathOf(string key) => Path.Combine(_root, key[..2], key + ".fuz");

    public byte[]? TryGet(string key)
    {
        var p = PathOf(key);
        try { return File.Exists(p) ? File.ReadAllBytes(p) : null; } catch (IOException) { return null; }
    }

    public void Put(string key, byte[] fuz)
    {
        var p = PathOf(key);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            var tmp = p + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
            File.WriteAllBytes(tmp, fuz);
            File.Move(tmp, p, true);
        }
        catch (IOException) { /* the cache is only a speed-up */ }
    }

    /// <summary>Empty the cache. Returns (files, bytes) removed.</summary>
    public (int Files, long Bytes) Clear()
    {
        if (!Directory.Exists(_root)) return (0, 0);
        int n = 0; long b = 0;
        foreach (var f in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
        {
            try { b += new FileInfo(f).Length; File.Delete(f); n++; } catch (IOException) { }
        }
        try { Directory.Delete(_root, true); } catch (IOException) { }
        return (n, b);
    }

    public (int Files, long Bytes) Size()
    {
        if (!Directory.Exists(_root)) return (0, 0);
        var files = Directory.EnumerateFiles(_root, "*.fuz", SearchOption.AllDirectories).Select(f => new FileInfo(f)).ToList();
        return (files.Count, files.Sum(f => f.Length));
    }
}

/// <summary>What a legacy (or any earlier) voice pack folder holds for a patch.</summary>
public sealed class LegacyScan
{
    public string Folder { get; set; } = "";
    public int Files { get; set; }
    /// <summary>Lines without takes that the folder has audio for, by kind.</summary>
    public int FromFuz { get; set; }
    public int FromWavLip { get; set; }
    /// <summary>Lines that have takes already (left as they are).</summary>
    public int HaveTakes { get; set; }
    /// <summary>Lines the folder has nothing for: they still need generating.</summary>
    public int Missing { get; set; }
    /// <summary>Audio files in the folder that match no line of this patch (another mod's lines, or another version).</summary>
    public int Unmatched { get; set; }
    public int LegacyNamed { get; set; }
    public int HashNamed { get; set; }
    public List<string> UnmatchedExamples { get; set; } = new();
}

public sealed partial class Phase2Service
{
    private sealed record LegacyFile(string Path, bool IsFuz);

    /// <summary>Index a folder (all sub-folders) of .fuz and WAV + LIP files by file name without extension, any case.</summary>
    private static Dictionary<string, LegacyFile> IndexAudio(string folder)
    {
        var map = new Dictionary<string, LegacyFile>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in Directory.EnumerateFiles(folder, "*.*", SearchOption.AllDirectories))
        {
            var ext = Path.GetExtension(f).ToLowerInvariant();
            if (ext == ".fuz") map[Path.GetFileNameWithoutExtension(f)] = new LegacyFile(f, true); // a .fuz wins over a loose WAV
            else if (ext == ".wav" && File.Exists(Path.ChangeExtension(f, ".lip"))) map.TryAdd(Path.GetFileNameWithoutExtension(f), new LegacyFile(f, false));
        }
        return map;
    }

    private static (LegacyFile? File, bool Legacy) Find(Dictionary<string, LegacyFile> index, Phase2Line line)
    {
        if (index.TryGetValue(LineNaming.Legacy(line.FinalLine), out var a)) return (a, true);
        if (index.TryGetValue(LineNaming.Dbvo2(line.FinalLine), out var b)) return (b, false);
        return (null, false);
    }

    /// <summary>Preview an import: what the folder would fill, what stays to generate, and what matches nothing.</summary>
    public LegacyScan ScanLegacy(DbvoPatch patch, string folder)
    {
        if (!Directory.Exists(folder)) throw new InvalidOperationException("That folder was not found.");
        var index = IndexAudio(folder);
        var scan = new LegacyScan { Folder = folder, Files = index.Count };
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in patch.Lines.Where(l => !l.IsOmitted && !l.Excluded))
        {
            var (file, legacy) = Find(index, line);
            if (file != null) used.Add(file.Path);
            if (line.Takes.Count > 0) { scan.HaveTakes++; continue; }
            if (file == null) { scan.Missing++; continue; }
            if (file.IsFuz) scan.FromFuz++; else scan.FromWavLip++;
            if (legacy) scan.LegacyNamed++; else scan.HashNamed++;
        }
        var unmatched = index.Values.Where(v => !used.Contains(v.Path)).ToList();
        scan.Unmatched = unmatched.Count;
        scan.UnmatchedExamples = unmatched.Take(5).Select(v => System.IO.Path.GetFileName(v.Path)).ToList();
        return scan;
    }

    /// <summary>
    /// Import an earlier pack (DBVO 1 Legacy names or DBVO 2.0 names, any folder depth) into lines without takes. A .fuz
    /// is unpacked into a WAV + LIP take and kept as it is, so building .fuz files later reuses it unchanged.
    /// </summary>
    public async Task<int> ImportLegacyAsync(DbvoPatch patch, VoicePack voice, string folder, ExternalTools tools,
        IProgress<GenerationProgress>? progress, CancellationToken ct, SemaphoreSlim? stateGate = null)
    {
        var index = IndexAudio(folder);
        var todo = patch.Lines.Where(l => !l.IsOmitted && !l.Excluded && l.Takes.Count == 0)
            .Select(l => (Line: l, Find(index, l).File)).Where(x => x.File != null).ToList();
        var workRoot = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "dbvo");
        Directory.CreateDirectory(workRoot);
        int done = 0, ok = 0, failed = 0;
        var gate = stateGate ?? new SemaphoreSlim(1, 1);
        foreach (var (line, file) in todo)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (file!.IsFuz)
                {
                    using var work = new WorkFolder(workRoot);
                    var (lipBytes, xwm) = FuzFile.Split(await File.ReadAllBytesAsync(file.Path, ct));
                    var x = work.File("a.xwm");
                    await File.WriteAllBytesAsync(x, xwm, ct);
                    var wav = work.File("a.wav");
                    await tools.ToWavAsync(x, wav, 44100, false, 0, ct);
                    var lip = work.File("a.lip");
                    await File.WriteAllBytesAsync(lip, lipBytes, ct);
                    await gate.WaitAsync(ct);
                    try { if (line.Takes.Count == 0) { AddExistingTake(patch, voice, line, wav, lip, "fuz", file.Path, "imported from an earlier pack (.fuz)"); line.NoAdopt = false; ok++; } }
                    finally { gate.Release(); }
                }
                else
                {
                    await gate.WaitAsync(ct);
                    try { if (line.Takes.Count == 0) { AddExistingTake(patch, voice, line, file.Path, System.IO.Path.ChangeExtension(file.Path, ".lip"), "existing", null, "imported from an earlier pack (WAV + LIP)"); line.NoAdopt = false; ok++; } }
                    finally { gate.Release(); }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { failed++; line.LastError = "Could not import: " + ex.Message; }
            progress?.Report(new GenerationProgress(++done, todo.Count, failed, line.FinalLine, null));
        }
        await gate.WaitAsync(CancellationToken.None);
        try { _lib.Save(patch); } finally { gate.Release(); }
        return ok;
    }

    /// <summary>
    /// Add another patch's takes to this one as extra takes, line by line (same in-game line). Nothing here is replaced:
    /// a line keeps its selected take; a line without takes selects the first one added.
    /// </summary>
    public (int Takes, int Lines, int OnlyThere) MergeTakes(DbvoPatch target, VoicePack voice, DbvoPatch source)
    {
        if (target.Id == source.Id) throw new InvalidOperationException("Pick another patch.");
        var byLine = target.Lines.GroupBy(l => l.FinalLine.Trim()).ToDictionary(g => g.Key, g => g.First());
        int takes = 0, lines = 0, onlyThere = 0;
        var srcFolder = _lib.PatchFolder(source.Id);
        var dstFolder = _lib.PatchFolder(target.Id);
        foreach (var sl in source.Lines.Where(l => l.Takes.Count > 0))
        {
            if (!byLine.TryGetValue(sl.FinalLine.Trim(), out var tl)) { onlyThere++; continue; }
            var stem = LineNaming.Stem(tl.FinalLine, voice.Naming);
            bool hadNone = tl.Takes.Count == 0;
            int added = 0;
            foreach (var st in sl.Takes)
            {
                var wav = System.IO.Path.Combine(srcFolder, st.WavPath);
                var lip = System.IO.Path.Combine(srcFolder, st.LipPath);
                if (!File.Exists(wav) || !File.Exists(lip)) continue;
                var t = new Take
                {
                    Slot = tl.Takes.Count == 0 ? 1 : tl.Takes.Max(x => x.Slot) + 1,
                    SpokenText = st.SpokenText,
                    DurationSeconds = st.DurationSeconds,
                    CreatedUtc = st.CreatedUtc,
                    Settings = st.Settings,
                };
                var rel = System.IO.Path.Combine("takes", tl.LineId, t.Slot.ToString("00"));
                t.WavPath = System.IO.Path.Combine(rel, stem + ".wav");
                t.LipPath = System.IO.Path.Combine(rel, stem + ".lip");
                FileLinks.LinkOrCopy(wav, System.IO.Path.Combine(dstFolder, t.WavPath), true);
                FileLinks.LinkOrCopy(lip, System.IO.Path.Combine(dstFolder, t.LipPath), true);
                if (st.OriginalFuz != null && File.Exists(System.IO.Path.Combine(srcFolder, st.OriginalFuz)))
                {
                    t.OriginalFuz = System.IO.Path.Combine(rel, stem + ".fuz");
                    FileLinks.LinkOrCopy(System.IO.Path.Combine(srcFolder, st.OriginalFuz), System.IO.Path.Combine(dstFolder, t.OriginalFuz), true);
                }
                tl.Takes.Add(t);
                added++;
            }
            if (added == 0) continue;
            takes += added; lines++;
            if (hadNone)
            {
                tl.SelectedTakeId = tl.Takes[0].Id;
                tl.Status = Phase2Status.Generated;
                tl.NoAdopt = false;
            }
        }
        _lib.Save(target);
        return (takes, lines, onlyThere);
    }
}

public sealed partial class Phase2Service
{
    /// <summary>The ElevenLabs voice a recording is converted into: the voice pack's Voice Changer voice, else its text-to-speech voice.</summary>
    public static (string VoiceId, string VoiceName) RecordingTarget(VoicePack v) =>
        !string.IsNullOrWhiteSpace(v.Sts.VoiceId) ? (v.Sts.VoiceId, v.Sts.VoiceName) : (v.Eleven.VoiceId, v.Eleven.VoiceName);

    /// <summary>
    /// A microphone recording becomes a take: decode (any format the browser recorded) to 44.1 kHz mono, keep
    /// <paramref name="startSec"/>..<paramref name="endSec"/>, optionally convert it into the voice pack's voice with
    /// ElevenLabs speech-to-speech, apply the voice pack's loudness and tail trim, then make the LIP from the line's text.
    /// </summary>
    public async Task<Take> AddRecordingAsync(DbvoPatch patch, VoicePack voice, Phase2Line line, byte[] audio, string extension,
        double startSec, double endSec, bool convert, ExternalTools tools, CancellationToken ct, SemaphoreSlim? stateGate = null)
    {
        var workRoot = Path.Combine(Path.GetTempPath(), "dbvo");
        Directory.CreateDirectory(workRoot);
        using var work = new WorkFolder(workRoot);
        var input = work.File("in" + (extension.StartsWith('.') ? extension : "." + extension));
        await File.WriteAllBytesAsync(input, audio, ct);
        var cut = work.File("cut.wav");
        var args = new List<string> { "-y", "-hide_banner", "-loglevel", "error", "-i", input };
        if (startSec > 0.001) { args.Add("-ss"); args.Add(startSec.ToString("0.###", CultureInfo.InvariantCulture)); }
        if (endSec > startSec + 0.05) { args.Add("-to"); args.Add(endSec.ToString("0.###", CultureInfo.InvariantCulture)); }
        args.AddRange(new[] { "-ac", "1", "-ar", "44100", "-acodec", "pcm_s16le", "-map_metadata", "-1", cut });
        var r = await ExternalTools.RunAsync(tools.FfmpegPath, args, ct);
        if (r.ExitCode != 0 || !File.Exists(cut)) throw new ToolException("ffmpeg could not read the recording: " + r.StdErr.Trim());
        if (WavSeconds(cut) < 0.2) throw new InvalidOperationException("The recording is too short (under 0.2 s after trimming).");

        var wav = work.File("a.wav");
        if (convert)
        {
            if (string.IsNullOrWhiteSpace(_settings.ElevenLabsApiKey)) throw new InvalidOperationException("Converting needs your ElevenLabs API key (Global Settings).");
            var (voiceId, _) = RecordingTarget(voice);
            if (string.IsNullOrWhiteSpace(voiceId)) throw new InvalidOperationException("Pick a voice for this voice pack first.");
            var sts = voice.Sts;
            var vs = ModelCapabilities.Build(sts.ModelId, sts.Stability, sts.Similarity, sts.Style, sts.SpeakerBoost, 1.0);
            if (vs != null) vs.Speed = null;
            using var client = new ElevenLabsClient(_settings.ElevenLabsApiKey);
            var outBytes = await client.SpeechToSpeechAsync(voiceId, sts.ModelId, await File.ReadAllBytesAsync(cut, ct), "recording.wav", vs, sts.Seed, sts.RemoveBackgroundNoise, sts.OutputFormat, ct);
            string src;
            if (sts.OutputFormat.StartsWith("pcm_", StringComparison.OrdinalIgnoreCase))
            {
                src = work.File("raw.wav");
                ExternalTools.PcmToWav(outBytes, int.Parse(sts.OutputFormat.Split('_')[1]), src);
            }
            else
            {
                src = work.File(sts.OutputFormat.StartsWith("wav", StringComparison.OrdinalIgnoreCase) ? "src.wav" : "src.mp3");
                await File.WriteAllBytesAsync(src, outBytes, ct);
            }
            await tools.ToWavAsync(src, wav, 44100, voice.Loudnorm, voice.GainDb, ct);
        }
        else await tools.ToWavAsync(cut, wav, 44100, voice.Loudnorm, voice.GainDb, ct);

        var w = WavFile.Read(wav);
        w.TrimTailAndFade(voice.TrimTailMs, voice.FadeOutMs);
        w.Write(wav);
        var lip = work.File("a.lip");
        await tools.MakeLipAsync(wav, work.File("r.wav"), lip, SpokenText.StripTags(line.Spoken), ct);

        var gate = stateGate ?? new SemaphoreSlim(1, 1);
        await gate.WaitAsync(CancellationToken.None);
        try
        {
            var take = AddRecordedTake(patch, voice, line, wav, lip, convert);
            take.DurationSeconds = w.DurationSeconds;
            line.NoAdopt = false;
            _lib.Save(patch);
            return take;
        }
        finally { gate.Release(); }
    }
}

/// <summary>How closely a transcript matches the intended text: tags, *actions*, case and punctuation are ignored, and
/// misheard spellings ("vampier") still score high. 0..1.</summary>
public static partial class QaText
{
    [GeneratedRegex(@"\[[^\]]*\]|\*[^*]*\*|<[^>]*>")]
    private static partial Regex Markup();
    [GeneratedRegex(@"[\p{L}\p{N}]+")]
    private static partial Regex Word();

    public static string Normalize(string t)
    {
        var s = Markup().Replace(t ?? "", " ").ToLowerInvariant().Replace("'", "").Replace("’", "");
        return string.Join(' ', Word().Matches(s).Select(m => m.Value));
    }

    public static double Score(string intended, string heard)
    {
        var a = Normalize(intended);
        var b = Normalize(heard);
        if (a.Length == 0 && b.Length == 0) return 1;
        if (a.Length == 0 || b.Length == 0) return 0;
        return Math.Max(Ratio(a.Split(' '), b.Split(' ')), Ratio(a.ToCharArray(), b.ToCharArray()));
    }

    /// <summary>2·matches / (|a| + |b|) with matches from the longest common subsequence (like Python's difflib ratio, close enough).</summary>
    private static double Ratio<T>(IReadOnlyList<T> a, IReadOnlyList<T> b)
    {
        var prev = new int[b.Count + 1];
        var cur = new int[b.Count + 1];
        var eq = EqualityComparer<T>.Default;
        for (int i = 1; i <= a.Count; i++)
        {
            for (int j = 1; j <= b.Count; j++)
                cur[j] = eq.Equals(a[i - 1], b[j - 1]) ? prev[j - 1] + 1 : Math.Max(prev[j], cur[j - 1]);
            (prev, cur) = (cur, prev);
        }
        return 2.0 * prev[b.Count] / (a.Count + b.Count);
    }
}

/// <summary>Spoken text out to a spreadsheet and back (CSV, UTF-8 with BOM, the separator Excel expects here).</summary>
public static class SpokenCsv
{
    public sealed record Row(string LineId, string FileName, string Original, string Spoken);

    public static string Write(IEnumerable<Row> rows, char? separator = null)
    {
        char sep = separator ?? (CultureInfo.CurrentCulture.TextInfo.ListSeparator is { Length: 1 } ls ? ls[0] : ',');
        var sb = new StringBuilder();
        string Q(string v) => "\"" + (v ?? "").Replace("\"", "\"\"") + "\"";
        sb.Append(string.Join(sep, new[] { "Line ID", "File name", "Original line", "Spoken text" }.Select(Q))).Append("\r\n");
        foreach (var r in rows) sb.Append(string.Join(sep, new[] { r.LineId, r.FileName, r.Original, r.Spoken }.Select(Q))).Append("\r\n");
        return sb.ToString();
    }

    /// <summary>Parse a CSV saved by Excel or any editor: comma, semicolon or tab, quoted fields with line breaks.</summary>
    public static List<Row> Read(string text)
    {
        text = text.TrimStart('﻿');
        if (text.StartsWith("sep=", StringComparison.OrdinalIgnoreCase)) text = text[(text.IndexOf('\n') + 1)..];
        int nl = text.IndexOf('\n');
        var head = nl < 0 ? text : text[..nl];
        char sep = new[] { ';', ',', '\t' }.OrderByDescending(c => head.Count(x => x == c)).First();
        var records = Parse(text, sep);
        if (records.Count == 0) return new();
        var header = records[0].Select(h => h.Trim().ToLowerInvariant()).ToList();
        int Col(params string[] names) => header.FindIndex(h => names.Contains(h));
        int id = Col("line id", "id"), file = Col("file name", "filename"), orig = Col("original line", "original"), spoken = Col("spoken text", "spoken");
        if (spoken < 0 || (id < 0 && file < 0)) throw new InvalidOperationException("The file needs a \"Spoken text\" column and a \"Line ID\" or \"File name\" column (export one from DBVO Studio to see the layout).");
        string At(List<string> r, int i) => i >= 0 && i < r.Count ? r[i] : "";
        return records.Skip(1).Where(r => r.Any(x => x.Length > 0)).Select(r => new Row(At(r, id), At(r, file), At(r, orig), At(r, spoken))).ToList();
    }

    private static List<List<string>> Parse(string t, char sep)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var f = new StringBuilder();
        bool q = false;
        for (int i = 0; i < t.Length; i++)
        {
            char c = t[i];
            if (q)
            {
                if (c == '"') { if (i + 1 < t.Length && t[i + 1] == '"') { f.Append('"'); i++; } else q = false; }
                else f.Append(c);
            }
            else if (c == '"') q = true;
            else if (c == sep) { row.Add(f.ToString()); f.Clear(); }
            else if (c == '\r') { }
            else if (c == '\n') { row.Add(f.ToString()); f.Clear(); rows.Add(row); row = new(); }
            else f.Append(c);
        }
        if (f.Length > 0 || row.Count > 0) { row.Add(f.ToString()); rows.Add(row); }
        return rows;
    }
}
