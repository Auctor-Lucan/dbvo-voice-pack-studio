using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using DbvoStudio.Core.Archives;
using DbvoStudio.Core.Fomod;
using DbvoStudio.Core.Projects;
using DbvoStudio.Core.Tools;

namespace DbvoStudio.Core.Books;

public sealed class BooksExportReport
{
    public int Books { get; set; }
    public int Files { get; set; }
    public int Joined { get; set; }
    public int Reused { get; set; }
    public int Failed { get; set; }
    /// <summary>Books left out because a part has no take yet (per voice).</summary>
    public List<string> Missing { get; set; } = new();
    public List<string> Errors { get; set; } = new();
    public List<string> Archives { get; set; } = new();
    public string? Ini { get; set; }
    public string? Topic { get; set; }
    public string? Report { get; set; }
    public string? Fomod { get; set; }
    public int IniLines { get; set; }
    public List<string> Warnings { get; set; } = new();
    public string Folder { get; set; } = "";
}

/// <summary>
/// Export: joined, encoded audio for every voice, packed per narrator (and one archive for NPC voices) as a BSA with
/// a dummy plugin, or as loose files; the VBoS .ini (only books that have audio); a topic file in the IVBOS format;
/// a validation report; and a FOMOD installer shaped like the IVBOS release.
/// </summary>
public static class BooksExporter
{
    public const string SoundFolder = @"sound\fx\voicedbooksofskyrim";

    /// <summary>The empty light plugin that loads an archive (49 bytes, as in the IVBOS release).</summary>
    public static readonly byte[] DummyEsp =
    {
        0x54, 0x45, 0x53, 0x34, 0x19, 0x00, 0x00, 0x00, 0x00, 0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x2C, 0x00, 0x00, 0x00, 0x48, 0x45, 0x44, 0x52, 0x0C, 0x00, 0x9A, 0x99,
        0xD9, 0x3F, 0x00, 0x00, 0x00, 0x00, 0x00, 0x08, 0x00, 0x00, 0x43, 0x4E, 0x41, 0x4D, 0x01, 0x00, 0x00,
    };

    public static string ModLabel(BooksProject p) => (p.ModName.Trim().Length > 0 ? p.ModName.Trim() : p.Name.Trim()).Trim();

    /// <summary>"IVBOS VIGILANT - Female Narrator" style archive name for a voice group.</summary>
    public static string ArchiveName(BooksProject p, string group)
    {
        var prefix = p.ArchivePrefix.Trim();
        var name = string.Join(" ", new[] { prefix, ModLabel(p) }.Where(x => x.Length > 0)) + " - " + group;
        return string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim();
    }

    public static string IniFileName(BooksProject p)
    {
        var n = p.IniName.Trim().Length > 0 ? p.IniName.Trim() : string.Concat(ModLabel(p).Where(char.IsLetterOrDigit));
        if (n.EndsWith("_VBOS.ini", StringComparison.OrdinalIgnoreCase)) return n;
        return (n.Length > 0 ? n : "Books") + "_VBOS.ini";
    }

    /// <summary>The voice groups of an export: one per narrator, plus "NPC Voices" for books read by voice types.</summary>
    public static List<(string Group, List<string> Voices)> Groups(BooksProject p)
    {
        var g = p.Narrators.Select(n => (n.Name.Trim().Length > 0 ? n.Name.Trim() : "Narrator", new List<string> { BooksProject.NarratorKey(n.Id) })).ToList();
        var vts = p.Lines.Where(l => !l.Excluded && l.Voice.StartsWith("vt:")).Select(l => l.Voice).Distinct().OrderBy(x => x).ToList();
        if (vts.Count > 0) g.Add(("NPC Voices", vts));
        return g;
    }

