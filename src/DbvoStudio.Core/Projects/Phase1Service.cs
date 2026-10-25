using System.Text.RegularExpressions;
using DbvoStudio.Core.Plugins;
using DbvoStudio.Core.Rules;
using DbvoStudio.Core.Topics;

namespace DbvoStudio.Core.Projects;

public sealed record MissingMaster(string FileName, string NexusSearchUrl);

public sealed class ImportReport
{
    public required ModPlugin Plugin { get; init; }
    public List<MissingMaster> MissingMasters { get; } = new();
    public int Added { get; set; }
    public int Changed { get; set; }
    public int Removed { get; set; }
    public int Kept { get; set; }
    public int UnresolvedStrings { get; set; }
}

/// <summary>Phase 1: import plugins into a mod, merge updates, write DBVO Fix plugins and topic files.</summary>
public sealed class Phase1Service
{
    private static readonly HashSet<string> BaseGame = new(StringComparer.OrdinalIgnoreCase)
    {
        "Skyrim.esm", "Update.esm", "Dawnguard.esm", "HearthFires.esm", "Dragonborn.esm",
    };

    private readonly Library _lib;
    private readonly AppSettings _settings;

    public Phase1Service(Library lib, AppSettings settings)
    {
        _lib = lib;
        _settings = settings;
    }

    public static string NexusSearch(string pluginName) =>
        "https://www.nexusmods.com/games/skyrimspecialedition/mods?keyword=" +
        Uri.EscapeDataString(Path.GetFileNameWithoutExtension(pluginName));

    /// <summary>Masters the plugin needs that are neither in the base game folder nor uploaded to this mod.</summary>
    public List<MissingMaster> FindMissingMasters(Mod mod, PluginFile plugin)
    {
        var have = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (Directory.Exists(_settings.BaseGameFolder))
            foreach (var f in Directory.EnumerateFiles(_settings.BaseGameFolder)) have.Add(Path.GetFileName(f));
        var mastersDir = Path.Combine(_lib.ModFolder(mod.Id), "masters");
        if (Directory.Exists(mastersDir))
            foreach (var f in Directory.EnumerateFiles(mastersDir)) have.Add(Path.GetFileName(f));
        foreach (var p in mod.Plugins) have.Add(p.FileName);
        return plugin.Masters.Where(m => !have.Contains(m))
            .Select(m => new MissingMaster(m, BaseGame.Contains(m) ? "" : NexusSearch(m))).ToList();
    }

    /// <summary>Copy an uploaded master into the mod's masters folder.</summary>
    public void AddMaster(Mod mod, string path)
    {
        var dir = Path.Combine(_lib.ModFolder(mod.Id), "masters");
        Directory.CreateDirectory(dir);
        File.Copy(path, Path.Combine(dir, Path.GetFileName(path)), true);
    }

    public void DeleteUploadedMasters(Mod mod)
    {
        var dir = Path.Combine(_lib.ModFolder(mod.Id), "masters");
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
    }

