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
        stringsFolder ??= ResolveStringsFolder(mod, pluginPath);
        int unresolved = 0;
        if (plugin.IsLocalized && !plugin.AttachStrings(stringsFolder, _settings.StringsLanguage))
            unresolved = -1;

        var extraction = PlayerLineExtractor.Extract(plugin);
        unresolved = unresolved < 0 ? Math.Max(1, extraction.UnresolvedStrings) : extraction.UnresolvedStrings;

        var pluginsDir = Path.Combine(_lib.ModFolder(mod.Id), "plugins");
        Directory.CreateDirectory(pluginsDir);
        var stored = Path.Combine(pluginsDir, plugin.FileName);
        if (!string.Equals(Path.GetFullPath(pluginPath), Path.GetFullPath(stored), StringComparison.OrdinalIgnoreCase))
            File.Copy(pluginPath, stored, true);

        var existing = mod.Plugins.FirstOrDefault(p => string.Equals(p.FileName, plugin.FileName, StringComparison.OrdinalIgnoreCase));
        var mp = existing ?? new ModPlugin { FileName = plugin.FileName };
        mp.StoredFile = stored;
        mp.Masters = plugin.Masters.ToList();
        mp.Localized = plugin.IsLocalized;
        mp.ImportedUtc = DateTime.UtcNow;
        mp.Notices.Clear();

        var report = new ImportReport { Plugin = mp, UnresolvedStrings = unresolved };
        report.MissingMasters.AddRange(FindMissingMasters(mod, plugin));
        if (unresolved != 0)
            mp.Notices.Add($"This plugin is localized and {(unresolved < 0 ? "its strings files were not found" : unresolved + " strings could not be read")}. Add its .STRINGS files.");

        var lines = extraction.Lines.Select(x => NewLine(x.FormId, x.Field, x.EditorId, x.Original, x.Kind, x.Suggested, x.HiddenByPrompts, x.Order)).ToList();
        Merge(mod, mp, existing, lines, report);
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
    private void Merge(Mod mod, ModPlugin mp, ModPlugin? existing, List<Phase1Line> lines, ImportReport report)
    {
        var old = mp.Lines.GroupBy(l => l.Key).ToDictionary(g => g.Key, g => g.First());
        var fresh = new List<Phase1Line>();
        foreach (var nl in lines)
        {
            if (old.TryGetValue(nl.Key, out var prev))
            {
                old.Remove(nl.Key);
                if (prev.Original == nl.Original)
                {
                    // Unchanged in the mod: keep every user decision.
                    nl.Final = prev.Final;
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
        foreach (var gone in old.Values)
        {
            if (gone.Status != Phase1Status.Removed) report.Removed++;
            gone.Status = Phase1Status.Removed;
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

    /// <summary>
    /// Import a topic .txt (for mods without a plugin to read, or collections such as player-name lines).
    /// Every line is checked with the script's rules. Lines are kept as they are by default: they are what the
    /// game shows, and changing one changes its filename. No DBVO Fix plugin can be made from a topic file.
    /// Re-importing a file with the same name updates it (lines are matched by their text).
    /// </summary>
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
        var fix = PluginFile.Read(fixPath);
        var source = PluginFile.Read(target.StoredFile, new HashSet<string>());
        int applied = 0, markers = 0, unmatched = 0;
        var byKey = target.Lines.ToDictionary(l => l.Key);
        foreach (var rec in fix.Records)
        {
            var sub = rec.Signature == "DIAL" ? rec.Get("FULL") : rec.Get("RNAM");
            if (sub == null) continue;
            var text = fix.DecodeLString(sub.Data);
            if (text == null) continue;
            uint? local = MapFormId(rec.FormId, fix, source);
            if (local == null) { unmatched++; continue; }
            var key = new Phase1Line { FormId = local.Value, Field = rec.Signature == "DIAL" ? LineField.TopicText : LineField.Prompt }.Key;
            if (!byKey.TryGetValue(key, out var line)) { unmatched++; continue; }
            if (text.Contains("BROKEN LINE", StringComparison.Ordinal) || text.Contains("TOO LONG WILL CTD", StringComparison.Ordinal))
            {
                markers++;
                continue;
            }
            if (line.Final == text && line.Reviewed) continue;
            SetFinal(line, text);
            applied++;
        }
        target.ImportedFixPlugin = Path.GetFileName(fixPath);
        _lib.Save(mod);
        return (applied, markers, unmatched);
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
        line.Final = string.IsNullOrWhiteSpace(text) ? null : text.Trim();
        line.UserEdited = line.Final != line.Suggested;
        line.Reviewed = true;
        if (line.Status == Phase1Status.ChangedInMod) line.Status = Phase1Status.Active;
    }

    /// <summary>Undo back to the original text from the plugin.</summary>
    public static void RevertToOriginal(Phase1Line line)
    {
        line.Final = line.Original;
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
    public List<string> WriteOutputs(Mod mod, ModPlugin p, IEnumerable<string> fixFolders, string topicFolder)
    {
        var written = new List<string>();
        var topicPath = Path.Combine(topicFolder, TopicFileName(p));
        TopicFile.Write(topicPath, TopicLines(p));
        written.Add(topicPath);

        var edits = FixEdits(p);
        if (edits.Count == 0) return written;
        var plugin = PluginFile.Read(p.StoredFile);
        if (plugin.IsLocalized)
        {
            var sf = ResolveStringsFolder(mod, p.StoredFile);
            plugin.AttachStrings(sf, _settings.StringsLanguage);
        }
        var bytes = FixPluginWriter.Build(plugin, edits);
        foreach (var folder in fixFolders.Where(f => !string.IsNullOrWhiteSpace(f)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, p.FixFileName);
            File.WriteAllBytes(path, bytes);
            written.Add(path);
        }
        return written;
    }
}