    public static async Task<BooksExportReport> ExportAsync(BooksProject p, string projectFolder, ExternalTools tools, bool writeFomod,
        IProgress<GenerationProgress>? progress, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(p.OutputFolder)) throw new InvalidOperationException("Choose an output folder first (Export ▾ → Output folder).");
        if (p.Narrators.Count == 0 && !p.Lines.Any(l => l.Voice.StartsWith("vt:"))) throw new InvalidOperationException("Add a narrator first (Voices tab).");
        var rep = new BooksExportReport { Folder = p.OutputFolder };
        Directory.CreateDirectory(p.OutputFolder);
        var groups = Groups(p);
        var books = p.Books.Where(b => !b.Excluded && !b.Gone).ToList();
        var jobs = new List<(string Group, string Voice, Book Book, string Variant)>();
        foreach (var (group, voices) in groups)
            foreach (var b in books)
                foreach (var v in BooksService.VoicesFor(p, b).Where(voices.Contains))
                {
                    jobs.Add((group, v, b, ""));
                    foreach (var variant in b.Variants.Where(x => x.Spoken.Trim().Length > 0)) jobs.Add((group, v, b, variant.Id));
                }
        // Build (or reuse) every join, then pack.
        var results = new Dictionary<(string Group, string Stem, string Variant), BookJoin>();
        int done = 0;
        foreach (var job in jobs)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(new GenerationProgress(done, jobs.Count, rep.Failed, job.Book.Title, null));
            try
            {
                var before = p.Joins.TryGetValue(BookJoiner.JoinKey(job.Voice, job.Book.Stem, job.Variant), out var old) ? old.Fingerprint : null;
                var j = await BookJoiner.JoinAsync(p, projectFolder, job.Voice, job.Book, job.Variant, tools, encode: true, ct);
                if (j == null) rep.Missing.Add($"{job.Group}: {job.Book.Title} ({job.Book.Stem}{(job.Variant.Length > 0 ? ", variant" : "")})");
                else
                {
                    results[(job.Group, job.Book.Stem, job.Variant)] = j;
                    if (before == j.Fingerprint) rep.Reused++; else rep.Joined++;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                rep.Failed++;
                rep.Errors.Add($"{job.Book.Stem} ({BooksService.VoiceName(p, job.Voice)}): {Text.FriendlyError.From(ex)}");
            }
            done++;
        }
        progress?.Report(new GenerationProgress(done, jobs.Count, rep.Failed, "Packing", null));

        var written = new List<string>();
        // Main audio per group: BSA + dummy plugin, or loose files.
        foreach (var (group, _) in groups)
        {
            var name = ArchiveName(p, group);
            var files = results.Where(r => r.Key.Group == group && r.Key.Variant.Length == 0).Select(r => (Stem: r.Key.Stem, Path: Path.Combine(projectFolder, r.Value.Xwm))).ToList();
            if (files.Count == 0) continue;
            var dir = Path.Combine(p.OutputFolder, name);
            if (p.Packaging == "loose")
            {
                var snd = Path.Combine(dir, SoundFolder);
                Directory.CreateDirectory(snd);
                foreach (var f in files) { File.Copy(f.Path, Path.Combine(snd, f.Stem + ".xwm"), true); written.Add(Path.Combine(name, SoundFolder, f.Stem + ".xwm")); }
            }
            else
            {
                Directory.CreateDirectory(dir);
                var bsa = Path.Combine(dir, name + ".bsa");
                BsaWriter.Write(bsa, files.Select(f => new BsaWriter.Entry(SoundFolder + "\\" + f.Stem + ".xwm", f.Path)), ct);
                File.WriteAllBytes(Path.Combine(dir, name + ".esp"), DummyEsp);
                written.Add(Path.Combine(name, name + ".bsa"));
                written.Add(Path.Combine(name, name + ".esp"));
                rep.Archives.Add(Path.Combine(name, name + ".bsa"));
            }
            rep.Files += files.Count;
        }
        // Variants: loose files chosen by the installer (they override the archive copy when the plugin is there).
        foreach (var b in books)
            foreach (var v in b.Variants.Where(x => x.Spoken.Trim().Length > 0))
                foreach (var (group, _) in groups)
                {
                    if (!results.TryGetValue((group, b.Stem, v.Id), out var j)) continue;
                    var folder = Path.Combine(p.OutputFolder, VariantFolder(p, v), group, SoundFolder);
                    Directory.CreateDirectory(folder);
                    File.Copy(Path.Combine(projectFolder, j.Xwm), Path.Combine(folder, b.Stem + ".xwm"), true);
                    written.Add(Path.Combine(VariantFolder(p, v), group, SoundFolder, b.Stem + ".xwm"));
                    rep.Files++;
                }

