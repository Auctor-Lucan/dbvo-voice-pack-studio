using System.Text;
using System.Text.RegularExpressions;
using DbvoStudio.Core.Archives;
using DbvoStudio.Core.Audio;
using DbvoStudio.Core.Naming;
using DbvoStudio.Core.Plugins;
using DbvoStudio.Core.Projects;
using DbvoStudio.Core.Tools;

namespace DbvoStudio.Core.Variants;

/// <summary>One variant in the Variant settings: how its take is made.</summary>
public sealed class VariantRow
{
    /// <summary>ElevenLabs stability. v3/v4 use three steps: 0 Creative, 0.5 Natural, 1 Robust.</summary>
    public double Stability { get; set; } = 0.5;
    public double Speed { get; set; } = 1.0;
    /// <summary>A word merged into the prefix tag: "[British Extremely Baleful, softly]".</summary>
    public string Word { get; set; } = "";
    /// <summary>Off: a new random seed for each take. On: <see cref="Seed"/> every time.</summary>
    public bool FixedSeed { get; set; }
    public long? Seed { get; set; }

    public VariantRow Clone() => (VariantRow)MemberwiseClone();
}

public sealed class VariantPreset
{
    public string Name { get; set; } = "";
    public List<VariantRow> Rows { get; set; } = new();
}

/// <summary>
/// A voice pack's Variants setup (Phase 2 → Variants). Only the Variants view reads it: the prefix and rows are used for
/// the variant takes made there and nothing else. Lines added by hand live here too, with their takes in takes\.
/// </summary>
public sealed class VariantsConfig
{
    public string VoicePackId { get; set; } = "";
    /// <summary>Sent in front of every variant instead of the line's own leading tags. Empty: the line's own tags.</summary>
    public string Prefix { get; set; } = "";
    /// <summary>Send the prefix as context (previous_text: not billed, a little weaker) or as text. Null: the app's default.</summary>
    public bool? TagsAsContext { get; set; }
    public string Preset { get; set; } = "Lively";
    public List<VariantRow> Rows { get; set; } = new();
    /// <summary>Your own presets (Save as preset…). The built-in ones are <see cref="BuiltInPresets"/>.</summary>
    public List<VariantPreset> Presets { get; set; } = new();
    /// <summary>The Variants Pack's name: "&lt;name&gt;.bsa" and the empty plugin "&lt;name&gt;.esp" that loads it.</summary>
    public string PackName { get; set; } = "";
    /// <summary>Where the Variants Pack is written. Empty: "Variants Pack" in the voice pack's FOMOD folder.</summary>
    public string OutputFolder { get; set; } = "";
    /// <summary>Lines added by hand that no patch of this voice pack has.</summary>
    public List<Phase2Line> AddedLines { get; set; } = new();
    public DateTime? LastExportUtc { get; set; }
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;

    public static VariantsConfig Default(string voicePackId) => new() { VoicePackId = voicePackId, Rows = BuiltInPresets()[1].Rows };

    public static List<VariantPreset> BuiltInPresets() => new()
    {
        new() { Name = "Subtle", Rows = new() { new() { Stability = 0.5 }, new() { Stability = 0.5 }, new() { Stability = 0.5 } } },
        new() { Name = "Lively", Rows = new() { new() { Stability = 0.5 }, new() { Stability = 0.5, Speed = 0.95, Word = "softly" }, new() { Stability = 0, Speed = 1.05 } } },
        new() { Name = "Moods", Rows = new() { new() { Stability = 0.5, Word = "warm" }, new() { Stability = 0.5, Word = "tired" }, new() { Stability = 0.5, Word = "wry" } } },
    };

    public string PackNameFor(VoicePack v) =>
        !string.IsNullOrWhiteSpace(PackName) ? PackName.Trim()
        : (v.Name.StartsWith("DBVO", StringComparison.OrdinalIgnoreCase) || v.Name.EndsWith("DBVO", StringComparison.OrdinalIgnoreCase) ? v.Name : "DBVO " + v.Name) + " Variants Pack";

    public string OutputFolderFor(VoicePack v) =>
        !string.IsNullOrWhiteSpace(OutputFolder) ? OutputFolder.Trim()
        : string.IsNullOrWhiteSpace(v.Destinations.ReleaseFolder) ? "" : Path.Combine(v.Destinations.ReleaseFolder, "Variants Pack");
}

/// <summary>What one variant take is made with (passed to generation).</summary>
public sealed record VariantSpec(string Spoken, double Stability, double Speed, long? Seed, bool TagsAsContext, bool Keep);