    /// <summary>
    /// Import a plugin into a mod. If the mod already has a plugin with this name, this is an update:
    /// lines are matched by FormID and field; unchanged lines keep their edits, changed lines are flagged
    /// with the earlier edit kept for reference, new lines are added, and missing lines are marked removed.
    /// </summary>
    public ImportReport Import(Mod mod, string pluginPath, string? stringsFolder = null)
    {
        var plugin = PluginFile.Read(pluginPath);
        int unresolved = 0;
        if (plugin.IsLocalized)
        {
            if (stringsFolder != null && StringsTable.Exists(stringsFolder, plugin.BaseName, _settings.StringsLanguage))
                plugin.AttachStrings(StringsTable.Load(stringsFolder, plugin.BaseName, _settings.StringsLanguage));
            else if (AttachBestStrings(mod, pluginPath, plugin).Unresolved < 0) unresolved = -1;
        }

        var extraction = PlayerLineExtractor.Extract(plugin);
        unresolved = unresolved < 0 ? Math.Max(1, extraction.UnresolvedStrings) : extraction.UnresolvedStrings;

        var pluginsDir = Path.Combine(_lib.ModFolder(mod.Id), "plugins");
        Directory.CreateDirectory(pluginsDir);
        var stored = Path.Combine(pluginsDir, plugin.FileName);
        if (!string.Equals(Path.GetFullPath(pluginPath), Path.GetFullPath(stored), StringComparison.OrdinalIgnoreCase))
            File.Copy(pluginPath, stored, true);

        var existing = mod.Plugins.FirstOrDefault(p => string.Equals(p.FileName, plugin.FileName, StringComparison.OrdinalIgnoreCase));
        var mp = existing ?? new ModPlugin { FileName = plugin.FileName };
        var oldMasters = mp.Masters.ToList();
        mp.StoredFile = stored;
        mp.Masters = plugin.Masters.ToList();
        mp.Localized = plugin.IsLocalized;
        mp.ImportedUtc = DateTime.UtcNow;
        mp.Notices.Clear();
        mp.HiddenRule = 4;
        mp.StringsRule = 2;

        var report = new ImportReport { Plugin = mp, UnresolvedStrings = unresolved };
        report.MissingMasters.AddRange(FindMissingMasters(mod, plugin));
        if (unresolved != 0)
            mp.Notices.Add($"This plugin is localized and {(unresolved < 0 ? "its strings files were not found" : unresolved + " strings could not be read")}. Add its .STRINGS files.");

        // Every line is listed. Topic texts that look like an EditorID (DA03VileGreetingBranchTopic) start out hidden and
        // unvoiced; ♪ voices them anyway. Prompts no longer hide anything: the plugin can't tell what the game shows.
        var lines = extraction.Lines
            .Select(x => NewLine(x.FormId, x.Field, x.EditorId, x.Original, x.Kind, x.Suggested, IsIdentifierText(x.Field, x.Kind, x.Original), x.Order)).ToList();
        Merge(mod, mp, existing, lines, report, oldMasters);
        return report;
    }

    /// <summary>A Phase 1 row from the script's verdict. Player-name lines get the script's formatting fixes but keep the alias.</summary>
    private static Phase1Line NewLine(uint formId, LineField field, string? edid, string original, LineKind kind, string? suggested, bool hidden, int order)
    {
        var nl = new Phase1Line
        {
            FormId = formId,
            Field = field,
            EditorId = edid,
            Original = original,
            Suggested = suggested,
            Final = kind is LineKind.Valid or LineKind.Fixed ? suggested : null,
            Kind = kind,
            HiddenByPrompts = hidden,
            Order = order,
        };
        if (kind == LineKind.Broken && ScriptRules.HasPlayerAlias(original) && !ScriptRules.HasOtherAlias(original))
        {
            var fixedText = ScriptRules.Fix(ScriptRules.Trim(original));
            nl.Suggested = fixedText;
            nl.Final = fixedText.Length > ScriptRules.MaxLength ? null : fixedText;
        }
        return nl;
    }

