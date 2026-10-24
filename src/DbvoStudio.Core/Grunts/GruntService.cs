using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using DbvoStudio.Core.Audio;
using DbvoStudio.Core.Plugins;
using DbvoStudio.Core.Projects;
using DbvoStudio.Core.Tools;

namespace DbvoStudio.Core.Grunts;

public sealed record GruntIssue(string Severity, string Text, string? EventId = null, string? LineId = null);

/// <summary>One file an export writes, relative to the output folder.</summary>
public sealed class PlannedFile
{
    public required string Rel { get; init; }
    /// <summary>audio | plugin | ini | subtitle</summary>
    public required string Kind { get; init; }
    public GruntLine? Line { get; init; }
    public string EventId { get; init; } = "";
    public byte[]? Bytes { get; set; }
    /// <summary>Audio: wav | xwm | fuz.</summary>
    public string Format { get; init; } = "";
}

public sealed class GruntExportPlan
{
    public List<PlannedFile> Files { get; } = new();
    public List<GruntIssue> Issues { get; } = new();
    public List<PchtPlugin> Plugins { get; } = new();
    public List<string> Notes { get; } = new();
    /// <summary>Files that already exist in the output folder and weren't written by this project's last export.</summary>
    public List<string> WouldOverwrite { get; } = new();
    public bool HasErrors => Issues.Any(i => i.Severity == "error");
}

public sealed record AudioInfo(double Seconds, int SampleRate, int Channels, string Codec);

/// <summary>
/// Player Grunts Tool: assigning audio to events, checking a project, and exporting PCHT / MPCHT / SPVS voice packs.
/// Source files are never changed: imported audio is referenced where it is, exports are built in a temporary folder next
/// to the output and only moved into place once everything was written and checked.
/// </summary>
public static partial class GruntService
{
    public static readonly string[] AudioExtensions = { ".wav", ".xwm", ".fuz", ".mp3", ".ogg", ".flac", ".m4a", ".opus", ".webm", ".aac" };
    public static bool IsAudio(string path) => AudioExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    /// <summary>The absolute path of a line's audio (recordings and generated takes live in the project folder).</summary>
    public static string SourcePath(GruntLine l, string projectFolder) =>
        Path.IsPathRooted(l.Source) ? l.Source : Path.Combine(projectFolder, l.Source);

    public static GruntEventState State(GruntProject p, string eventId)
    {
        if (!p.Events.TryGetValue(eventId, out var s)) { s = new GruntEventState(); p.Events[eventId] = s; }
        return s;
    }

    // ------------------------------------------------------------------ audio

    [GeneratedRegex(@"Duration:\s*(\d+):(\d+):(\d+(?:\.\d+)?)")] private static partial Regex DurationRx();
    [GeneratedRegex(@"Audio:\s*([^,\s]+)[^,]*,\s*(\d+)\s*Hz,\s*([^,]+)")] private static partial Regex StreamRx();

    /// <summary>Length, rate and channels of any audio ffmpeg can read (.fuz is unpacked first).</summary>
    public static async Task<AudioInfo?> ProbeAsync(ExternalTools tools, string path, CancellationToken ct = default)
    {
        string input = path;
        string? tmp = null;
        try
        {
            if (path.EndsWith(".fuz", StringComparison.OrdinalIgnoreCase)) { tmp = UnpackFuz(path); input = tmp; }
            var r = await ExternalTools.RunAsync(tools.FfmpegPath, new[] { "-hide_banner", "-i", input }, ct);
            var d = DurationRx().Match(r.StdErr);
            var s = StreamRx().Match(r.StdErr);
            if (!d.Success && !s.Success) return null;
            double secs = d.Success ? int.Parse(d.Groups[1].Value) * 3600 + int.Parse(d.Groups[2].Value) * 60 + double.Parse(d.Groups[3].Value, CultureInfo.InvariantCulture) : 0;
            int rate = s.Success ? int.Parse(s.Groups[2].Value) : 0;
            var ch = s.Success ? s.Groups[3].Value.Trim() : "";
            int channels = ch.StartsWith("mono") ? 1 : ch.StartsWith("stereo") ? 2 : ch.Contains('.') ? 6 : int.TryParse(ch.Split(' ')[0], out var n) ? n : 0;
            return new AudioInfo(secs, rate, channels, s.Success ? s.Groups[1].Value : "");
        }
        finally { if (tmp != null) try { File.Delete(tmp); } catch { } }
    }

    /// <summary>The xWMA inside a .fuz, as a temporary .xwm file (ffmpeg reads xWMA, not the .fuz wrapper).</summary>
    public static string UnpackFuz(string fuz)
    {
        var (_, xwm) = FuzFile.Split(File.ReadAllBytes(fuz));
        var tmp = Path.Combine(Path.GetTempPath(), "dbvo-fuz-" + Guid.NewGuid().ToString("N")[..10] + ".xwm");
        File.WriteAllBytes(tmp, xwm);
        return tmp;
    }

    /// <summary>A WAV of a line for listening (cached in the project folder, rebuilt when the source changes).</summary>
    public static async Task<string> PreviewAsync(ExternalTools tools, GruntLine line, string projectFolder, CancellationToken ct = default)
    {
        var src = SourcePath(line, projectFolder);
        if (!File.Exists(src)) throw new FileNotFoundException($"The audio file is missing: {src}");
        if (src.EndsWith(".wav", StringComparison.OrdinalIgnoreCase) && IsPlainWav(src)) return src;
        var cache = Path.Combine(projectFolder, "cache", "preview");
        Directory.CreateDirectory(cache);
        var key = Hash($"{src}|{File.GetLastWriteTimeUtc(src).Ticks}|{new FileInfo(src).Length}");
        var outPath = Path.Combine(cache, key + ".wav");
        if (File.Exists(outPath)) return outPath;
        string input = src; string? tmp = null;
        try
        {
            if (src.EndsWith(".fuz", StringComparison.OrdinalIgnoreCase)) { tmp = UnpackFuz(src); input = tmp; }
            await tools.ToWavAsync(input, outPath + ".tmp.wav", 44100, false, 0, ct);
            File.Move(outPath + ".tmp.wav", outPath, true);
        }
        finally { if (tmp != null) try { File.Delete(tmp); } catch { } }
        return outPath;
    }

    /// <summary>True for a 16-bit PCM WAV a browser plays as it is.</summary>
    private static bool IsPlainWav(string path)
    {
        try
        {
            using var f = File.OpenRead(path);
            var b = new byte[36];
            if (f.Read(b, 0, 36) < 36) return false;
            return b[0] == 'R' && b[8] == 'W' && BitConverter.ToUInt16(b, 20) == 1 && BitConverter.ToUInt16(b, 34) == 16;
        }
        catch { return false; }
    }