        // The VBoS .ini: books that have audio in at least one group.
        var withAudio = books.Where(b => results.Keys.Any(k => k.Stem == b.Stem && k.Variant.Length == 0)).ToList();
        rep.Books = withAudio.Count;
        if (p.WriteIni)
        {
            var ini = BuildIni(withAudio);
            var dir = Path.Combine(p.OutputFolder, "VBOS INI");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, IniFileName(p));
            await File.WriteAllTextAsync(path, ini, new UTF8Encoding(false), ct);
            rep.Ini = Path.GetRelativePath(p.OutputFolder, path);
            rep.IniLines = ini.Split('\n').Count(l => l.Contains('~'));
            written.Add(rep.Ini);
        }
        // Topic file (IVBOS format) for reference and for other tools.
        {
            var path = Path.Combine(p.OutputFolder, Path.GetFileNameWithoutExtension(IniFileName(p)).Replace("_VBOS", "") + "_IVBOS_Topic.txt");
            await File.WriteAllTextAsync(path, BuildTopic(p, books), new UTF8Encoding(false), ct);
            rep.Topic = Path.GetRelativePath(p.OutputFolder, path);
            written.Add(rep.Topic);
        }
        if (writeFomod)
        {
            WriteFomod(p, groups.Select(g => g.Group).Where(g => results.Keys.Any(k => k.Group == g)).ToList(), books, results);
            rep.Fomod = "fomod";
            written.Add(Path.Combine("fomod", "ModuleConfig.xml"));
            written.Add(Path.Combine("fomod", "info.xml"));
        }
        // Clean up files an earlier export wrote that this one didn't. Not when a book failed this time: its earlier
        // audio stays in place (and on the list) rather than leaving the book silent in game.
        if (rep.Failed == 0)
            foreach (var stale in p.ExportedFiles.Except(written, StringComparer.OrdinalIgnoreCase))
            {
                var f = Path.Combine(p.OutputFolder, stale);
                try { if (File.Exists(f)) File.Delete(f); } catch { }
            }
        p.ExportedFiles = rep.Failed == 0 ? written : written.Union(p.ExportedFiles, StringComparer.OrdinalIgnoreCase).ToList();
        p.LastExportUtc = DateTime.UtcNow;
        var report = Validate(p, projectFolder, rep, results);
        var rpath = Path.Combine(p.OutputFolder, Path.GetFileNameWithoutExtension(IniFileName(p)).Replace("_VBOS", "") + "_IVBOS_FULL_VALIDATION.txt");
        await File.WriteAllTextAsync(rpath, report, new UTF8Encoding(false), ct);
        rep.Report = Path.GetRelativePath(p.OutputFolder, rpath);
        p.ExportedFiles.Add(rep.Report);
        return rep;
    }

    public static string VariantFolder(BooksProject p, BookVariant v) =>
        ArchiveName(p, $"{(v.Label.Length > 0 ? v.Label : Path.GetFileNameWithoutExtension(v.Plugin))} {(v.WhenInstalled ? "Installed" : "NOT Installed")}");

    /// <summary>The VBoS .ini: "0xLOCALID~Plugin|sound\fx\voicedbooksofskyrim\stem.xwm", grouped under ";Plugin" headers.</summary>
    public static string BuildIni(IEnumerable<Book> books)
    {
        var sb = new StringBuilder();
        var byPlugin = books.SelectMany(b => b.Records.Select(r => (r, b))).GroupBy(x => x.r.Plugin, StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var g in byPlugin)
        {
            sb.Append(';').Append(Path.GetFileNameWithoutExtension(g.Key)).Append("\r\n");
            foreach (var (r, b) in g.OrderBy(x => x.b.Stem, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.r.FormId))
            {
                var key = $"0x{r.FormId & 0xFFFFFF:X}~{r.Plugin}";
                if (!seen.Add(key)) continue;
                sb.Append(key).Append('|').Append(SoundFolder).Append('\\').Append(b.Stem).Append(".xwm\r\n");
            }
        }
        return sb.ToString();
    }

    /// <summary>The IVBOS topic file: one line per book, "stem|VoiceType|spoken text" (VoiceType only when the book is read by an NPC voice).</summary>
    public static string BuildTopic(BooksProject p, IEnumerable<Book> books)
    {
        var sb = new StringBuilder();
        foreach (var b in books)
        {
            sb.Append(b.Stem).Append('|');
            if (b.Reader == "npc" && b.VoiceType.Length > 0 && p.NpcVoices) sb.Append(b.VoiceType).Append('|');
            sb.Append(BookText.OneLine(b.Spoken)).Append("\r\n");
        }
        return sb.ToString();
    }

    private static void WriteFomod(BooksProject p, List<string> groups, List<Book> books, Dictionary<(string Group, string Stem, string Variant), BookJoin> results)
    {
        var mod = ModLabel(p);
        var proj = new FomodProject { Name = mod, ModuleName = $"Voiced Books - {mod}", Encoding = "utf-16" };
        proj.Info = new FomodInfo { Name = $"Voiced Books - {mod}", Author = "", Description = $"Narrated books, letters and journals for {mod}, for Voiced Books of Skyrim (VBoS).", Groups = new() { "Audio - Voice" } };
        var step = new FomodStep { Name = $"Voiced Books - {mod}" };
        var group = new FomodGroup { Name = "Narrator", Type = "SelectExactlyOne" };
        bool hasNpc = groups.Contains("NPC Voices");
        var ini = p.WriteIni ? new FomodFile { Kind = "file", Source = Path.Combine("VBOS INI", IniFileName(p)), Destination = IniFileName(p) } : null;
        IEnumerable<FomodFile> GroupFiles(string g)
        {
            var name = ArchiveName(p, g);
            if (p.Packaging == "loose") yield return new FomodFile { Kind = "folder", Source = Path.Combine(name, "sound"), Destination = "sound" };
            else
            {
                yield return new FomodFile { Kind = "file", Source = Path.Combine(name, name + ".bsa"), Destination = name + ".bsa" };
                yield return new FomodFile { Kind = "file", Source = Path.Combine(name, name + ".esp"), Destination = name + ".esp" };
            }
        }
        foreach (var g in groups.Where(g => g != "NPC Voices"))
        {
            var o = new FomodOption
            {
                Name = g, Mode = DetectMode.Optional,
                Description = $"Books, letters and journals read by {g}." + (hasNpc ? " Also installs the NPC voices: letters and journals written by characters, read in their own voice type." : ""),
            };
            o.Files.AddRange(GroupFiles(g));
            if (hasNpc) o.Files.AddRange(GroupFiles("NPC Voices"));
            if (ini != null) o.Files.Add(ini);
            o.ConditionFlagsXml = new XElement("conditionFlags", new XElement("flag", new XAttribute("name", "VB_" + Flag(g)), "On")).ToString();
            group.Options.Add(o);
        }
        if (hasNpc)
        {
            var o = new FomodOption { Name = "Only NPC Voices", Mode = DetectMode.Optional, Description = "Only the letters and journals written by characters, in their own voice type. For players who use another narrator." };
            o.Files.AddRange(GroupFiles("NPC Voices"));
            if (ini != null) o.Files.Add(ini);
            o.ConditionFlagsXml = new XElement("conditionFlags", new XElement("flag", new XAttribute("name", "VB_NPC"), "On")).ToString();
            group.Options.Add(o);
        }
        group.Options.Add(new FomodOption { Name = "None", Mode = DetectMode.Optional, Description = "Install nothing from this page." });
        step.Groups.Add(group);
        proj.Steps.Add(step);
        // Variant texts: installed automatically when (or unless) their plugin is active, for the narrator that was picked.
        var patterns = new XElement("patterns");
        foreach (var b in books)
            foreach (var v in b.Variants.Where(x => x.Spoken.Trim().Length > 0))
                foreach (var g in groups)
                {
                    if (!results.ContainsKey((g, b.Stem, v.Id))) continue;
                    var flags = g == "NPC Voices"
                        ? groups.Where(x => x != "NPC Voices").Select(x => "VB_" + Flag(x)).Append("VB_NPC").ToList()
                        : new List<string> { "VB_" + Flag(g) };
                    foreach (var flag in flags)
                        patterns.Add(new XElement("pattern",
                            new XElement("dependencies", new XAttribute("operator", "And"),
                                new XElement("flagDependency", new XAttribute("flag", flag), new XAttribute("value", "On")),
                                new XElement("fileDependency", new XAttribute("file", v.Plugin), new XAttribute("state", v.WhenInstalled ? "Active" : "Missing"))),
                            new XElement("files", new XElement("file",
                                new XAttribute("source", Path.Combine(VariantFolder(p, v), g, SoundFolder, b.Stem + ".xwm")),
                                new XAttribute("destination", Path.Combine(SoundFolder, b.Stem + ".xwm")), new XAttribute("priority", 1)))));
                }
        if (patterns.HasElements) proj.ConditionalFileInstallsXml = new XElement("conditionalFileInstalls", patterns).ToString();
        var dir = Path.Combine(p.OutputFolder, "fomod");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "ModuleConfig.xml"), FomodXml.Encode(FomodXml.WriteModuleConfig(proj), proj.Encoding));
        File.WriteAllBytes(Path.Combine(dir, "info.xml"), FomodXml.Encode(FomodXml.WriteInfo(proj), proj.Encoding));
    }

    private static string Flag(string g) => string.Concat(g.Where(char.IsLetterOrDigit));

    private static string Sha(string path) => File.Exists(path) ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) : "(missing)";

    /// <summary>The validation report (IVBOS style): coverage, .ini integrity, archive contents against the .ini, text checks, hashes.</summary>
    public static string Validate(BooksProject p, string projectFolder, BooksExportReport rep, Dictionary<(string Group, string Stem, string Variant), BookJoin> results)
    {
        var sb = new StringBuilder();
        void L(string s = "") => sb.Append(s).Append("\r\n");
        L($"VOICED BOOKS — FULL VALIDATION");
        L($"Project: {p.Name}    Mod: {ModLabel(p)}    Exported: {DateTime.Now:yyyy-MM-dd HH:mm}");
        L($"Made with DBVO Studio (Voiced Books Tool). Rules: IVBOS master specification v1.6.");
        L();
        L("1. SOURCES");
        if (p.Source != "plugins") L($"   Built-in text set: {p.Source} v{p.SetVersion}");
        foreach (var pl in p.Plugins) L($"   {(pl.Target ? "target" : "master")}  {pl.FileName}  SHA-256 {Sha(pl.Path)}");
        L();
        var books = p.Books.Where(b => !b.Gone).ToList();
        L("2. BOOK COVERAGE");
        L($"   Books: {books.Count} ({books.Sum(b => b.Records.Count)} records; {books.Count(b => b.Records.Count > 1)} texts shared by several records)");
        L($"   Left out: {books.Count(b => b.Excluded)}");
        foreach (var g in books.Where(b => b.Excluded).GroupBy(b => b.ExcludeReason.Length > 0 ? b.ExcludeReason : "user")) L($"      {g.Key}: {g.Count()}");
        L($"   Voiced: {rep.Books}    Missing audio: {rep.Missing.Count}");
        foreach (var m in rep.Missing.Take(200)) L($"      MISSING  {m}");
        L();
        L("3. INI INTEGRITY");
        var iniPath = rep.Ini != null ? Path.Combine(p.OutputFolder, rep.Ini) : null;
        var iniPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (iniPath != null && File.Exists(iniPath))
        {
            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int dup = 0, bad = 0;
            foreach (var line in File.ReadAllLines(iniPath))
            {
                if (!line.Contains('~')) continue;
                var parts = line.Split('|');
                if (parts.Length != 2 || !parts[0].StartsWith("0x") || parts[0].Length > 3 + 6 + 1 + 64 || !parts[1].StartsWith(SoundFolder + "\\")) bad++;
                if (!keys.Add(parts[0])) dup++;
                var id = parts[0].Split('~')[0];
                if (id.Length > 8) bad++; // a load-order prefix would make it longer than 0x + 6 digits
                iniPaths[parts[1].ToLowerInvariant()] = parts[0];
            }
            L($"   Lines: {keys.Count}    Duplicate FormID~Plugin keys: {dup}    Malformed: {bad}");
            L($"   FormIDs are file-local (no load-order index); plugin names as in the load order.");
            L($"   SHA-256 {Sha(iniPath)}");
        }
        else L(p.WriteIni ? "   No .ini written (no audio)." : "   .ini switched off for this project (another mod's .ini maps these books).");
        L();
        L("4. ARCHIVES AGAINST THE INI");
        foreach (var g in Groups(p))
        {
            var stems = results.Where(r => r.Key.Group == g.Group && r.Key.Variant.Length == 0).Select(r => r.Key.Stem).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (stems.Count == 0) continue;
            var expected = books.Where(b => !b.Excluded && BooksService.VoicesFor(p, b).Intersect(g.Voices).Any()).Select(b => b.Stem).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var missing = expected.Except(stems).OrderBy(x => x).ToList();
            var orphan = iniPaths.Count == 0 ? new List<string>() : stems.Where(s => !iniPaths.ContainsKey($@"{SoundFolder}\{s}.xwm")).OrderBy(x => x).ToList();
            L($"   {ArchiveName(p, g.Group)}: {stems.Count} files; should have {expected.Count}; missing {missing.Count}; not in the .ini {orphan.Count}");
            foreach (var m in missing.Take(100)) L($"      MISSING  {m}");
            foreach (var o in orphan.Take(100)) L($"      ORPHAN   {o}");
        }
        if (iniPaths.Count > 0)
        {
            var everyStem = results.Keys.Where(k => k.Variant.Length == 0).Select(k => k.Stem).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var silent = iniPaths.Keys.Where(k => !everyStem.Contains(Path.GetFileNameWithoutExtension(k))).ToList();
            L($"   .ini lines without audio in any archive: {silent.Count}");
            foreach (var s in silent.Take(100)) L($"      SILENT   {s}");
        }
        L();
        L("5. AUDIO");
        var joins = results.Values.ToList();
        if (joins.Count > 0)
        {
            L($"   Files: {joins.Count}    Length: {TimeSpan.FromSeconds(joins.Sum(j => j.Seconds)):hh\\:mm\\:ss}    Loudness {joins.Min(j => j.Lufs):F1} to {joins.Max(j => j.Lufs):F1} LUFS    Peak max {joins.Max(j => j.PeakDb):F2} dBFS");
            var loud = joins.Where(j => j.PeakDb > -0.5).ToList();
            if (loud.Count > 0) L($"   {loud.Count} file(s) peak above -0.5 dBFS.");
        }
        L();
        L("6. TEXT CHECKS (flags, not blocks)");
        int serious = 0, notes = 0;
        foreach (var b in books.Where(b => !b.Excluded))
        {
            var issues = BookText.Check(b.InGame, b.Spoken, b.Raw.Length > 0 ? b.Raw : null, b.Title).Where(i => b.InGame.Length > 0 || i.Kind is "markup" or "caps").ToList();
            foreach (var i in issues)
            {
                if (i.Serious) serious++; else notes++;
                if (i.Serious) L($"   {b.Stem}: {i.Message}");
            }
        }
        L($"   Serious: {serious}    Notes: {notes}");
        L();
        L("7. ATTRIBUTION");
        foreach (var g in books.Where(b => !b.Excluded && b.Reader == "npc").GroupBy(b => b.VoiceType).OrderBy(g => g.Key))
            L($"   {g.Key}: {g.Count()} book(s)    {string.Join(", ", g.GroupBy(b => b.AuthorConfirmed ? "CONFIRMED" : b.VoiceTypeCertainty.ToString().ToUpperInvariant()).Select(x => $"{x.Key} {x.Count()}"))}");
        L();
        L("8. HASHES");
        if (rep.Topic != null) L($"   {rep.Topic}  SHA-256 {Sha(Path.Combine(p.OutputFolder, rep.Topic))}");
        foreach (var a in rep.Archives) L($"   {a}  SHA-256 {Sha(Path.Combine(p.OutputFolder, a))}");
        L();
        L(rep.Errors.Count == 0 && rep.Missing.Count == 0 && serious == 0 ? "RESULT: PASS" : $"RESULT: CHECK — {rep.Errors.Count} error(s), {rep.Missing.Count} missing, {serious} serious text flag(s)");
        foreach (var e in rep.Errors.Take(100)) L($"   ERROR  {e}");
        return sb.ToString();
    }
}