    /// <summary>Merge freshly read lines into a plugin's rows: keep decisions on unchanged lines, flag changed ones.</summary>
    private void Merge(Mod mod, ModPlugin mp, ModPlugin? existing, List<Phase1Line> lines, ImportReport report, IReadOnlyList<string>? oldMasters = null)
    {
        // Plugins: match lines by the record's owner and its ID within that owner, not by the raw FormID. The raw
        // FormID starts with the owner's position in the master list, which changes when an update adds, removes
        // or reorders masters. Topic files have no masters (their IDs are hashes) and match by the raw ID.
        bool byOwner = oldMasters != null && !mp.IsTopicFile;
        string KeyOf(Phase1Line l, IReadOnlyList<string> masters) => byOwner ? StableKey(l.FormId, l.Field, masters, mp.FileName) : l.Key;
        var old = mp.Lines.GroupBy(l => KeyOf(l, oldMasters ?? mp.Masters)).ToDictionary(g => g.Key, g => g.First());
        var fresh = new List<Phase1Line>();
        foreach (var nl in lines)
        {
            var k = KeyOf(nl, mp.Masters);
            if (old.TryGetValue(k, out var prev))
            {
                old.Remove(k);
                if (prev.Original == nl.Original)
                {
                    // Unchanged in the mod: keep every user decision.
                    nl.Final = prev.Final;
                    nl.VoiceApproved = prev.VoiceApproved;
                    nl.FixApproved = prev.FixApproved;
                    nl.UserEdited = prev.UserEdited;
                    nl.Reviewed = prev.Reviewed;
                    nl.IncludeAnyway = prev.IncludeAnyway;
                    nl.Excluded = prev.Excluded;
                    nl.Variants = prev.Variants;
                    nl.Status = prev.Status == Phase1Status.ChangedInMod ? Phase1Status.ChangedInMod : Phase1Status.Active;
                    nl.PreviousFinal = prev.PreviousFinal;
                    nl.PreviousOriginal = prev.PreviousOriginal;
                    nl.PlayerNameExported = prev.PlayerNameExported;
                    report.Kept++;
                }
                else
                {
                    nl.Status = Phase1Status.ChangedInMod;
                    nl.PreviousOriginal = prev.Original;
                    nl.PreviousFinal = prev.Final;
                    nl.Variants = prev.Variants;
                    nl.Excluded = prev.Excluded;
                    report.Changed++;
                }
            }
            else if (existing != null)
            {
                report.Added++;
            }
            fresh.Add(nl);
        }
        var taken = fresh.Select(l => l.Key).ToHashSet();
        foreach (var gone in old.Values)
        {
            if (gone.Status != Phase1Status.Removed) report.Removed++;
            gone.Status = Phase1Status.Removed;
            if (byOwner) gone.FormId = Remap(gone.FormId, oldMasters!, mp.Masters, mp.FileName);
            if (!taken.Add(gone.Key)) continue; // its ID now belongs to another record
            fresh.Add(gone);
        }
        mp.Lines = fresh;
        if (existing == null)
        {
            mod.Plugins.Add(mp);
            report.Added = fresh.Count;
        }
        _lib.Save(mod);
    }

    /// <summary>"owner:local-id:field" for a FormID read with the given master list.</summary>
    public static string StableKey(uint formId, LineField field, IReadOnlyList<string> masters, string self)
    {
        int idx = (int)(formId >> 24);
        var owner = idx < masters.Count ? masters[idx] : self;
        return $"{owner.ToLowerInvariant()}:{formId & 0xFFFFFF:X6}:{(field == LineField.TopicText ? "FULL" : "RNAM")}";
    }

    /// <summary>A FormID read with one master list, rewritten for another (unchanged when its owner is no longer a master).</summary>
    public static uint Remap(uint formId, IReadOnlyList<string> from, IReadOnlyList<string> to, string self)
    {
        int idx = (int)(formId >> 24);
        var owner = idx < from.Count ? from[idx] : self;
        int ni = owner.Equals(self, StringComparison.OrdinalIgnoreCase) ? to.Count : -1;
        for (int i = 0; ni < 0 && i < to.Count; i++) if (to[i].Equals(owner, StringComparison.OrdinalIgnoreCase)) ni = i;
        return ni < 0 ? formId : ((uint)ni << 24) | (formId & 0xFFFFFF);
    }

    /// <summary>
    /// Import a topic .txt (for mods without a plugin to read, or collections such as player-name lines).
    /// Every line is checked with the script's rules. Lines are kept as they are by default: they are what the
    /// game shows, and changing one changes its filename. No DBVO Fix plugin can be made from a topic file.
    /// Re-importing a file with the same name updates it (lines are matched by their text).
    /// </summary>
    /// <summary>A topic text that is only an identifier: the EditorID itself, or one word in CamelCase/with digits and no spaces.</summary>
    public static bool IsIdentifierText(LineField field, LineKind kind, string text)
    {
        if (kind == LineKind.SkippedEditorId) return true;
        if (field != LineField.TopicText) return false;
        var t = text.Trim();
        if (t.Length < 8 || !Regex.IsMatch(t, "^[A-Za-z][A-Za-z0-9_]*$")) return false;
        return Regex.IsMatch(t, "[a-z][A-Z]|[A-Za-z][0-9]|_");
    }