    public static string Hash(string s) => Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(s)))[..16].ToLowerInvariant();

    // ------------------------------------------------------------------ adding audio

    /// <summary>Natural order: "2.wav" before "10.wav".</summary>
    public static int NaturalCompare(string a, string b)
    {
        static string Key(string s) => Regex.Replace(s.ToLowerInvariant(), @"\d+", m => m.Value.PadLeft(10, '0'));
        return string.CompareOrdinal(Key(a), Key(b));
    }

    /// <summary>Audio files in the given files and folders (folders are not searched below their own level unless asked).</summary>
    public static List<string> CollectAudio(IEnumerable<string> paths, bool recursive = false)
    {
        var list = new List<string>();
        foreach (var p in paths)
        {
            if (Directory.Exists(p)) list.AddRange(Directory.EnumerateFiles(p, "*", recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly).Where(IsAudio).OrderBy(x => x, Comparer<string>.Create(NaturalCompare)));
            else if (File.Exists(p) && IsAudio(p)) list.Add(p);
        }
        // A .wav and a .fuz/.lip of the same line: keep one (the WAV).
        return list.GroupBy(x => Path.Combine(Path.GetDirectoryName(x) ?? "", Path.GetFileNameWithoutExtension(x)), StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderBy(x => Array.IndexOf(AudioExtensions, Path.GetExtension(x).ToLowerInvariant())).First())
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>The text of an SPVS subtitle file (.toml next to the audio) in the given language, or "".</summary>
    public static string ReadSubtitle(string audioPath, string lang)
    {
        var toml = Path.ChangeExtension(audioPath, ".toml");
        if (!File.Exists(toml)) return "";
        foreach (var raw in File.ReadAllLines(toml))
        {
            var m = Regex.Match(raw.Trim().TrimStart('﻿'), @"^([A-Za-z_]+)\s*=\s*""((?:[^""\\]|\\.)*)""");
            if (m.Success && m.Groups[1].Value.Equals(lang, StringComparison.OrdinalIgnoreCase)) return Regex.Unescape(m.Groups[2].Value);
        }
        return "";
    }

    /// <summary>Adds files to an event (pool: as new lines; slots: into the first empty slots, in slot order). Returns the new lines.</summary>
    public static List<GruntLine> AddFiles(GruntProject p, GruntEventDef ev, IEnumerable<string> files, string? slotId = null)
    {
        var st = State(p, ev.Id);
        var added = new List<GruntLine>();
        var known = new HashSet<string>(st.Lines.Select(l => l.Source), StringComparer.OrdinalIgnoreCase);
        foreach (var f in files)
        {
            if (known.Contains(f)) continue;
            var line = new GruntLine { Source = f, Origin = "import", Text = ReadSubtitle(f, p.Spvs.SubtitleLanguage) };
            if (ev.Kind == "slots")
            {
                var slot = slotId != null ? ev.Slots.FirstOrDefault(s => s.Id == slotId) : ev.Slots.FirstOrDefault(s => !st.Lines.Any(l => l.Slot == s.Id));
                if (slot == null) break; // every slot filled
                st.Lines.RemoveAll(l => l.Slot == slot.Id);
                line.Slot = slot.Id;
                if (line.Text.Length == 0) line.Text = slot.Text;
                slotId = null;
            }
            st.Lines.Add(line);
            known.Add(f);
            added.Add(line);
        }
        if (added.Count > 0) st.Skipped = false;
        return added;
    }

    /// <summary>
    /// Reads a voice pack folder laid out for PCHT (Sound\FX\PC Head Tracking\...) or SPVS (Sound\SPVS_AdditionalVoice\...\&lt;slot&gt;\...)
    /// and assigns its files to this project's events, also across mods (a PCHT pack into an SPVS project uses SPVS's own
    /// mapping). Shout and vanilla-based files are matched by their fixed names. Files already in the project are skipped.
    /// </summary>
    public static (int Files, int Events, List<string> Notes) ImportPackFolder(GruntProject p, GruntCatalog cat, string folder)
    {
        var notes = new List<string>();
        int files = 0;
        var touched = new HashSet<string>();
        var dirs = Directory.EnumerateDirectories(folder, "*", SearchOption.AllDirectories).Prepend(folder).ToList();

        string? pchtRoot = FindDir(folder, "Sound", "FX", "PC Head Tracking") ?? (Path.GetFileName(folder).Equals("PC Head Tracking", StringComparison.OrdinalIgnoreCase) ? folder : null);
        string? spvsRoot = null;
        var addRoot = FindDir(folder, "Sound", "SPVS_AdditionalVoice");
        if (addRoot != null)
        {
            // The first voice slot folder that has files (CustomVoiceType\FemaleCustom01, UniqueVoiceType\Akina, ...).
            spvsRoot = Directory.EnumerateDirectories(addRoot).SelectMany(Directory.EnumerateDirectories)
                .FirstOrDefault(d => Directory.EnumerateFiles(d, "*", SearchOption.AllDirectories).Any(IsAudio));
            if (spvsRoot != null) notes.Add($"SPVS voice folder: {Path.GetFileName(spvsRoot)}.");
        }
        if (pchtRoot != null)
        {
            // An MPCHT pack keeps its folders under a prefix folder.
            if (!Directory.Exists(Path.Combine(pchtRoot, "Combat")) && Directory.EnumerateDirectories(pchtRoot).FirstOrDefault(d => Directory.Exists(Path.Combine(d, "Combat"))) is { } pre)
            { pchtRoot = pre; notes.Add($"MPCHT prefix folder: {Path.GetFileName(pre)}."); }
        }

        void AddPool(GruntEventDef ev, string dir)
        {
            if (!Directory.Exists(dir)) return;
            var audio = CollectAudio(new[] { dir });
            if (audio.Count == 0) return;
            var added = AddFiles(p, ev, audio);
            files += added.Count;
            if (added.Count > 0) touched.Add(ev.Id);
        }

        foreach (var ev in cat.Events.Where(e => e.Kind == "pool"))
        {
            if (p.Target == GruntTargets.Pcht)
            {
                if (pchtRoot != null) AddPool(ev, Path.Combine(pchtRoot, Native(ev.Folder)));
                else if (spvsRoot != null)
                    foreach (var sp in SpvsSourcesFor(ev))
                        AddPool(ev, Path.Combine(spvsRoot, Native(sp)));
            }
            else
            {
                if (spvsRoot != null) AddPool(ev, Path.Combine(spvsRoot, Native(ev.Folder)));
                else if (pchtRoot != null)
                    foreach (var pf in ev.PchtFolders) AddPool(ev, Path.Combine(pchtRoot, Native(pf)));
            }
        }

        // Fixed names: shouts (and SPVS vanilla-based files when the script-based folders were empty).
        var byStem = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var voiceRoot = FindDir(folder, "Sound", "Voice");
        if (voiceRoot != null)
            foreach (var f in Directory.EnumerateFiles(voiceRoot, "*", SearchOption.AllDirectories).Where(IsAudio))
                byStem.TryAdd(Path.GetFileNameWithoutExtension(f), f);
        foreach (var ev in cat.Events.Where(e => e.Kind == "slots"))
            foreach (var slot in ev.Slots)
            {
                var st = State(p, ev.Id);
                if (st.Lines.Any(l => l.Slot == slot.Id)) continue;
                var hit = slot.Files.Select(f => byStem.TryGetValue(Leaf(f), out var x) ? x : null).FirstOrDefault(x => x != null);
                // A PCHT pack's shouts in an SPVS project (or the reverse): the same shout part has another file name.
                if (hit == null && slot.Link.Length > 0)
                    hit = OtherNamesFor(slot.Link, p.Target).Select(n => byStem.TryGetValue(n, out var x) ? x : null).FirstOrDefault(x => x != null);
                if (hit == null) continue;
                var added = AddFiles(p, ev, new[] { hit }, slot.Id);
                files += added.Count;
                if (added.Count > 0) touched.Add(ev.Id);
            }
        if (p.Target == GruntTargets.Spvs)
            foreach (var ev in cat.Events.Where(e => e.VanillaNames.Count > 0 && State(p, e.Id).Lines.Count == 0))
            {
                var hits = ev.VanillaNames.Select(n => byStem.TryGetValue(n, out var x) ? x : null).Where(x => x != null).Select(x => x!).ToList();
                if (hits.Count == 0) continue;
                files += AddFiles(p, ev, hits).Count;
                touched.Add(ev.Id);
            }
        if (pchtRoot == null && spvsRoot == null && voiceRoot == null)
            notes.Add("No PCHT (Sound\\FX\\PC Head Tracking) or SPVS (Sound\\SPVS_AdditionalVoice) folders were found there. Pick the voice pack's mod folder, the one that holds Sound.");
        return (files, touched.Count, notes);
    }

    /// <summary>The last part of a Windows-style relative path.</summary>
    public static string Leaf(string rel) => rel[(rel.LastIndexOfAny(new[] { '\\', '/' }) + 1)..];

    /// <summary>A Windows-style relative path for this OS.</summary>
    public static string Native(string rel) => rel.Replace('\\', Path.DirectorySeparatorChar);

    /// <summary>The SPVS folders whose lines suit a PCHT event (the reverse of SPVS's rename-tool mapping).</summary>
    private static IEnumerable<string> SpvsSourcesFor(GruntEventDef pchtEvent)
    {
        var spvs = GruntCatalogs.BuiltIn(GruntTargets.Spvs);
        return spvs.Events.Where(e => e.PchtFolders.Contains(pchtEvent.Folder, StringComparer.OrdinalIgnoreCase)).Select(e => e.Folder);
    }

    /// <summary>File names (no extension) the other mod uses for the same shout part.</summary>
    private static IEnumerable<string> OtherNamesFor(string link, string target)
    {
        var other = GruntCatalogs.BuiltIn(target == GruntTargets.Pcht ? GruntTargets.Spvs : GruntTargets.Pcht);
        return other.Events.SelectMany(e => e.Slots).Where(s => s.Link == link).SelectMany(s => s.Files).Select(Leaf);
    }

    private static string? FindDir(string folder, params string[] parts)
    {
        // The folder itself, or the folder one level up (e.g. the user picked Sound).
        foreach (var start in new[] { folder, Path.GetDirectoryName(folder) ?? folder })
        {
            var cur = start;
            bool ok = true;
            foreach (var part in parts)
            {
                var next = Directory.Exists(cur) ? Directory.EnumerateDirectories(cur).FirstOrDefault(d => Path.GetFileName(d).Equals(part, StringComparison.OrdinalIgnoreCase)) : null;
                if (next == null) { ok = false; break; }
                cur = next;
            }
            if (ok) return cur;
        }
        return null;
    }

    /// <summary>Copies the lines of another Player Grunts project (PCHT ↔ SPVS uses SPVS's own event mapping). Returns lines added.</summary>
    public static int ImportProject(GruntProject into, GruntCatalog intoCat, GruntProject from, GruntCatalog fromCat, string fromFolder, string intoFolder)
    {
        int added = 0;
        GruntLine CopyLine(GruntLine l, string? slot = null)
        {
            var src = SourcePath(l, fromFolder);
            // A recording of the other project is copied in, so deleting that project doesn't break this one.
            if (!Path.IsPathRooted(l.Source) && File.Exists(src))
            {
                var dest = Path.Combine(intoFolder, "audio", "imported", Path.GetFileName(src));
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                if (!File.Exists(dest)) File.Copy(src, dest);
                src = dest;
            }
            return new GruntLine { Source = src, Origin = l.Origin, Text = l.Text, FileName = l.FileName, Slot = slot ?? l.Slot, DurationSeconds = l.DurationSeconds, SampleRate = l.SampleRate, Channels = l.Channels };
        }
        foreach (var ev in intoCat.Events)
        {
            var st = State(into, ev.Id);
            var have = new HashSet<string>(st.Lines.Select(l => l.Source), StringComparer.OrdinalIgnoreCase);
            IEnumerable<(GruntEventDef Ev, GruntEventState St)> sources;
            if (ev.Kind == "slots")
            {
                foreach (var slot in ev.Slots.Where(s => !st.Lines.Any(l => l.Slot == s.Id)))
                {
                    var hit = fromCat.Events.Where(e => e.Kind == "slots").SelectMany(e => e.Slots.Where(s => (s.Link.Length > 0 && s.Link == slot.Link) || (e.Id == ev.Id && s.Id == slot.Id)).Select(s => (e, s)))
                        .Select(x => from.Events.TryGetValue(x.e.Id, out var fs) ? fs.Lines.FirstOrDefault(l => l.Slot == x.s.Id) : null).FirstOrDefault(l => l != null);
                    if (hit == null) continue;
                    st.Lines.Add(CopyLine(hit, slot.Id));
                    added++;
                }
                continue;
            }
            if (into.Target == from.Target) sources = fromCat.Events.Where(e => e.Id == ev.Id).Select(e => (e, State(from, e.Id)));
            else if (into.Target == GruntTargets.Spvs) sources = fromCat.Events.Where(e => e.Kind == "pool" && ev.PchtFolders.Contains(e.Folder, StringComparer.OrdinalIgnoreCase)).Select(e => (e, State(from, e.Id)));
            else sources = fromCat.Events.Where(e => e.Kind == "pool" && e.PchtFolders.Contains(ev.Folder, StringComparer.OrdinalIgnoreCase)).Select(e => (e, State(from, e.Id)));
            foreach (var (_, fst) in sources)
                foreach (var l in fst.Lines)
                {
                    var c = CopyLine(l);
                    if (!have.Add(c.Source)) continue;
                    if (into.Target != from.Target) c.FileName = "";
                    st.Lines.Add(c);
                    added++;
                }
        }
        return added;
    }

    /// <summary>Finds missing files again under a folder, by file name. Returns how many were found.</summary>
    public static int Relink(GruntProject p, string projectFolder, string folder)
    {
        var index = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Where(IsAudio)
            .GroupBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
        int fixedCount = 0;
        foreach (var l in p.Events.Values.SelectMany(s => s.Lines))
        {
            if (File.Exists(SourcePath(l, projectFolder))) continue;
            if (!index.TryGetValue(Path.GetFileName(l.Source), out var cands)) continue;
            // Several files with the name: the one whose parent folders match the old path best.
            var old = l.Source.Replace('/', '\\').Split('\\');
            var best = cands.OrderByDescending(c => old.Reverse().Zip(c.Replace('/', '\\').Split('\\').Reverse()).TakeWhile(x => x.First.Equals(x.Second, StringComparison.OrdinalIgnoreCase)).Count()).First();
            l.Source = best;
            fixedCount++;
        }
        return fixedCount;
    }

    // ------------------------------------------------------------------ export plan and checks

    /// <summary>The voice type folders shouts go into, for the project's gender.</summary>
    private static List<string> ShoutVoiceTypes(GruntProject p)
    {
        string pre = p.Pcht.Multi ? p.Pcht.Prefix : "";
        var list = new List<string>();
        if (p.Gender is "female" or "both") list.Add(pre + "_FemaleBYO");
        if (p.Gender is "male" or "both") list.Add(pre + "_MaleBYO");
        return list;
    }

    private static string Ext(string format, bool dialogue) => format switch { "xwm" => ".xwm", "fuz" => dialogue ? ".fuz" : ".xwm", _ => ".wav" };

    /// <summary>Numbered names: 01..99, or 001.. for a pool of 100 or more (PCHT's own convention).</summary>
    public static string Number(int i, int count) => (i + 1).ToString(count >= 100 ? "000" : "00", CultureInfo.InvariantCulture);

    [GeneratedRegex(@"[^A-Za-z0-9 _\-.'()]")] private static partial Regex BadFileChars();

    /// <summary>
    /// Plans an export: every file it would write, the plugins (built and validated in memory), and every problem.
    /// Nothing is written. <paramref name="pchtSource"/> is the installed PC Head Tracking - MCM.esp (PCHT projects).
    /// </summary>
    public static GruntExportPlan Plan(GruntProject p, GruntCatalog cat, string projectFolder, PluginFile? pchtSource, AppTools tools, GruntCatalog? installedForChecks = null)
    {
        var plan = new GruntExportPlan();
        void Err(string t, string? ev = null, string? line = null) => plan.Issues.Add(new GruntIssue("error", t, ev, line));
        void Warn(string t, string? ev = null, string? line = null) => plan.Issues.Add(new GruntIssue("warning", t, ev, line));
        void Info(string t, string? ev = null) => plan.Issues.Add(new GruntIssue("info", t, ev));
        var fmt = p.Audio.Format;
        if (string.IsNullOrWhiteSpace(p.OutputFolder)) Err("Choose the output folder (Export settings).");
        if (fmt is "xwm" or "fuz" && !tools.Xwma) Err($"{fmt.ToUpperInvariant()} files need xWMAEncode.exe. Pick it in Global Settings → General & tools → Tools, or export WAV.");
        if (fmt == "fuz" && !tools.Lip) Warn("FaceFXWrapper or FonixData.cdf isn't set, so the .fuz files get no lip sync data (grunts rarely need it).");
        if (!tools.Ffmpeg) Err("ffmpeg.exe wasn't found. Pick it in Global Settings → General & tools → Tools.");
        if (p.Audio.Normalize && (p.Audio.TargetLufs > -6 || p.Audio.TargetLufs < -40)) Err("The loudness target must be between -40 and -6 LUFS.");

        var byId = cat.Events.ToDictionary(e => e.Id, StringComparer.OrdinalIgnoreCase);
        foreach (var id in p.Events.Keys.Where(k => !byId.ContainsKey(k) && p.Events[k].Lines.Count > 0))
            Warn($"{p.Events[id].Lines.Count} line(s) belong to the event \"{id}\", which the current event list doesn't have (a custom event that was removed, or an older mod version). They are not exported.", id);

        // Missing or unreadable audio.
        foreach (var ev in cat.Events)
        {
            if (!p.Events.TryGetValue(ev.Id, out var st)) continue;
            foreach (var l in st.Lines)
            {
                var src = SourcePath(l, projectFolder);
                if (!File.Exists(src)) Err($"{ev.Label}: the audio file is missing ({src}). Use Find missing files, or remove the line.", ev.Id, l.Id);
                else if (l.DurationSeconds > 0 && l.DurationSeconds < 0.05) Warn($"{ev.Label}: {Path.GetFileName(src)} is almost silent ({l.DurationSeconds:0.00} s).", ev.Id, l.Id);
                else if (p.Target == GruntTargets.Pcht && ev.Kind == "pool" && l.DurationSeconds > 5) Warn($"{ev.Label}: {Path.GetFileName(src)} is {l.DurationSeconds:0.0} s long; PCHT recommends under 5 s.", ev.Id, l.Id);
                if (ev.Kind == "slots" && (l.Slot.Length == 0 || ev.Slots.All(s => s.Id != l.Slot))) Err($"{ev.Label}: a line isn't in any of the shout's parts.", ev.Id, l.Id);
            }
            var dupSources = st.Lines.GroupBy(l => l.Source, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).ToList();
            foreach (var d in dupSources) Warn($"{ev.Label}: {Path.GetFileName(d.Key)} is assigned {d.Count()} times (it plays more often).", ev.Id);
        }
        var used = p.Events.Where(kv => byId.ContainsKey(kv.Key) && !kv.Value.Skipped && kv.Value.Lines.Count > 0).Select(kv => (Ev: byId[kv.Key], St: kv.Value)).ToList();
        if (used.Count == 0) Err("No event has audio yet.");

        if (p.Target == GruntTargets.Pcht) PlanPcht(p, used, cat, plan, pchtSource, Err, Warn, Info, fmt);
        else PlanSpvs(p, used, cat, plan, Err, Warn, Info, fmt, installedForChecks);

        foreach (var ev in cat.Events.Where(e => e.Kind == "pool" && (!p.Events.TryGetValue(e.Id, out var s) || (s.Lines.Count == 0 && !s.Skipped))))
            plan.Issues.Add(new GruntIssue("info", $"{ev.Category} · {ev.Label}: no audio{(p.Target == GruntTargets.Pcht ? " (the event plays nothing, as with PCHT's own empty folders)" : " (SPVS skips the event)")}.", ev.Id));

        // Overwrites: files in the output that this project's last export didn't write.
        if (!string.IsNullOrWhiteSpace(p.OutputFolder) && Directory.Exists(p.OutputFolder))
        {
            var mine = new HashSet<string>(p.ExportedFiles, StringComparer.OrdinalIgnoreCase);
            foreach (var f in plan.Files)
                if (!mine.Contains(f.Rel) && File.Exists(Path.Combine(p.OutputFolder, f.Rel))) plan.WouldOverwrite.Add(f.Rel);
            if (plan.WouldOverwrite.Count > 0) Warn($"{plan.WouldOverwrite.Count} file(s) in the output folder weren't written by this project and would be replaced, e.g. {plan.WouldOverwrite[0]}.");
        }
        return plan;
    }

    private static void PlanPcht(GruntProject p, List<(GruntEventDef Ev, GruntEventState St)> used, GruntCatalog cat, GruntExportPlan plan, PluginFile? src,
        Action<string, string?, string?> Err, Action<string, string?, string?> Warn, Action<string, string?> Info, string fmt)
    {
        bool multi = p.Pcht.Multi;
        var prefix = p.Pcht.Prefix.Trim();
        if (multi)
        {
            if (prefix.Length == 0) Err("Multi Voice Packs: set the prefix (letters and digits, e.g. the character's name).", null, null);
            else if (!prefix.All(char.IsAsciiLetterOrDigit)) Err("Multi Voice Packs: the prefix may only have letters and digits (no spaces or _).", null, null);
            if (p.Pcht.FullName.Trim().Length == 0) Err("Multi Voice Packs: set the name shown in the MCM.", null, null);
        }
        string root = multi && prefix.Length > 0 ? GruntCatalogs.PchtSoundRoot + "\\" + prefix : GruntCatalogs.PchtSoundRoot;
        var sndrFiles = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        bool needPlugin = multi;
        foreach (var (ev, st) in used.Where(u => u.Ev.Kind == "pool"))
        {
            if (ev.Sndr.Length == 0) { Err($"{ev.Label}: the event has no sound descriptor to list its files.", ev.Id, null); continue; }
            var list = new List<string>();
            for (int i = 0; i < st.Lines.Count; i++)
            {
                var name = Number(i, st.Lines.Count);
                var rel = $@"{root}\{ev.Folder}\{name}{Ext(fmt, false)}";
                plan.Files.Add(new PlannedFile { Rel = rel, Kind = "audio", Line = st.Lines[i], EventId = ev.Id, Format = fmt == "fuz" ? "xwm" : fmt });
                // PCHT's descriptors always name .wav; the game plays the .xwm of the same name when there is no .wav.
                list.Add($@"data\{root}\{ev.Folder}\{name}.wav");
            }
            sndrFiles[ev.Sndr] = list;
            if (st.Lines.Count != ev.BaseSlots) needPlugin = true;
        }
        foreach (var (ev, st) in used.Where(u => u.Ev.Kind == "slots"))
        {
            foreach (var l in st.Lines)
            {
                var slot = ev.Slots.FirstOrDefault(s => s.Id == l.Slot);
                if (slot == null) continue;
                foreach (var f in slot.Files)
                {
                    bool patch = f.Contains(GruntCatalogs.PchtPatchPlugin, StringComparison.OrdinalIgnoreCase);
                    if (patch && !p.Pcht.PatchShouts) continue;
                    foreach (var vt in ShoutVoiceTypes(p))
                    {
                        plan.Files.Add(new PlannedFile { Rel = f.Replace("{vt}", vt) + Ext(fmt, true), Kind = "audio", Line = l, EventId = ev.Id, Format = fmt });
                        // The MPCHT guide puts shouts under the new plugin's folder; the game reads the folder of the plugin that
                        // holds the shout dialogue (PCHT's), so both are written.
                        if (multi && prefix.Length > 0)
                            plan.Files.Add(new PlannedFile { Rel = Regex.Replace(f, @"^Sound\\Voice\\[^\\]+\\", $@"Sound\Voice\PCHTV_{prefix}.esp\").Replace("{vt}", vt) + Ext(fmt, true), Kind = "audio", Line = l, EventId = ev.Id, Format = fmt });
                    }
                }
            }
            if (st.Lines.Count < ev.Slots.Count(s => p.Pcht.PatchShouts || !s.Files.All(f => f.Contains(GruntCatalogs.PchtPatchPlugin))))
                Info($"Shouts · {ev.Label}: {st.Lines.Count} of {ev.Slots.Count} parts have audio; the others keep the game's voice.", ev.Id);
        }
        if (used.Any(u => u.Ev.Kind == "slots") && p.Gender == "both" && !multi)
            Info("Shouts are written for both the female and the male voice type.", null);

        if (needPlugin)
        {
            if (src == null) { Err($"Set the PC Head Tracking folder (Export settings or Global Settings): the {(multi ? "voice set" : "pack's")} plugin is built from its {GruntCatalogs.PchtPlugin}.", null, null); return; }
            try
            {
                PchtPlugin plugin;
                if (multi)
                {
                    plugin = PchtPlugins.BuildVoiceSet(src, prefix, p.Pcht.FullName, p.Gender, sndrFiles, $"Player Grunts voice set {p.Pcht.FullName.Trim()} (DBVO Studio)");
                    plan.Notes.Add($"{plugin.FileName}: an ESL-flagged voice set. Needs \"Multi Voice Packs support for PC Head Tracking\" and FormList Manipulator; pick \"{p.Pcht.FullName.Trim()}\" in the PC Head Tracking MCM.");
                    if (p.Pcht.WriteFlmIni)
                    {
                        var lines = new List<string>(plugin.FlmLines);
                        // PC Head Tracking - Patch.esp's Dawnguard and Dragonborn shouts check their own voice type list.
                        if (p.Pcht.PatchShouts && used.Any(u => u.Ev.Kind == "slots" && u.Ev.Slots.Any(s => s.Files.Any(f => f.Contains(GruntCatalogs.PchtPatchPlugin)))))
                        { lines.Add($"FormList=_VoicePowerCustomPatch|{plugin.MaleVoiceType}"); lines.Add($"FormList=_VoicePowerCustomPatch|{plugin.FemaleVoiceType}"); }
                        plan.Files.Add(new PlannedFile { Rel = $"PCHTV_{prefix}_FLM.ini", Kind = "ini", Bytes = Encoding.ASCII.GetBytes(string.Join("\r\n", lines) + "\r\n") });
                    }
                    else plan.Issues.Add(new GruntIssue("warning", "The FormList Manipulator .ini is off: the voice set won't show in the MCM until its lines are added by hand."));
                }
                else
                {
                    var name = string.IsNullOrWhiteSpace(p.Pcht.PluginName) ? DefaultPluginName(p) : p.Pcht.PluginName.Trim();
                    if (!name.EndsWith(".esp", StringComparison.OrdinalIgnoreCase)) name += ".esp";
                    if (name.Equals(GruntCatalogs.PchtPlugin, StringComparison.OrdinalIgnoreCase) || name.Equals(GruntCatalogs.PchtPatchPlugin, StringComparison.OrdinalIgnoreCase))
                    { Err("The pack's plugin can't have the name of a PCHT plugin (that would replace it).", null, null); return; }
                    var changed = used.Where(u => u.Ev.Kind == "pool" && u.St.Lines.Count != u.Ev.BaseSlots).Select(u => u.Ev.Sndr).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    plugin = PchtPlugins.BuildOverride(src, name, sndrFiles.Where(kv => changed.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value), $"Player Grunts lines for {p.Name} (DBVO Studio)");
                    plan.Notes.Add($"{plugin.FileName}: ESL-flagged, overrides {plugin.Overrides} of PCHT's sound descriptors so they list this pack's number of lines. It has {GruntCatalogs.PchtPlugin} as its master, so it loads after it; let it win over other PCHT voice packs that change the same descriptors.");
                }
                var problems = PchtPlugins.Validate(plugin.Bytes, plugin.FileName, src);
                foreach (var pr in problems) Err($"{plugin.FileName}: {pr}", null, null);
                if (problems.Count == 0)
                {
                    plan.Plugins.Add(plugin);
                    plan.Files.Add(new PlannedFile { Rel = plugin.FileName, Kind = "plugin", Bytes = plugin.Bytes });
                }
            }
            catch (InvalidOperationException ex) { Err(ex.Message, null, null); }
        }
        else if (!needPlugin) plan.Notes.Add("Every event has exactly as many lines as PCHT's own plugin lists, so no plugin is needed.");
    }

    public static string DefaultPluginName(GruntProject p)
    {
        var n = BadFileChars().Replace(p.Name, "").Trim();
        return $"PCHT Lines - {(n.Length > 0 ? n : "Voice Pack")}.esp";
    }

    private static void PlanSpvs(GruntProject p, List<(GruntEventDef Ev, GruntEventState St)> used, GruntCatalog cat, GruntExportPlan plan,
        Action<string, string?, string?> Err, Action<string, string?, string?> Warn, Action<string, string?> Info, string fmt, GruntCatalog? installed)
    {
        var slot = p.Spvs.Slot;
        if (!GruntCatalogs.SpvsSlots.Contains(slot)) { Err("Choose the SPVS voice slot (FemaleCustom01–05 or MaleCustom01–05).", null, null); return; }
        if (!p.Spvs.ScriptBased && !p.Spvs.VanillaBased) Err("Turn on the script-based or the vanilla-based method (or both).", null, null);
        string addRoot = $@"{GruntCatalogs.SpvsAdditionalRoot}\{slot}";
        var lang = string.IsNullOrWhiteSpace(p.Spvs.SubtitleLanguage) ? "en" : p.Spvs.SubtitleLanguage.Trim().ToLowerInvariant();
        var namesInParent = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); // parent folder|name → event
        foreach (var (ev, st) in used.Where(u => u.Ev.Kind == "pool"))
        {
            bool vanilla = ev.Folder.StartsWith("_Vanilla", StringComparison.OrdinalIgnoreCase);
            if (ev.Origin == "custom" && installed != null && ev.Key.Length > 0 && !installed.Events.Any(e => e.Key.Equals(ev.Key, StringComparison.OrdinalIgnoreCase)))
                Warn($"{ev.Label}: the installed SPVS has no category {ev.Key}, so nothing plays these lines until an SPVS version with that event is installed.", ev.Id, null);
            if (p.Spvs.ScriptBased || !vanilla)
            {
                var parent = ev.Folder.Split('\\')[0];
                var stem = (ev.Key.Length > 0 ? ev.Key : Regex.Replace(ev.Folder, @"[^A-Za-z0-9]", "")).ToLowerInvariant();
                for (int i = 0; i < st.Lines.Count; i++)
                {
                    var l = st.Lines[i];
                    var name = l.FileName.Trim().Length > 0 ? l.FileName.Trim() : $"{stem}_{Number(i, st.Lines.Count)}";
                    if (BadFileChars().IsMatch(name)) { Err($"{ev.Label}: the file name \"{name}\" has characters a game path can't use.", ev.Id, l.Id); continue; }
                    if (ev.Folder.Equals(@"Discovered\Specific\Voice", StringComparison.OrdinalIgnoreCase) && l.FileName.Trim().Length == 0)
                        Err($"{ev.Label}: give the line the location's English name as its file name (e.g. Whiterun), or SPVS can't match it to a location.", ev.Id, l.Id);
                    // SPVS asks for names that are unique across the subfolders of one event folder (e.g. all of Magic).
                    if (namesInParent.TryGetValue(parent + "|" + name, out var other) && other != ev.Id)
                        Err($"{ev.Label}: the file name {name} is also used in {byLabel(other)}; SPVS needs names unique within {parent}.", ev.Id, l.Id);
                    namesInParent[parent + "|" + name] = ev.Id;
                    var ext = fmt == "fuz" ? ".fuz" : Ext(fmt, true);
                    var rel = $@"{addRoot}\{ev.Folder}\{name}{ext}";
                    if (plan.Files.Any(f => f.Rel.Equals(rel, StringComparison.OrdinalIgnoreCase))) { Err($"{ev.Label}: two lines would be written as {name}{ext}.", ev.Id, l.Id); continue; }
                    plan.Files.Add(new PlannedFile { Rel = rel, Kind = "audio", Line = l, EventId = ev.Id, Format = fmt });
                    if (p.Spvs.Subtitles && l.Text.Trim().Length > 0 && !vanilla)
                        plan.Files.Add(new PlannedFile { Rel = $@"{addRoot}\{ev.Folder}\{name}.toml", Kind = "subtitle", EventId = ev.Id, Bytes = new UTF8Encoding(false).GetBytes($"{lang} = \"{TomlEscape(l.Text.Trim())}\"\r\n") });
                }
                if (fmt == "xwm") Info($"{ev.Label}: XWM files play without lip sync in SPVS (WAV and FUZ have it).", ev.Id);
            }
            if (vanilla && p.Spvs.VanillaBased && ev.VanillaNames.Count > 0)
            {
                for (int i = 0; i < ev.VanillaNames.Count; i++)
                {
                    var l = st.Lines[i % st.Lines.Count];
                    plan.Files.Add(new PlannedFile { Rel = $@"Sound\Voice\{GruntCatalogs.SpvsPlugin}\SPVS_Replace{slot}\{ev.VanillaNames[i]}{Ext(fmt, true)}", Kind = "audio", Line = l, EventId = ev.Id, Format = fmt });
                }
                if (st.Lines.Count < ev.VanillaNames.Count) Info($"{ev.Label}: the vanilla-based method has {ev.VanillaNames.Count} fixed files; your {st.Lines.Count} line(s) are repeated to fill them.", ev.Id);
                else if (st.Lines.Count > ev.VanillaNames.Count) Info($"{ev.Label}: the vanilla-based method has only {ev.VanillaNames.Count} fixed files, so lines after the first {ev.VanillaNames.Count} are only used by the script-based method.", ev.Id);
            }
        }
        foreach (var (ev, st) in used.Where(u => u.Ev.Kind == "slots"))
            foreach (var l in st.Lines)
            {
                var s = ev.Slots.FirstOrDefault(x => x.Id == l.Slot);
                if (s == null) continue;
                foreach (var f in s.Files) plan.Files.Add(new PlannedFile { Rel = f.Replace("{slot}", slot) + Ext(fmt, true), Kind = "audio", Line = l, EventId = ev.Id, Format = fmt });
            }
        plan.Notes.Add($"SPVS voice slot {slot}: in the SPVS MCM pick {Regex.Replace(slot, @"(Female|Male)Custom0?(\d)", "Custom$2")} under the {(slot.StartsWith("Female") ? "female" : "male")} voices{(p.Spvs.VanillaBased ? ", and choose Vanilla-based or Script-based per category under Voices" : "")}.");
        string byLabel(string id) => cat.Events.FirstOrDefault(e => e.Id == id)?.Label ?? id;
    }

    private static string TomlEscape(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "").Replace("\n", "\\n");

    // ------------------------------------------------------------------ export

    /// <summary>What the export can use.</summary>
    public sealed record AppTools(bool Ffmpeg, bool Xwma, bool Lip);

    public sealed class ExportResult
    {
        public int Written { get; set; }
        public int Converted { get; set; }
        public int Removed { get; set; }
        public string Report { get; set; } = "";
    }

    /// <summary>
    /// Writes a planned export. Everything goes into a temporary folder next to the output first; only when every file is
    /// written are they moved into place, and the files this project's previous export wrote that are no longer part of it
    /// are removed (never anything else). On failure or cancel the temporary folder is deleted and the output is untouched.
    /// </summary>
    public static async Task<ExportResult> ExportAsync(GruntProject p, GruntCatalog cat, GruntExportPlan plan, string projectFolder, ExternalTools tools,
        IProgress<(int Done, int Total, string Text)>? progress, CancellationToken ct)
    {
        if (plan.HasErrors) throw new InvalidOperationException("The project has problems to fix first (see Check).");
        var output = Path.GetFullPath(p.OutputFolder);
        var parent = Path.GetDirectoryName(output) ?? output;
        Directory.CreateDirectory(parent);
        var staging = Path.Combine(parent, "." + Path.GetFileName(output) + ".dbvo-export-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(staging);
        var result = new ExportResult();
        var cache = Path.Combine(projectFolder, "cache", "export");
        Directory.CreateDirectory(cache);
        var converted = new Dictionary<string, string>(); // line+format → cached file
        try
        {
            int done = 0, total = plan.Files.Count;
            foreach (var f in plan.Files)
            {
                ct.ThrowIfCancellationRequested();
                var dest = Path.Combine(staging, f.Rel.Replace('\\', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                if (f.Bytes != null) await File.WriteAllBytesAsync(dest, f.Bytes, ct);
                else if (f.Line != null)
                {
                    var key = f.Line.Id + "|" + f.Format;
                    if (!converted.TryGetValue(key, out var made))
                    {
                        made = await ConvertAsync(p, f.Line, f.Format, projectFolder, cache, tools, ct);
                        converted[key] = made;
                        result.Converted++;
                    }
                    File.Copy(made, dest, true);
                }
                result.Written++;
                progress?.Report((++done, total, Path.GetFileName(f.Rel)));
            }

            // Move into place: remove this project's own old files that the new export doesn't write, then copy over.
            ct.ThrowIfCancellationRequested();
            Directory.CreateDirectory(output);
            var now = new HashSet<string>(plan.Files.Select(f => f.Rel), StringComparer.OrdinalIgnoreCase);
            foreach (var old in p.ExportedFiles.Where(o => !now.Contains(o)))
            {
                var path = Path.Combine(output, old.Replace('\\', Path.DirectorySeparatorChar));
                if (File.Exists(path)) { File.Delete(path); result.Removed++; DeleteEmptyParents(Path.GetDirectoryName(path)!, output); }
            }
            foreach (var f in plan.Files)
            {
                var from = Path.Combine(staging, f.Rel.Replace('\\', Path.DirectorySeparatorChar));
                var to = Path.Combine(output, f.Rel.Replace('\\', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                File.Move(from, to, true);
            }
            p.ExportedFiles = plan.Files.Select(f => f.Rel).ToList();
            p.LastExportUtc = DateTime.UtcNow;
            result.Report = Report(p, cat, plan, result);
            p.LastReport = result.Report;
            await File.WriteAllTextAsync(Path.Combine(projectFolder, "last-export.txt"), result.Report, ct);
            return result;
        }
        finally
        {
            try { if (Directory.Exists(staging)) Directory.Delete(staging, true); } catch { }
        }
    }

    private static void DeleteEmptyParents(string dir, string stopAt)
    {
        try
        {
            while (dir.Length > stopAt.Length && Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
            {
                Directory.Delete(dir);
                dir = Path.GetDirectoryName(dir)!;
            }
        }
        catch { }
    }

    /// <summary>One line converted to the export format, with the loudness settings (cached by source, settings and format).</summary>
    private static async Task<string> ConvertAsync(GruntProject p, GruntLine l, string format, string projectFolder, string cache, ExternalTools tools, CancellationToken ct)
    {
        var src = SourcePath(l, projectFolder);
        var a = p.Audio;
        var key = Hash(string.Join("|", src, File.GetLastWriteTimeUtc(src).Ticks, new FileInfo(src).Length, format, a.Normalize, a.TargetLufs, a.PeakDb, a.GainDb, a.SampleRate, format == "fuz" ? l.Text : "", "v1"));
        var outPath = Path.Combine(cache, key + "." + format);
        if (File.Exists(outPath)) return outPath;
        var workRoot = Path.Combine(Path.GetTempPath(), "dbvo");
        Directory.CreateDirectory(workRoot);
        using var work = new WorkFolder(workRoot);
        string input = src;
        if (src.EndsWith(".fuz", StringComparison.OrdinalIgnoreCase)) { input = work.File("in.xwm"); File.WriteAllBytes(input, FuzFile.Split(File.ReadAllBytes(src)).Xwm); }
        var plain = work.File("a.wav");
        await tools.ToWavAsync(input, plain, a.SampleRate, false, 0, ct);
        double gain = a.GainDb;
        if (a.Normalize)
        {
            var (lufs, peak) = await tools.MeasureAsync(plain, ct);
            l.Lufs = lufs; l.PeakDb = peak;
            if (lufs is { } i && !double.IsInfinity(i))
            {
                double g = a.TargetLufs - i;
                if (peak is { } pk && !double.IsInfinity(pk)) g = Math.Min(g, a.PeakDb - pk); // never past the peak ceiling
                gain += g;
            }
            else if (peak is { } pk2 && !double.IsInfinity(pk2)) gain += Math.Min(0, a.PeakDb - pk2); // too short to measure: only keep peaks under the ceiling
        }
        var wav = work.File("b.wav");
        if (Math.Abs(gain) > 0.01) await tools.ToWavAsync(plain, wav, a.SampleRate, false, gain, ct);
        else File.Copy(plain, wav);
        string made;
        switch (format)
        {
            case "xwm":
                made = work.File("c.xwm");
                await tools.ToXwmaAsync(wav, made, 48000, ct);
                break;
            case "fuz":
            {
                var xwm = work.File("c.xwm");
                await tools.ToXwmaAsync(wav, xwm, 48000, ct);
                byte[] lip = Array.Empty<byte>();
                var text = Naming.SpokenText.StripTags(l.Text).Trim();
                if (text.Length > 0 && File.Exists(tools.FaceFxWrapperPath) && File.Exists(tools.FonixDataPath))
                {
                    try { var lp = work.File("c.lip"); await tools.MakeLipAsync(wav, work.File("r.wav"), lp, text, ct); lip = File.ReadAllBytes(lp); }
                    catch (ToolException) { /* a grunt without phonemes: no lip data */ }
                }
                made = work.File("c.fuz");
                File.WriteAllBytes(made, FuzFile.Build(lip, File.ReadAllBytes(xwm)));
                break;
            }
            default: made = wav; break;
        }
        File.Copy(made, outPath + ".tmp", true);
        File.Move(outPath + ".tmp", outPath, true);
        return outPath;
    }

    public static string Report(GruntProject p, GruntCatalog cat, GruntExportPlan plan, ExportResult r)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Player Grunts export · {p.Name} · {GruntTargets.Name(p.Target)}{(p.Target == GruntTargets.Pcht && p.Pcht.Multi ? " (Multi Voice Packs)" : "")}");
        sb.AppendLine($"{DateTime.Now:yyyy-MM-dd HH:mm} → {p.OutputFolder}");
        sb.AppendLine();
        var audio = plan.Files.Where(f => f.Kind == "audio").ToList();
        var events = audio.Select(f => f.EventId).Distinct().Count();
        sb.AppendLine($"Events with audio: {events}");
        sb.AppendLine($"Audio files written: {audio.Count} ({audio.Select(f => f.Line!.Id).Distinct().Count()} lines, {r.Converted} converted this time)");
        if (p.Target == GruntTargets.Pcht)
        {
            var extra = p.Events.Where(kv => cat.Events.Any(e => e.Id == kv.Key && e.Kind == "pool")).Sum(kv => Math.Max(0, kv.Value.Lines.Count - cat.Events.First(e => e.Id == kv.Key).BaseSlots));
            sb.AppendLine($"Lines beyond PCHT's own slots: {extra}");
        }
        foreach (var pl in plan.Plugins)
            sb.AppendLine($"Plugin: {pl.FileName} ({(pl.Overrides > 0 ? pl.Overrides + " sound descriptor overrides" : pl.NewRecords + " new records")}; masters {string.Join(", ", pl.Masters)}; ESL-flagged; checked)");
        foreach (var f in plan.Files.Where(f => f.Kind == "ini")) sb.AppendLine($"FormList Manipulator: {f.Rel}");
        var subs = plan.Files.Count(f => f.Kind == "subtitle");
        if (subs > 0) sb.AppendLine($"Subtitle files: {subs}");
        if (r.Removed > 0) sb.AppendLine($"Old files of this project removed: {r.Removed}");
        sb.AppendLine($"Loudness: {(p.Audio.Normalize ? $"{p.Audio.TargetLufs:0.#} LUFS, peaks under {p.Audio.PeakDb:0.#} dBTP" : "unchanged")}{(Math.Abs(p.Audio.GainDb) > 0.01 ? $", {p.Audio.GainDb:+0.#;-0.#} dB" : "")}; format {p.Audio.Format.ToUpperInvariant()}, {p.Audio.SampleRate} Hz mono");
        if (plan.Notes.Count > 0) { sb.AppendLine(); sb.AppendLine("Notes:"); foreach (var n in plan.Notes) sb.AppendLine("- " + n); }
        var warns = plan.Issues.Where(i => i.Severity == "warning").ToList();
        if (warns.Count > 0) { sb.AppendLine(); sb.AppendLine("Warnings:"); foreach (var w in warns) sb.AppendLine("- " + w.Text); }
        return sb.ToString();
    }
}
