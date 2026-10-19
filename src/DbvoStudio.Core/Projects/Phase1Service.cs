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

        var old = mp.Lines.ToDictionary(l => l.Key);
        var fresh = new List<Phase1Line>();
        foreach (var x in extraction.Lines)
        {
            var nl = new Phase1Line
            {
                FormId = x.FormId,
                Field = x.Field,
                EditorId = x.EditorId,
                Original = x.Original,
                Suggested = x.Suggested,
                Final = x.Kind is LineKind.Valid or LineKind.Fixed ? x.Suggested : null,
                Kind = x.Kind,
                HiddenByPrompts = x.HiddenByPrompts,
                Order = x.Order,
            };
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
            gone.Status = Phase1Status.Removed;
            fresh.Add(gone);
            report.Removed++;
        }
        mp.Lines = fresh;
        if (existing == null)
        {
            mod.Plugins.Add(mp);
            report.Added = fresh.Count;
        }
        _lib.Save(mod);
        return report;
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
        p.Lines.Where(l => l.NeedsOverride && !(l.IsFlagged && !l.UserEdited))
            .Select(l => new LineEdit(l.FormId, l.Field, l.Final!)).ToList();

    /// <summary>
    /// Write the DBVO Fix plugin (only when needed) and the topic file for one plugin into the given folders.
    /// Returns the files written.
    /// </summary>
    public List<string> WriteOutputs(Mod mod, ModPlugin p, IEnumerable<string> fixFolders, string topicFolder)
    {
        var written = new List<string>();
        var topicPath = Path.Combine(topicFolder, $"TopicList{Path.GetFileNameWithoutExtension(p.FileName)}.txt");
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