    /// <summary>
    /// Brings mods imported before 7.9.2 up to date, once per plugin: only EditorID-like topic texts are hidden (unless you
    /// voiced them), and the ones 7.9.1 took out of the list are read back from the stored plugin copy. Decisions stay.
    /// </summary>
    public bool RecheckHidden(Mod mod)
    {
        bool changed = false;
        foreach (var mp in mod.Plugins.Where(p => p.HiddenRule < 4))
        {
            bool restore = mp.HiddenRule == 3 && !mp.IsTopicFile;
            mp.HiddenRule = 4;
            changed = true;
            foreach (var l in mp.Lines) l.HiddenByPrompts = IsIdentifierText(l.Field, l.Kind, l.Original);
            if (!restore || string.IsNullOrEmpty(mp.StoredFile) || !File.Exists(mp.StoredFile)) continue;
            try
            {
                var plugin = PluginFile.Read(mp.StoredFile);
                if (plugin.IsLocalized && LoadStrings(mod, mp.StoredFile).Table is { } st) plugin.AttachStrings(st);
                var have = mp.Lines.Select(l => (l.FormId, l.Field)).ToHashSet();
                foreach (var x in PlayerLineExtractor.Extract(plugin).Lines)
                    if (IsIdentifierText(x.Field, x.Kind, x.Original) && !have.Contains((x.FormId, x.Field)))
                        mp.Lines.Add(NewLine(x.FormId, x.Field, x.EditorId, x.Original, x.Kind, x.Suggested, true, x.Order));
                mp.Lines = mp.Lines.OrderBy(l => l.Order).ToList();
            }
            catch { /* the lines stay as they are */ }
        }
        return changed;
    }

    public ImportReport ImportTopicFile(Mod mod, string txtPath)
    {
        var raw = TopicFile.Read(txtPath);
        var dir = Path.Combine(_lib.ModFolder(mod.Id), "topics");
        Directory.CreateDirectory(dir);
        var name = Path.GetFileName(txtPath);
        var stored = Path.Combine(dir, name);
        if (!string.Equals(Path.GetFullPath(txtPath), Path.GetFullPath(stored), StringComparison.OrdinalIgnoreCase))
            File.Copy(txtPath, stored, true);

        var existing = mod.Plugins.FirstOrDefault(p => p.IsTopicFile && string.Equals(p.FileName, name, StringComparison.OrdinalIgnoreCase));
        var mp = existing ?? new ModPlugin { FileName = name, IsTopicFile = true };
        mp.StoredFile = stored;
        mp.ImportedUtc = DateTime.UtcNow;
        mp.Notices.Clear();
        var report = new ImportReport { Plugin = mp };

        var seen = new HashSet<string>();
        var lines = new List<Phase1Line>();
        foreach (var text in raw)
        {
            if (!seen.Add(text)) continue;
            uint id = (uint)Naming.XxHash64.Hash(System.Text.Encoding.UTF8.GetBytes(text));
            var r = ScriptRules.Classify(text, null);
            if (r.Kind == LineKind.Empty) continue;
            var nl = NewLine(id, LineField.TopicText, null, text, r.Kind, r.Final, false, lines.Count);
            // Keep the line as written: the topic file mirrors the game's text.
            if (nl.Kind == LineKind.Fixed) nl.Final = text;
            if (nl.IsPlayerName) nl.Final = text;
            lines.Add(nl);
        }
        Merge(mod, mp, existing, lines, report);
        return report;
    }

    /// <summary>
    /// Read an earlier DBVO Fix plugin made for this plugin and take its dialogue texts as the final lines.
    /// FormIDs are matched across the two master lists. Old marker texts (BROKEN LINE, TOO LONG WILL CTD) are ignored.
    /// Returns (applied, ignoredMarkers, unmatched).
    /// </summary>
    public (int Applied, int Markers, int Unmatched) ImportFixPlugin(Mod mod, ModPlugin target, string fixPath)
    {
        if (target.IsTopicFile) throw new InvalidOperationException("A topic file has no DBVO Fix plugin.");
        int applied = 0, markers = 0, unmatched = 0;
        foreach (var (line, text) in FixTexts(target, fixPath))
        {
            if (line == null) { unmatched++; continue; }
            if (text.Contains("BROKEN LINE", StringComparison.Ordinal) || text.Contains("TOO LONG WILL CTD", StringComparison.Ordinal))
            {
                markers++;
                continue;
            }
            if (line.Final == text && line.Reviewed) continue;
            // Remember the line as it was before the first import, so the import can be removed again.
            line.FixImport ??= new FixImportUndo { Final = line.Final, UserEdited = line.UserEdited, Reviewed = line.Reviewed };
            SetFinal(line, text);
            line.FixImport.Imported = line.Final;
            applied++;
        }
        target.ImportedFixPlugin = Path.GetFileName(fixPath);
        _lib.Save(mod);
        return (applied, markers, unmatched);
    }