/// <summary>Every line of a voice pack with the same file name: the same audio in game, so one set of variants.</summary>
public sealed class VariantGroup
{
    public string Key { get; init; } = "";
    public string Text { get; init; } = "";
    public string Stem { get; init; } = "";
    /// <summary>Patch = null for a line added by hand.</summary>
    public List<(DbvoPatch? Patch, Phase2Line Line)> Members { get; } = new();

    public IEnumerable<(DbvoPatch? Patch, Phase2Line Line, Take Take)> Kept =>
        Members.SelectMany(m => m.Line.Takes.Where(t => t.Kept).Select(t => (m.Patch, m.Line, t))).OrderBy(x => x.t.CreatedUtc);
    public int KeptCount => Members.Sum(m => m.Line.Takes.Count(t => t.Kept));
    public int TakeCount => Members.Sum(m => m.Line.Takes.Count);
    public int PendingCount => Members.Sum(m => m.Line.Takes.Count(t => t.VariantPending && !t.Kept));
    public bool Added => Members.All(m => m.Patch == null);

    /// <summary>Where new variant takes go: the line that already has the most kept takes, else the first patch by name.</summary>
    public (DbvoPatch? Patch, Phase2Line Line) Home =>
        Members.OrderByDescending(m => m.Line.Takes.Count(t => t.Kept)).ThenBy(m => m.Patch == null ? 1 : 0)
            .ThenBy(m => m.Patch?.Name ?? "", StringComparer.OrdinalIgnoreCase).First();
}

public static class VariantsService
{
    /// <summary>ElevenLabs seeds are 32-bit unsigned.</summary>
    public const long MaxSeed = 4294967295;
    /// <summary>DBReV tries name(1)…(19) next to the line's own file.</summary>
    public const int DbrevMax = 19;
    /// <summary>DBVO 2 tries numbered files 0…256 in the line's folder.</summary>
    public const int Dbvo2Max = 256;