    /// <summary>The dialogue texts of a DBVO Fix plugin, matched to the target plugin's lines (line is null when no line matches).</summary>
    private static IEnumerable<(Phase1Line? Line, string Text)> FixTexts(ModPlugin target, string fixPath)
    {
        var fix = PluginFile.Read(fixPath);
        var source = PluginFile.Read(target.StoredFile, new HashSet<string>());
        var byKey = target.Lines.ToDictionary(l => l.Key);
        foreach (var rec in fix.Records)
        {
            var sub = rec.Signature == "DIAL" ? rec.Get("FULL") : rec.Get("RNAM");
            if (sub == null) continue;
            var text = fix.DecodeLString(sub.Data);
            if (text == null) continue;
            uint? local = MapFormId(rec.FormId, fix, source);
            if (local == null) { yield return (null, text); continue; }
            var key = new Phase1Line { FormId = local.Value, Field = rec.Signature == "DIAL" ? LineField.TopicText : LineField.Prompt }.Key;
            yield return (byKey.TryGetValue(key, out var line) ? line : null, text);
        }
    }

    /// <summary>
    /// Remove an imported DBVO Fix plugin: lines it changed get their earlier text back. Lines edited by hand after the
    /// import keep the hand edit. Imports made before the earlier text was kept (7.3 and older) need the fix plugin
    /// file again (fixPath): its lines that still read as the plugin wrote them go back to the script's version.
    /// Returns (restored, keptEdits).
    /// </summary>
    public (int Restored, int Kept) RemoveFixPlugin(Mod mod, ModPlugin target, string? fixPath = null)
    {
        int restored = 0, kept = 0;
        foreach (var line in target.Lines.Where(l => l.FixImport != null))
        {
            var u = line.FixImport!;
            if (line.Final == u.Imported) { line.Final = u.Final; line.UserEdited = u.UserEdited; line.Reviewed = u.Reviewed; restored++; }
            else kept++;
            line.FixImport = null;
        }
        if (!string.IsNullOrWhiteSpace(fixPath))
            foreach (var (line, text) in FixTexts(target, fixPath))
            {
                if (line == null || text.Contains("BROKEN LINE", StringComparison.Ordinal) || text.Contains("TOO LONG WILL CTD", StringComparison.Ordinal)) continue;
                // As the script left it before the import: its suggestion for valid and fixed lines, empty for broken ones.
                var script = line.Kind is LineKind.Valid or LineKind.Fixed ? line.Suggested : null;
                if (line.Final == script) continue;                       // the import changed nothing here
                if (line.Final != Rules.ScriptRules.Detofu(text.Trim())) { kept++; continue; }   // edited by hand since
                line.Final = script; line.UserEdited = false; line.Reviewed = false;
                restored++;
            }
        target.ImportedFixPlugin = null;
        _lib.Save(mod);
        return (restored, kept);
    }

    /// <summary>Translate a FormID from one plugin's master list to another's (by owning file name).</summary>
    public static uint? MapFormId(uint formId, PluginFile from, PluginFile to)
    {
        string owner = from.OwnerOf(formId);
        int index;
        if (string.Equals(owner, to.FileName, StringComparison.OrdinalIgnoreCase)) index = to.Masters.Count;
        else
        {
            index = to.Masters.ToList().FindIndex(m => string.Equals(m, owner, StringComparison.OrdinalIgnoreCase));
            if (index < 0) return null;
        }
        return ((uint)index << 24) | (formId & 0x00FFFFFF);
    }

    /// <summary>Player-name lines (with &lt;Alias=Player&gt;) of a plugin, as they would appear in a player-name topic file.</summary>
    public static List<string> PlayerNameLines(ModPlugin p) =>
        p.Lines.Where(l => l.Status != Phase1Status.Removed && l.IsPlayerName)
            .Select(l => (l.Final ?? l.Original).Trim()).Distinct().ToList();

    /// <summary>
    /// The strings the game itself shows for a plugin. A plugin the game's Data folder has (Skyrim.esm, the DLCs, Creation Club)
    /// reads them where the game does: loose Data\Strings first, then the game's archives ("Skyrim - Interface.bsa", the plugin's
    /// own .bsa). Strings files kept with the mod or next to the plugin come only after that, so a text overhaul's or an old
    /// version's strings can't replace the game's wording (the DBVO file names must match what the game shows).
    /// </summary>
    public (StringsTable? Table, string Source) LoadStrings(Mod mod, string pluginPath)
    {
        foreach (var (load, src) in StringsCandidates(mod, pluginPath))
            if (load() is { } t) return (t, src);
        return (null, "");
    }

    /// <summary>
    /// Every strings source for a plugin, in the order the game would use them: for a plugin the game's Data folder has,
    /// loose Data\Strings (loaded last, so it wins), then the game's archives; then the strings next to the plugin, kept with
    /// the mod, and the Global Settings strings folder.
    /// </summary>
    private IEnumerable<(Func<StringsTable?> Load, string Source)> StringsCandidates(Mod mod, string pluginPath)
    {
        var name = Path.GetFileNameWithoutExtension(pluginPath);
        var lang = _settings.StringsLanguage;
        var data = Revoice.RevoiceService.DataFolder(_settings.BaseGameFolder);
        if (data != null && File.Exists(Path.Combine(data, Path.GetFileName(pluginPath))))
        {
            var loose = Path.Combine(data, "Strings");
            if (StringsTable.Exists(loose, name, lang)) yield return (() => StringsTable.Load(loose, name, lang), loose);
            foreach (var bsa in new[] { name + ".bsa", name + " - Main.bsa", "Skyrim - Interface.bsa" })
            {
                var path = Path.Combine(data, bsa);
                if (!File.Exists(path)) continue;
                yield return (() => { try { using var a = Archives.BsaArchive.Open(path); return StringsTable.FromArchive(a, name, lang); } catch { return null; } }, path);
            }
        }
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in new[] { Path.Combine(Path.GetDirectoryName(Path.GetFullPath(pluginPath))!, "Strings"), Path.Combine(_lib.ModFolder(mod.Id), "strings"), _settings.StringsFolder })
            if (!string.IsNullOrEmpty(c) && seen.Add(Path.GetFullPath(c)) && StringsTable.Exists(c, name, lang))
                yield return (() => StringsTable.Load(c, name, lang), c);
    }

    /// <summary>
    /// Attaches the strings the game shows. A strings file that doesn't fit the plugin (an old or mismatched copy: some of the
    /// plugin's texts aren't in it) is passed over for the next source that has them all, or the one missing the fewest.
    /// </summary>
    public (string Source, int Unresolved) AttachBestStrings(Mod mod, string pluginPath, PluginFile plugin)
    {
        (StringsTable T, string S, int U)? best = null;
        foreach (var (load, src) in StringsCandidates(mod, pluginPath))
        {
            var t = load();
            if (t == null) continue;
            plugin.AttachStrings(t);
            int u = PlayerLineExtractor.Extract(plugin).UnresolvedStrings;
            if (u == 0) return (src, 0);
            if (best == null || u < best.Value.U) best = (t, src, u);
        }
        if (best is { } b) { plugin.AttachStrings(b.T); return (b.S, b.U); }
        return ("", -1);
    }

    /// <summary>
    /// Once per plugin (mods imported before 8.1.3): reads the texts again from the strings the game shows. Lines whose words
    /// differ (a text overhaul's strings were used) take the game's words; lines you edited keep your edit for reference and
    /// show as changed in the mod. Returns how many lines changed, or -1 when nothing was checked.
    /// </summary>
    public int RecheckStrings(Mod mod)
    {
        int changed = -1;
        foreach (var mp in mod.Plugins.Where(p => p.StringsRule < 2 && p.Localized && !p.IsTopicFile))
        {
            if (string.IsNullOrEmpty(mp.StoredFile) || !File.Exists(mp.StoredFile)) continue;
            try
            {
                if (!StringsCandidates(mod, mp.StoredFile).Any()) continue;
                var plugin = PluginFile.Read(mp.StoredFile);
                var (source, _) = AttachBestStrings(mod, mp.StoredFile, plugin);
                if (source.Length == 0) continue;
                mp.StringsRule = 2;
                changed = Math.Max(changed, 0);
                var fresh = PlayerLineExtractor.Extract(plugin).Lines.GroupBy(x => (x.FormId, x.Field)).ToDictionary(g => g.Key, g => g.First());
                int n = 0;
                foreach (var l in mp.Lines.Where(l => l.Status != Phase1Status.Removed))
                {
                    if (!fresh.TryGetValue((l.FormId, l.Field), out var x) || x.Original == l.Original) continue;
                    var nl = NewLine(x.FormId, x.Field, x.EditorId, x.Original, x.Kind, x.Suggested, l.HiddenByPrompts, l.Order);
                    if (l.UserEdited)
                    {
                        l.PreviousOriginal = l.Original;
                        l.PreviousFinal = l.Final;
                        l.Status = Phase1Status.ChangedInMod;
                        l.UserEdited = false;
                    }
                    l.Original = nl.Original;
                    l.Suggested = nl.Suggested;
                    l.Final = nl.Final;
                    l.Kind = nl.Kind;
                    n++;
                }
                if (n > 0) mp.Notices.Add($"{n} line(s) now use the game's own wording ({Path.GetFileName(source)}); the strings files used before had other words.");
                changed += n;
            }
            catch { /* the lines stay as they are */ }
        }
        return changed;
    }

    /// <summary>A line whose words differ between the game's own strings and another strings file (a text overhaul, an older copy).</summary>
    public sealed record StringsDifference(string Key, uint FormId, string Field, string Game, string Other, string? Final, bool Edited);

    /// <summary>
    /// Compares the plugin's player lines in the strings the game shows with the strings kept with the mod (or next to the
    /// plugin, or the Global Settings strings folder). Returns the differences and where the other strings came from.
    /// </summary>
    public (List<StringsDifference> Diffs, string GameSource, string OtherSource) StringsDiff(Mod mod, ModPlugin mp)
    {
        if (!mp.Localized || mp.IsTopicFile || !File.Exists(mp.StoredFile)) return (new(), "", "");
        var (game, gameSrc) = LoadStrings(mod, mp.StoredFile);
        var other = ResolveStringsFolder(mod, mp.StoredFile);
        if (game == null || other == null || string.Equals(Path.GetFullPath(other), Path.GetFullPath(gameSrc), StringComparison.OrdinalIgnoreCase)) return (new(), gameSrc, other ?? "");
        var plugin = PluginFile.Read(mp.StoredFile);
        plugin.AttachStrings(game);
        var a = PlayerLineExtractor.Extract(plugin).Lines.GroupBy(x => (x.FormId, x.Field)).ToDictionary(g => g.Key, g => g.First().Original);
        plugin.AttachStrings(StringsTable.Load(other, plugin.BaseName, _settings.StringsLanguage));
        var lines = mp.Lines.Where(l => l.Status != Phase1Status.Removed).GroupBy(l => (l.FormId, l.Field)).ToDictionary(g => g.Key, g => g.First());
        var list = new List<StringsDifference>();
        foreach (var x in PlayerLineExtractor.Extract(plugin).Lines)
        {
            if (!a.TryGetValue((x.FormId, x.Field), out var g) || g == x.Original || !lines.TryGetValue((x.FormId, x.Field), out var l)) continue;
            list.Add(new StringsDifference(l.Key, x.FormId, x.Field.ToString(), g, x.Original, l.Final, l.UserEdited));
        }
        return (list, gameSrc, other);
    }

    private string? ResolveStringsFolder(Mod mod, string pluginPath)
    {
        var candidates = new[]
        {
            Path.Combine(Path.GetDirectoryName(Path.GetFullPath(pluginPath))!, "Strings"),
            Path.Combine(_lib.ModFolder(mod.Id), "strings"),
            _settings.StringsFolder,
        };
        var name = Path.GetFileNameWithoutExtension(pluginPath);
        return candidates.FirstOrDefault(c => !string.IsNullOrEmpty(c) && StringsTable.Exists(c, name, _settings.StringsLanguage));
    }

    /// <summary>Apply a user edit: the final in-game text for a row.</summary>
    public static void SetFinal(Phase1Line line, string? text)
    {
        line.Final = string.IsNullOrWhiteSpace(text) ? null : Rules.ScriptRules.Detofu(text.Trim());
        line.UserEdited = line.Final != line.Suggested;
        line.Reviewed = true;
        if (line.Status == Phase1Status.ChangedInMod) line.Status = Phase1Status.Active;
    }

    /// <summary>Undo back to the original text from the plugin.</summary>
    public static void RevertToOriginal(Phase1Line line)
    {
        line.Final = line.Original == null ? null : Rules.ScriptRules.Detofu(line.Original);
        line.UserEdited = line.Final != line.Suggested;
        line.Reviewed = true;
    }

    public static void AcceptSuggestion(Phase1Line line)
    {
        line.Final = line.Suggested;
        line.UserEdited = false;
        line.Reviewed = true;
    }

    /// <summary>Flagged rows (BROKEN / TOO LONG) the user has never opened: shown as a warning before Phase 2.</summary>
    public static List<Phase1Line> UnreviewedFlags(ModPlugin p) =>
        p.Lines.Where(l => l.Status != Phase1Status.Removed && l.IsFlagged && !l.Reviewed && !l.Excluded).ToList();

    /// <summary>Topic lines for one plugin, de-duplicated ignoring case.</summary>
    public static List<string> TopicLines(ModPlugin p)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return p.Lines.OrderBy(l => l.Order).SelectMany(l => l.TopicLines()).Where(seen.Add).ToList();
    }

    /// <summary>The edits for the DBVO Fix plugin. Unresolved flagged lines get no override.</summary>
    public static List<LineEdit> FixEdits(ModPlugin p) =>
        p.IsTopicFile ? new List<LineEdit>()
        : p.Lines.Where(l => l.NeedsOverride)
            .Select(l => new LineEdit(l.FormId, l.Field, l.Final!)).ToList();

    /// <summary>File name of the topic file written for a plugin (a topic-file source keeps its own name).</summary>
    public static string TopicFileName(ModPlugin p) =>
        p.IsTopicFile ? p.FileName : $"TopicList{Path.GetFileNameWithoutExtension(p.FileName)}.txt";

    /// <summary>
    /// Write the DBVO Fix plugin (only when needed) and the topic file for one plugin into the given folders.
    /// Returns the files written.
    /// </summary>
    /// <summary>DBVO Fix plugins of this mod that already exist in a folder.</summary>
    public static List<string> ExistingFixPlugins(Mod mod, string folder) =>
        mod.Plugins.Where(p => p.NeedsFixPlugin).Select(p => Path.Combine(folder, p.FixFileName)).Where(File.Exists).ToList();

    /// <param name="removeStaleFix">
    /// When the plugin no longer needs a DBVO Fix plugin (every edit was undone), delete an earlier one in these
    /// folders, so the old edits don't stay in the game (not with <paramref name="keepExistingFix"/>).
    /// </param>
    public List<string> WriteOutputs(Mod mod, ModPlugin p, IEnumerable<string> fixFolders, string topicFolder, bool keepExistingFix = false, bool removeStaleFix = false)
    {
        var written = new List<string>();
        var topicPath = Path.Combine(topicFolder, TopicFileName(p));
        TopicFile.Write(topicPath, TopicLines(p));
        written.Add(topicPath);

        var edits = p.IsTopicFile ? new List<LineEdit>() : FixEdits(p);
        if (edits.Count == 0)
        {
            if (removeStaleFix && !keepExistingFix && !p.IsTopicFile)
                foreach (var folder in fixFolders.Where(f => !string.IsNullOrWhiteSpace(f)).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    var stale = Path.Combine(folder, p.FixFileName);
                    if (!File.Exists(stale)) continue;
                    try { File.Delete(stale); written.Add("Removed (no edits left): " + stale); } catch { }
                }
            return written;
        }
        var plugin = PluginFile.Read(p.StoredFile);
        if (plugin.IsLocalized)
        {
            if (LoadStrings(mod, p.StoredFile).Table is { } st) plugin.AttachStrings(st);
        }
        var bytes = FixPluginWriter.Build(plugin, edits);
        foreach (var folder in fixFolders.Where(f => !string.IsNullOrWhiteSpace(f)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, p.FixFileName);
            if (keepExistingFix && File.Exists(path)) continue;
            File.WriteAllBytes(path, bytes);
            written.Add(path);
        }
        return written;
    }
}