    /// <summary>The voice pack's lines grouped by file name, across every patch and the hand-added lines.</summary>
    public static List<VariantGroup> Groups(VoicePack voice, IEnumerable<DbvoPatch> patches, VariantsConfig cfg)
    {
        var map = new Dictionary<string, VariantGroup>(StringComparer.Ordinal);
        void Add(DbvoPatch? p, Phase2Line l)
        {
            if (l.Excluded || string.IsNullOrWhiteSpace(l.FinalLine)) return;
            var stem = l.FileStem ?? LineNaming.Stem(l.FinalLine, voice.Naming);
            var key = voice.Naming == NamingMode.Legacy ? stem.ToUpperInvariant() : stem;
            if (!map.TryGetValue(key, out var g)) map[key] = g = new VariantGroup { Key = key, Text = l.FinalLine, Stem = stem };
            g.Members.Add((p, l));
        }
        foreach (var p in patches.Where(p => p.VoicePackId == voice.Id).OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
            foreach (var l in p.Lines) Add(p, l);
        foreach (var l in cfg.AddedLines) Add(null, l);
        return map.Values.ToList();
    }

    private static readonly Regex Leading = new(@"^\s*((?:\[[^\]]*\]\s*)*)", RegexOptions.Compiled);
    private static readonly Regex Tag = new(@"\[[^\]]*\]", RegexOptions.Compiled);

    /// <summary>"British Extremely Baleful" → "[British Extremely Baleful]"; tags as written stay.</summary>
    public static string NormalizePrefix(string prefix)
    {
        var p = (prefix ?? "").Trim();
        if (p.Length == 0) return "";
        if (!p.Contains('[')) return "[" + p.Trim('[', ']').Trim() + "]";
        return Regex.Replace(p, @"\s+", " ");
    }

    /// <summary>
    /// The text sent for one variant: the prefix (or, with none, the line's own leading tags) with the row's word merged
    /// into its last tag, then the line's words. "[British Extremely Baleful]" + "softly" + "Falkreath" →
    /// "[British Extremely Baleful, softly] Falkreath". One tag, not two: two leading tags weaken each other.
    /// </summary>
    public static string VariantText(string lineSpoken, string prefix, string word)
    {
        var m = Leading.Match(lineSpoken ?? "");
        var words = (lineSpoken ?? "")[m.Length..].Trim();
        var lead = NormalizePrefix(prefix);
        if (lead.Length == 0) lead = Regex.Replace(m.Groups[1].Value.Trim(), @"\s+", " ");
        var w = (word ?? "").Trim().Trim('[', ']').Trim();
        if (w.Length > 0)
        {
            var tags = Tag.Matches(lead);
            if (tags.Count == 0) lead = (lead + " [" + w + "]").Trim();
            else
            {
                var last = tags[^1];
                var inner = last.Value[1..^1].Trim();
                bool has = Regex.IsMatch(inner, @"(^|[\s,])" + Regex.Escape(w) + @"($|[\s,])", RegexOptions.IgnoreCase);
                if (!has) lead = lead[..last.Index] + "[" + (inner.Length > 0 ? inner + ", " + w : w) + "]" + lead[(last.Index + last.Length)..];
            }
        }
        return lead.Length > 0 ? lead + " " + words : words;
    }

    /// <summary>Characters ElevenLabs bills for a variant text: with the tags sent as context, only the words.</summary>
    public static int BilledLength(string text, bool tagsAsContext)
    {
        if (!tagsAsContext) return text.Length;
        var m = Leading.Match(text);
        return m.Success && text.Length > m.Length ? text.Length - m.Length : text.Length;
    }

    public static long NewSeed() => Random.Shared.NextInt64(1, MaxSeed);

    /// <summary>A line typed by hand: the reasons it can't be voiced as is (empty = fine), with the script's rules.</summary>
    public static List<string> CheckTypedLine(string text)
    {
        var why = Rules.ScriptRules.Explain(text);
        if (why.Count == 0 && !Rules.ScriptRules.IsVoiceReady(text)) why.Add("DBVO's script wouldn't accept this line as it is.");
        return why;
    }

    /// <summary>A new hand-added line for <paramref name="text"/> (the in-game line) with its spoken text.</summary>
    public static Phase2Line NewAddedLine(VoicePack voice, string text, string spoken)
    {
        var stem = LineNaming.Stem(text, voice.Naming);
        return new Phase2Line
        {
            LineId = NewId.Short(),
            Key = voice.Naming == NamingMode.Legacy ? stem.ToUpperInvariant() : stem,
            FinalLine = text,
            OriginalLine = text,
            Spoken = string.IsNullOrWhiteSpace(spoken) ? SpokenText.DefaultFor(text) : spoken.Trim(),
        };
    }

    /// <summary>The .fuz of a take: an imported .fuz as it is, else built from its WAV + LIP (through the .fuz cache).</summary>
    public static async Task<byte[]> TakeFuzAsync(string root, Take t, ExternalTools tools, FuzCache cache, string encoder, string workRoot, CancellationToken ct)
    {
        var original = t.OriginalFuz != null ? Path.Combine(root, t.OriginalFuz) : null;
        if (original != null && File.Exists(original)) return await File.ReadAllBytesAsync(original, ct);
        var wavPath = Path.Combine(root, t.WavPath);
        var lipBytes = await File.ReadAllBytesAsync(Path.Combine(root, t.LipPath), ct);
        var key = FuzCache.Key(await File.ReadAllBytesAsync(wavPath, ct), lipBytes, encoder);
        if (cache.TryGet(key) is { } hit) return hit;
        using var work = new WorkFolder(workRoot);
        var wav = work.File("a.wav");
        File.Copy(wavPath, wav, true);
        var xwm = work.File("a.xwm");
        await tools.ToXwmaAsync(wav, xwm, 48000, ct);
        var fuz = FuzFile.Build(lipBytes, await File.ReadAllBytesAsync(xwm, ct));
        cache.Put(key, fuz);
        return fuz;
    }

    /// <summary>The archive paths of a line's variants: DBVO 2 numbered files in its folder, and DBReV name(n) files.</summary>
    public static List<(string Dbvo2, string? Dbrev)> VariantPaths(string packId, string stem, int count)
    {
        var list = new List<(string, string?)>();
        var dir = @"sound\dbvo\" + packId.ToLowerInvariant() + @"\";
        for (int i = 1; i <= Math.Min(count, Dbvo2Max); i++)
            list.Add((dir + stem + @"\" + i + ".fuz", i <= DbrevMax ? dir + stem + "(" + i + ").fuz" : null));
        return list;
    }
}

public sealed class VariantsPackReport
{
    public int Lines { get; set; }
    public int Takes { get; set; }
    public int Files { get; set; }
    public long Bytes { get; set; }
    public string Bsa { get; set; } = "";
    public string Plugin { get; set; } = "";
    public string ListFile { get; set; } = "";
    public List<string> Skipped { get; } = new();
}

/// <summary>Builds the Variants Pack: one BSA holding every line's variants in both formats, plus the empty plugin that loads it.</summary>
public sealed class VariantsPackBuilder
{
    private readonly Library _lib;
    public VariantsPackBuilder(Library lib) => _lib = lib;

    public string RootOf(VoicePack v, DbvoPatch? p) => p == null ? _lib.VariantsFolder(v.Id) : _lib.PatchFolder(p.Id);

    /// <summary>
    /// Lines with two or more kept (★) takes get every kept take, oldest first, as
    /// sound\dbvo\&lt;pack&gt;\&lt;line&gt;\1.fuz, 2.fuz… (DBVO 2, any naming mode) and &lt;line&gt;(1).fuz… (DBReV playing a
    /// DBVO 1 pack). Folder names are stored exactly, trailing period included: the game finds them that way inside a BSA.
    /// </summary>
    public async Task<VariantsPackReport> BuildAsync(VoicePack voice, List<VariantGroup> groups, VariantsConfig cfg, ExternalTools tools,
        IProgress<GenerationProgress>? progress = null, CancellationToken ct = default)
    {
        if (voice.Naming != NamingMode.Legacy) throw new InvalidOperationException("The Variants Pack is for voice packs with DBVO 1 (Legacy) names. DBVO 2.0 patches already export their ★ variants with each patch.");
        var folder = cfg.OutputFolderFor(voice);
        if (string.IsNullOrWhiteSpace(folder)) throw new InvalidOperationException("Set the folder for the Variants Pack first (Variant settings), or a release folder for this voice pack.");
        var name = cfg.PackNameFor(voice);
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) throw new InvalidOperationException($"\"{name}\" can't be a file name. Change the Variants Pack name.");
        var rep = new VariantsPackReport();
        var todo = groups.Where(g => g.KeptCount >= 2).OrderBy(g => g.Stem, StringComparer.OrdinalIgnoreCase).ToList();
        if (todo.Count == 0) throw new InvalidOperationException("No line has two or more ★ takes yet, so there are no variants to pack.");
        var stage = Path.Combine(Path.GetTempPath(), "dbvo-variants-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(stage);
        var workRoot = Path.Combine(Path.GetTempPath(), "dbvo");
        Directory.CreateDirectory(workRoot);
        var cache = new FuzCache(_lib.FuzCacheFolder);
        var encoder = FuzCache.EncoderId(tools.XwmaEncodePath, 48000);
        var entries = new List<BsaWriter.Entry>();
        var list = new StringBuilder();
        int done = 0, n = 0;
        try
        {
            foreach (var g in todo)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report(new GenerationProgress(done, todo.Count, 0, g.Text, "Building the variants"));
                var paths = VariantsService.VariantPaths(voice.PackId, g.Stem, g.KeptCount);
                if (!BsaWriter.CanStore(paths[0].Dbvo2))
                {
                    rep.Skipped.Add($"{g.Text}: its file name has letters a BSA can't store.");
                    done++;
                    continue;
                }
                var kept = g.Kept.Take(paths.Count).ToList();
                int i = 0;
                foreach (var (p, _, take) in kept)
                {
                    var fuz = await VariantsService.TakeFuzAsync(RootOf(voice, p), take, tools, cache, encoder, workRoot, ct);
                    var file = Path.Combine(stage, (n++).ToString("D6") + ".fuz");
                    await File.WriteAllBytesAsync(file, fuz, ct);
                    rep.Bytes += fuz.Length;
                    entries.Add(new BsaWriter.Entry(paths[i].Dbvo2, file));
                    if (paths[i].Dbrev is { } d) entries.Add(new BsaWriter.Entry(d, file));
                    // A line added by hand has no patch exporting its own file: the first variant is that file too (DBVO 1
                    // plays it, and DBReV looks for it before name(1)…).
                    if (i == 0 && g.Added) entries.Add(new BsaWriter.Entry(@"sound\dbvo\" + voice.PackId.ToLowerInvariant() + @"\" + g.Stem + ".fuz", file));
                    i++;
                }
                rep.Lines++;
                rep.Takes += kept.Count;
                list.AppendLine($"{g.Text}\t{kept.Count} variant(s)\t{g.Stem}");
                done++;
            }
            if (rep.Lines == 0) throw new InvalidOperationException("None of the lines with variants could be packed: " + string.Join(" ", rep.Skipped));
            Directory.CreateDirectory(folder);
            rep.Bsa = Path.Combine(folder, name + ".bsa");
            rep.Plugin = Path.Combine(folder, name + ".esp");
            rep.ListFile = Path.Combine(folder, name + " - lines.txt");
            progress?.Report(new GenerationProgress(done, todo.Count, 0, name + ".bsa", "Packing the BSA"));
            rep.Files = BsaWriter.Write(rep.Bsa, entries, ct);
            FixPluginWriter.WriteSafely(rep.Plugin, FixPluginWriter.LoaderBytes());
            await File.WriteAllTextAsync(rep.ListFile,
                $"{name}: {rep.Lines} lines, {rep.Takes} variant takes, built {DateTime.Now:yyyy-MM-dd HH:mm} by DBVO Studio.\r\n" +
                "Line\tVariants\tFile name\r\n" + list.ToString().Replace("\n", "\r\n").Replace("\r\r\n", "\r\n"), new UTF8Encoding(false), ct);
        }
        finally
        {
            try { Directory.Delete(stage, true); } catch { }
        }
        cfg.LastExportUtc = DateTime.UtcNow;
        return rep;
    }
}
