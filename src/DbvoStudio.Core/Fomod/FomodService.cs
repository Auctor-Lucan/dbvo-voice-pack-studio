using System.IO.Compression;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Schema;
using DbvoStudio.Core.Naming;
using DbvoStudio.Core.Projects;
using DbvoStudio.Core.Tools;

namespace DbvoStudio.Core.Fomod;

public sealed class FomodIssue
{
    public string Level { get; set; } = "error"; // error | warning
    public string Where { get; set; } = "";
    public string Message { get; set; } = "";
    public string? OptionId { get; set; }
}

public sealed class SimOption
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Type { get; set; } = "Optional";
    public bool Selected { get; set; }
    public bool Locked { get; set; }
}

public sealed class SimGroup
{
    public string Name { get; set; } = "";
    public string Type { get; set; } = "";
    public List<SimOption> Options { get; set; } = new();
}

public sealed class SimStep
{
    public string Name { get; set; } = "";
    public bool Visible { get; set; } = true;
    public List<SimGroup> Groups { get; set; } = new();
}

public sealed class SimFile
{
    public string Destination { get; set; } = "";
    public string Source { get; set; } = "";
    public string Option { get; set; } = "";
    public bool Exists { get; set; }
    public int FileCount { get; set; }
}

public sealed class SimulationResult
{
    public List<SimStep> Steps { get; set; } = new();
    public List<SimFile> Files { get; set; } = new();
    public List<string> Conflicts { get; set; } = new();
    public List<string> Flags { get; set; } = new();
}

public sealed class BuildResult
{
    public string Staging { get; set; } = "";
    public string? Archive { get; set; }
    public int Files { get; set; }
    public int Linked { get; set; }
    public List<string> Warnings { get; set; } = new();
}

/// <summary>Phase 3: options from DBVO Patches, the DBVO JSON, validation, simulated install, staging and packaging.</summary>
public sealed class FomodService
{
    private readonly Library _lib;
    private readonly AppSettings _settings;

    public FomodService(Library lib, AppSettings settings)
    {
        _lib = lib;
        _settings = settings;
    }

    // ------------------------------------------------------------------ paths

    /// <summary>FOMOD paths use backslashes; turn them into this system's separator for file access.</summary>
    public static string Os(string path) => Path.DirectorySeparatorChar == '\\' ? path : path.Replace('\\', Path.DirectorySeparatorChar);

    public string Resolve(FomodProject p, string source) =>
        Path.IsPathRooted(Os(source)) ? Os(source) : Path.Combine(Os(p.RootFolder), Os(source));

    /// <summary>Relative to the FOMOD root when the path is inside it; otherwise absolute (copied in when packaging).</summary>
    public static string MakeRelative(FomodProject p, string absolute)
    {
        if (string.IsNullOrWhiteSpace(p.RootFolder)) return absolute;
        var rel = Path.GetRelativePath(p.RootFolder, absolute);
        return rel.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(rel) ? absolute : rel;
    }

    /// <summary>The DBVO JSON location inside the game's Data folder (Legacy vs DBVO 2.0).</summary>
    public static string JsonDestination(NamingMode mode, string packId) =>
        (mode == NamingMode.Dbvo2Hash ? @"SKSE\Plugins\DBVO2\voice_packs\" : @"DragonbornVoiceOver\voice_packs\")
            + packId.ToLowerInvariant() + "_voice_pack.json";

    public static string JsonText(DbvoJsonSettings j)
    {
        // The standard voice pack file: just the name shown in DBVO's menu and the ID (the Sound\DBVO\ folder).
        var o = new Dictionary<string, object> { ["voice_pack_name"] = j.VoicePackName, ["voice_pack_id"] = j.VoicePackId };
        return JsonSerializer.Serialize(o, new JsonSerializerOptions { WriteIndented = true });
    }

    // ------------------------------------------------------------------ options from DBVO Patches

    /// <summary>The folder of one mod inside a patch's final folder: the part of the .fuz template above Sound\.</summary>
    public static string ModContentFolder(DbvoPatch patch, VoicePack voice, Mod mod)
    {
        var tpl = Phase2Service.ResolveFuzTemplate(patch, voice);
        var expanded = Phase2Service.ExpandTemplate(tpl, mod.Name, voice, patch).Replace('/', '\\');
        int i = expanded.IndexOf(@"Sound\", StringComparison.OrdinalIgnoreCase);
        var prefix = i < 0 ? "" : expanded[..i].TrimEnd('\\');
        return string.IsNullOrEmpty(prefix) ? patch.FinalRoot : Path.Combine(patch.FinalRoot, prefix);
    }

    /// <summary>Files an option for (patch, mod) installs: its Sound folder and its DBVO Fix plugins.</summary>
    public List<FomodFile> FilesFor(FomodProject p, DbvoPatch patch, VoicePack voice, Mod mod)
    {
        var folder = ModContentFolder(patch, voice, mod);
        var files = new List<FomodFile>
        {
            new() { Kind = "folder", Source = MakeRelative(p, Path.Combine(folder, "Sound")), Destination = "Sound", Linked = true },
        };
        foreach (var plugin in mod.Plugins.Where(x => x.NeedsFixPlugin))
            files.Add(new FomodFile { Kind = "file", Source = MakeRelative(p, Path.Combine(folder, plugin.FixFileName)), Destination = plugin.FixFileName, Linked = true });
        return files;
    }

    /// <summary>
    /// Replace the files an option got from its DBVO Patch with fresh ones, keeping files the user added.
    /// Files from before the Linked flag are recognised by their destination: Sound, or a DBVO Fix plugin name.
    /// </summary>
    public static void Relink(FomodOption o, List<FomodFile> fresh, Mod mod)
    {
        bool IsFix(string dest) =>
            dest.EndsWith(".esp", StringComparison.OrdinalIgnoreCase)
            && (dest.StartsWith(FixPluginWriterPrefix, StringComparison.OrdinalIgnoreCase)
                || mod.Plugins.Any(pl => dest.Equals(pl.FixFileName, StringComparison.OrdinalIgnoreCase)));
        o.Files.RemoveAll(f => f.Linked
                               || (f.Kind == "folder" && f.Destination.Equals("Sound", StringComparison.OrdinalIgnoreCase) && o.PatchId != null)
                               || (f.Kind == "file" && IsFix(f.Destination)));
        o.Files.InsertRange(0, fresh.Where(f => !o.Files.Any(x => x.Destination.Equals(f.Destination, StringComparison.OrdinalIgnoreCase))));
    }

    /// <summary>A new option for one mod of a DBVO Patch, detecting the mod's own plugins.</summary>
    public FomodOption OptionFor(FomodProject p, DbvoPatch patch, VoicePack voice, Mod mod)
    {
        var plugins = mod.Plugins.Where(x => !x.IsTopicFile).Select(x => x.FileName).ToList();
        int lines = patch.Lines.Count(l => !l.IsOmitted && !l.Excluded && l.Sources.Any(s => s.ModId == mod.Id));
        return new FomodOption
        {
            Name = mod.Name,
            Description = $"{voice.Name} DBVO patch for {mod.Name}{(string.IsNullOrWhiteSpace(mod.Version) ? "" : " " + mod.Version)}: {lines} voiced lines."
                          + (string.IsNullOrWhiteSpace(mod.NexusUrl) ? "" : "\r\n\r\n" + mod.NexusUrl),
            Files = FilesFor(p, patch, voice, mod),
            Mode = plugins.Count > 0 ? DetectMode.Detect : DetectMode.Manual,
            Plugins = plugins,
            PatchId = patch.Id,
            ModId = mod.Id,
        };
    }

    /// <summary>
    /// Refresh the files of every linked option from its DBVO Patch (folders may have changed). Returns the options
    /// whose patch was deleted or no longer has their mod: they are left as they are.
    /// </summary>
    public List<string> RefreshLinked(FomodProject p, IReadOnlyList<DbvoPatch> patches, IReadOnlyList<VoicePack> voices, IReadOnlyList<Mod> mods)
    {
        var orphans = new List<string>();
        foreach (var o in p.AllOptions().Where(o => o.PatchId != null && o.ModId != null))
        {
            var patch = patches.FirstOrDefault(x => x.Id == o.PatchId);
            var mod = mods.FirstOrDefault(x => x.Id == o.ModId);
            var voice = patch == null ? null : voices.FirstOrDefault(v => v.Id == patch.VoicePackId);
            if (patch == null || mod == null || voice == null || !patch.ModIds.Contains(mod.Id)) { orphans.Add(o.Name); continue; }
            Relink(o, FilesFor(p, patch, voice, mod), mod);
        }
        return orphans;
    }

    private const string FixPluginWriterPrefix = "DBVO Fix - ";

    /// <summary>A new, empty project laid out like 2.7.0: a required core option carrying the DBVO JSON, then a page per category.</summary>
    public static FomodProject NewProject(VoicePack voice, string name)
    {
        var p = new FomodProject
        {
            Name = name,
            VoicePackId = voice.Id,
            ModuleName = name,
            Info = new FomodInfo { Name = name, Author = voice.Author },
            Json = new DbvoJsonSettings
            {
                VoicePackName = voice.Name, VoicePackId = voice.PackId,
            },
        };
        // "DBVO Vampira" + " DBVO Voice Pack" would say DBVO twice
        var packTitle = voice.Name.Contains("DBVO", StringComparison.OrdinalIgnoreCase) ? $"{voice.Name} Voice Pack" : $"{voice.Name} DBVO Voice Pack";
        var core = new FomodOption
        {
            Name = $"{packTitle} (Required)",
            Description = "Always installed: the DBVO voice pack file that makes this voice selectable in DBVO's MCM.",
            Mode = DetectMode.Required,
            CarriesJson = true,
        };
        p.Steps.Add(new FomodStep { Name = "Core", Groups = { new FomodGroup { Name = packTitle, Type = "SelectAll", Options = { core } } } });
        return p;
    }

    /// <summary>The group for a category, creating "&lt;category&gt;" step and group when missing.</summary>
    public static FomodGroup GroupFor(FomodProject p, string category)
    {
        var step = p.Steps.FirstOrDefault(s => s.Name.Equals(category, StringComparison.OrdinalIgnoreCase));
        if (step == null)
        {
            step = new FomodStep { Name = category };
            p.Steps.Add(step);
        }
        var g = step.Groups.FirstOrDefault();
        if (g == null)
        {
            g = new FomodGroup { Name = category + " (A-Z)", Type = "SelectAny" };
            step.Groups.Add(g);
        }
        return g;
    }

    // ------------------------------------------------------------------ validation

    private static XmlSchemaSet? _schema;

    private static XmlSchemaSet Schema()
    {
        if (_schema != null) return _schema;
        var set = new XmlSchemaSet();
        using var s = typeof(FomodService).Assembly.GetManifestResourceStream("DbvoStudio.Core.Fomod.ModuleConfig.xsd")
                      ?? throw new InvalidOperationException("ModuleConfig.xsd missing from the build.");
        using var r = XmlReader.Create(s);
        set.Add(null, r);
        set.Compile();
        return _schema = set;
    }

    public List<FomodIssue> Validate(FomodProject p)
    {
        var issues = new List<FomodIssue>();
        void Add(string level, string where, string msg, string? opt = null) => issues.Add(new FomodIssue { Level = level, Where = where, Message = msg, OptionId = opt });

        // Schema
        try
        {
            var xml = FomodXml.WriteModuleConfig(p);
            var doc = XDocument.Parse(System.Text.RegularExpressions.Regex.Replace(xml, @"^\s*<\?xml[^>]*\?>", ""));
            doc.Validate(Schema(), (_, e) => Add(e.Severity == XmlSeverityType.Error ? "error" : "warning", "ModuleConfig.xml", "Schema: " + e.Message));
        }
        catch (Exception ex) { Add("error", "ModuleConfig.xml", "Could not be written: " + ex.Message); }

        if (string.IsNullOrWhiteSpace(p.RootFolder) || !Directory.Exists(p.RootFolder)) Add("error", "Project", "Set the FOMOD root folder (the folder that holds the patch folders).");
        if (!string.IsNullOrWhiteSpace(p.ReleaseFolder) && !Path.IsPathRooted(p.ReleaseFolder)) Add("error", "Project", "The release folder must be a full path, such as D:\\DBVO\\Releases.");
        if (string.IsNullOrWhiteSpace(p.Info.Name)) Add("warning", "info.xml", "No mod name.");
        if (string.IsNullOrWhiteSpace(p.Info.Version)) Add("warning", "info.xml", "No version.");
        if (p.Json.Enabled)
        {
            if (string.IsNullOrWhiteSpace(p.Json.VoicePackId)) Add("error", "DBVO JSON", "No voice_pack_id: the voice pack would be invisible to DBVO.");
            int carriers = p.AllOptions().Count(o => o.CarriesJson);
            if (carriers == 0) Add("error", "DBVO JSON", "No option carries the DBVO voice pack JSON. Tick \"Carries the DBVO JSON\" on a required option.");
            if (carriers > 1) Add("warning", "DBVO JSON", $"{carriers} options carry the DBVO JSON; one is enough.");
            var c = p.AllOptions().FirstOrDefault(o => o.CarriesJson);
            if (c != null && c.Mode != DetectMode.Required) Add("warning", c.Name, "The option carrying the DBVO JSON is not Required, so a user could skip it.", c.Id);
        }
        CheckImage(p, p.ModuleImage, "Header image", null, issues);
        foreach (var step in p.Steps)
        {
            if (step.Groups.All(g => g.Options.Count == 0)) Add("warning", step.Name, "Empty page: it is left out of the installer.");
            foreach (var g in step.Groups)
            {
                if (g.Options.Count == 0) continue;
                var dup = g.Options.GroupBy(o => o.Name, StringComparer.OrdinalIgnoreCase).Where(x => x.Count() > 1).Select(x => x.Key).ToList();
                foreach (var d in dup) Add("warning", $"{step.Name} › {g.Name}", $"Two options are called \"{d}\".");
                if (g.Type == "SelectExactlyOne" && g.Options.All(o => o.Mode is DetectMode.Detect))
                    Add("warning", $"{step.Name} › {g.Name}", "Select exactly one: if none of the detected mods is installed, the user can't continue.");
                foreach (var o in g.Options)
                {
                    var where = $"{step.Name} › {g.Name} › {o.Name}";
                    if (string.IsNullOrWhiteSpace(o.Name)) Add("error", where, "Option without a name.", o.Id);
                    if (o.Mode == DetectMode.Detect && o.Plugins.Count == 0) Add("warning", where, "Detect is set but no plugins are listed; it acts as Optional.", o.Id);
                    CheckImage(p, o.Image, where, o.Id, issues);
                    foreach (var f in o.Files)
                    {
                        var abs = Resolve(p, f.Source);
                        bool exists = f.Kind == "folder" ? Directory.Exists(abs) : File.Exists(abs);
                        if (!exists) Add(o.PatchId != null ? "warning" : "error", where, $"Missing {f.Kind}: {f.Source}" + (o.PatchId != null ? " (build the .fuz files in Phase 2 first)" : ""), o.Id);
                        if (string.IsNullOrWhiteSpace(f.Destination) && f.Kind == "file") Add("warning", where, $"No destination for {f.Source}.", o.Id);
                    }
                }
            }
        }
        var patches = _lib.LoadPatches();
        var allMods = _lib.LoadMods();
        foreach (var o in p.AllOptions().Where(o => o.PatchId != null && o.ModId != null))
        {
            var patch = patches.FirstOrDefault(x => x.Id == o.PatchId);
            if (patch == null) Add("warning", o.Name, "Its DBVO Patch was deleted: the option is no longer updated. Remove it, or keep it as a plain option.", o.Id);
            else if (!patch.ModIds.Contains(o.ModId!) || !allMods.Any(m => m.Id == o.ModId))
                Add("warning", o.Name, $"Its mod is no longer in the DBVO Patch \"{patch.Name}\": the option is no longer updated.", o.Id);
        }
        foreach (var f in p.RequiredInstallFiles)
        {
            var abs = Resolve(p, f.Source);
            if (!(f.Kind == "folder" ? Directory.Exists(abs) : File.Exists(abs))) Add("error", "Required files", $"Missing {f.Kind}: {f.Source}");
        }
        return issues;
    }

    private void CheckImage(FomodProject p, string image, string where, string? opt, List<FomodIssue> issues)
    {
        if (string.IsNullOrWhiteSpace(image)) return;
        if (!File.Exists(Resolve(p, image))) issues.Add(new FomodIssue { Level = "warning", Where = where, Message = $"Image not found: {image}", OptionId = opt });
    }

    // ------------------------------------------------------------------ simulated install

    /// <summary>
    /// Walk the installer the way Mod Organizer / Vortex do with the given active plugins: compute each option's
    /// type, pre-select Required and Recommended, apply flags in step order and list the files that would install.
    /// </summary>
    public SimulationResult Simulate(FomodProject p, IEnumerable<string> activePlugins, IReadOnlyDictionary<string, bool>? choices = null)
    {
        var active = activePlugins.Select(x => x.Trim()).Where(x => x.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var flags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var result = new SimulationResult();
        var install = new List<(FomodFile f, string option)>();
        install.AddRange(p.RequiredInstallFiles.Select(f => (f, "Required files")));

        foreach (var step in p.Steps.Where(s => s.Groups.Any(g => g.Options.Count > 0)))
        {
            var ss = new SimStep { Name = step.Name };
            if (!string.IsNullOrWhiteSpace(step.VisibleXml)) ss.Visible = Eval(XElement.Parse(step.VisibleXml), active, flags);
            result.Steps.Add(ss);
            if (!ss.Visible) continue;
            foreach (var g in step.Groups.Where(g => g.Options.Count > 0))
            {
                var sg = new SimGroup { Name = g.Name, Type = g.Type };
                ss.Groups.Add(sg);
                foreach (var o in g.Options)
                {
                    var type = TypeOf(o, active, flags);
                    var so = new SimOption { Id = o.Id, Name = o.Name, Type = type, Locked = type is "Required" or "NotUsable" };
                    so.Selected = type is "Required" or "Recommended";
                    if (choices != null && choices.TryGetValue(o.Id, out var pick) && !so.Locked) so.Selected = pick;
                    if (type == "NotUsable") so.Selected = false;
                    if (g.Type == "SelectAll") { so.Selected = type != "NotUsable"; so.Locked = true; }
                    sg.Options.Add(so);
                }
                if (g.Type is "SelectExactlyOne" or "SelectAtMostOne")
                {
                    var sel = sg.Options.Where(x => x.Selected).ToList();
                    foreach (var extra in sel.Skip(1)) extra.Selected = false;
                    if (g.Type == "SelectExactlyOne" && sel.Count == 0)
                    {
                        var first = sg.Options.FirstOrDefault(x => x.Type != "NotUsable");
                        if (first != null) first.Selected = true;
                    }
                }
                if (g.Type == "SelectAtLeastOne" && !sg.Options.Any(x => x.Selected))
                {
                    var first = sg.Options.FirstOrDefault(x => x.Type != "NotUsable");
                    if (first != null) first.Selected = true;
                }
                foreach (var so in sg.Options.Where(x => x.Selected))
                {
                    var o = g.Options.First(x => x.Id == so.Id);
                    install.AddRange(o.Files.Select(f => (f, o.Name)));
                    if (o.CarriesJson && p.Json.Enabled) result.Flags.Add($"DBVO JSON installed by \"{o.Name}\"");
                    if (!string.IsNullOrWhiteSpace(o.ConditionFlagsXml))
                        foreach (var fl in XElement.Parse(o.ConditionFlagsXml).Elements().Where(e => e.Name.LocalName == "flag"))
                            flags[fl.Attribute("name")?.Value ?? ""] = fl.Value;
                }
            }
        }
        if (!string.IsNullOrWhiteSpace(p.ConditionalFileInstallsXml))
        {
            foreach (var pat in XElement.Parse(p.ConditionalFileInstallsXml).Descendants().Where(e => e.Name.LocalName == "pattern"))
            {
                var deps = pat.Elements().FirstOrDefault(e => e.Name.LocalName == "dependencies");
                var files = pat.Elements().FirstOrDefault(e => e.Name.LocalName == "files");
                if (deps != null && files != null && Eval(deps, active, flags))
                    install.AddRange(files.Elements().Where(e => e.Name.LocalName is "file" or "folder").Select(e => (new FomodFile
                    {
                        Kind = e.Name.LocalName, Source = e.Attribute("source")?.Value ?? "", Destination = e.Attribute("destination")?.Value ?? "",
                    }, "Conditional install")));
            }
        }
        result.Flags.AddRange(flags.Select(kv => $"flag {kv.Key} = {kv.Value}"));

        var byDest = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (f, option) in install)
        {
            var abs = Resolve(p, f.Source);
            bool exists = f.Kind == "folder" ? Directory.Exists(abs) : File.Exists(abs);
            int count = 0;
            if (exists && f.Kind == "folder")
            {
                foreach (var file in Directory.EnumerateFiles(abs, "*", SearchOption.AllDirectories))
                {
                    count++;
                    var dest = Path.Combine(f.Destination, Path.GetRelativePath(abs, file)).Replace('/', '\\');
                    (byDest.TryGetValue(dest, out var l) ? l : byDest[dest] = new()).Add(option);
                }
            }
            else if (exists)
            {
                count = 1;
                var dest = string.IsNullOrWhiteSpace(f.Destination) ? Path.GetFileName(f.Source) : f.Destination;
                (byDest.TryGetValue(dest, out var l) ? l : byDest[dest] = new()).Add(option);
            }
            result.Files.Add(new SimFile { Source = f.Source, Destination = f.Destination, Option = option, Exists = exists, FileCount = count });
        }
        foreach (var kv in byDest.Where(kv => kv.Value.Distinct().Count() > 1).Take(200))
            result.Conflicts.Add($"{kv.Key}: {string.Join(", ", kv.Value.Distinct())}");
        return result;
    }

    private static string TypeOf(FomodOption o, HashSet<string> active, Dictionary<string, string> flags)
    {
        var td = FomodXml.TypeDescriptor(o);
        var type = td.Elements().FirstOrDefault(e => e.Name.LocalName == "type");
        if (type != null) return type.Attribute("name")?.Value ?? "Optional";
        var dt = td.Elements().FirstOrDefault(e => e.Name.LocalName == "dependencyType");
        if (dt == null) return "Optional";
        var patterns = dt.Elements().FirstOrDefault(e => e.Name.LocalName == "patterns");
        if (patterns != null)
        {
            foreach (var pat in patterns.Elements().Where(e => e.Name.LocalName == "pattern"))
            {
                var deps = pat.Elements().FirstOrDefault(e => e.Name.LocalName == "dependencies");
                var t = pat.Elements().FirstOrDefault(e => e.Name.LocalName == "type");
                if (deps != null && t != null && Eval(deps, active, flags)) return t.Attribute("name")?.Value ?? "Optional";
            }
        }
        return dt.Elements().FirstOrDefault(e => e.Name.LocalName == "defaultType")?.Attribute("name")?.Value ?? "Optional";
    }

    /// <summary>Evaluate a compositeDependency: fileDependency, flagDependency, nested dependencies, And/Or.</summary>
    private static bool Eval(XElement deps, HashSet<string> active, Dictionary<string, string> flags)
    {
        bool or = string.Equals(deps.Attribute("operator")?.Value, "Or", StringComparison.OrdinalIgnoreCase);
        var results = new List<bool>();
        foreach (var e in deps.Elements())
        {
            switch (e.Name.LocalName)
            {
                case "fileDependency":
                {
                    var file = e.Attribute("file")?.Value ?? "";
                    var state = e.Attribute("state")?.Value ?? "Active";
                    bool on = active.Contains(file);
                    results.Add(state switch { "Active" => on, "Inactive" => !on, "Missing" => !on, _ => on });
                    break;
                }
                case "flagDependency":
                {
                    var name = e.Attribute("flag")?.Value ?? "";
                    var value = e.Attribute("value")?.Value ?? "";
                    results.Add(flags.TryGetValue(name, out var v) ? v == value : value.Length == 0);
                    break;
                }
                case "dependencies": results.Add(Eval(e, active, flags)); break;
                case "gameDependency":
                case "fommDependency": results.Add(true); break;
            }
        }
        if (results.Count == 0) return true;
        return or ? results.Any(x => x) : results.All(x => x);
    }

    // ------------------------------------------------------------------ build and package

    /// <summary>
    /// Stage the installer (fomod\ files, every option's files hardlinked, images, the DBVO JSON) and pack it.
    /// Sources outside the FOMOD root are copied in under _external\&lt;option&gt;\.
    /// </summary>
    public async Task<BuildResult> BuildAsync(FomodProject p, NamingMode naming, bool package, IProgress<GenerationProgress>? progress = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(p.ReleaseFolder)) throw new InvalidOperationException("Set the release folder first.");
        if (!Path.IsPathRooted(p.ReleaseFolder)) throw new InvalidOperationException("The release folder must be a full path, such as D:\\DBVO\\Releases.");
        var res = new BuildResult();
        var archiveBase = SafeName($"{(string.IsNullOrWhiteSpace(p.Info.Name) ? p.Name : p.Info.Name)}{(string.IsNullOrWhiteSpace(p.Info.Version) ? "" : " " + p.Info.Version)}");
        var staging = Path.Combine(p.ReleaseFolder, "_staging", archiveBase);
        if (Directory.Exists(staging)) Directory.Delete(staging, true);
        Directory.CreateDirectory(Path.Combine(staging, "fomod"));
        res.Staging = staging;

        var mapped = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var jobs = new List<(string from, string to, bool folder)>();
        string Map(string source, string owner, bool folder)
        {
            if (mapped.TryGetValue(source + "|" + folder, out var m)) return m;
            string rel = Path.IsPathRooted(source) ? "_external\\" + SafeName(owner) + "\\" + Path.GetFileName(Os(source).TrimEnd(Path.DirectorySeparatorChar)) : source;
            mapped[source + "|" + folder] = rel;
            jobs.Add((Resolve(p, source), Path.Combine(staging, Os(rel)), folder));
            return rel;
        }

        // Images
        string MapImage(string img) => Map(img, "Images", false);
        // Option files: map every source, remembering which option owns it for _external names
        var owners = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var o in p.AllOptions()) foreach (var f in o.Files) owners.TryAdd(f.Source, o.Name);
        foreach (var f in p.RequiredInstallFiles) owners.TryAdd(f.Source, "Required");
        string MapSource(string s)
        {
            var o = p.AllOptions().SelectMany(x => x.Files).Concat(p.RequiredInstallFiles).FirstOrDefault(f => f.Source == s);
            return Map(s, owners.TryGetValue(s, out var n) ? n : "files", o?.Kind == "folder");
        }

        // DBVO JSON: written into _common\ and installed by the carrier option.
        var withJson = Clone(p);
        if (p.Json.Enabled)
        {
            var dest = JsonDestination(naming, p.Json.VoicePackId);
            var rel = "_common\\" + dest;
            var target = Path.Combine(staging, Os(rel));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await File.WriteAllTextAsync(target, JsonText(p.Json), new System.Text.UTF8Encoding(false), ct);
            var carrier = withJson.AllOptions().FirstOrDefault(o => o.CarriesJson);
            if (carrier == null) res.Warnings.Add("No option carries the DBVO JSON; it was not added to the installer.");
            else
            {
                var top = dest.Split('\\')[0];
                carrier.Files.Insert(0, new FomodFile { Kind = "folder", Source = "_common\\" + top, Destination = top });
                mapped["_common\\" + top + "|True"] = "_common\\" + top;
            }
        }

        var moduleConfig = FomodXml.WriteModuleConfig(withJson, s => mapped.TryGetValue(s + "|True", out var hit) ? hit : MapSource(s), MapImage);
        var info = FomodXml.WriteInfo(withJson);
        await File.WriteAllBytesAsync(Path.Combine(staging, "fomod", "ModuleConfig.xml"), FomodXml.Encode(moduleConfig, p.Encoding), ct);
        await File.WriteAllBytesAsync(Path.Combine(staging, "fomod", "info.xml"), FomodXml.Encode(info, p.Encoding), ct);

        // Copy (hardlink) everything. First list every file, so progress counts files, not options.
        // Lists are taken before copying, and nothing inside the release folder is ever picked up
        // (a release folder inside the FOMOD root would otherwise be copied into itself).
        // Only the build's own output is skipped: the _staging folder and archives being written (the release
        // folder may well be the FOMOD root itself).
        var stagingRoot = Path.GetFullPath(Path.Combine(p.ReleaseFolder, "_staging")).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var ownArchives = new[] { ".zip", ".7z" }.Select(x => Path.GetFullPath(Path.Combine(p.ReleaseFolder, archiveBase + x))).ToHashSet(StringComparer.OrdinalIgnoreCase);
        bool InRelease(string f)
        {
            var full = Path.GetFullPath(f);
            return full.StartsWith(stagingRoot, StringComparison.OrdinalIgnoreCase) || full.EndsWith(".part", StringComparison.OrdinalIgnoreCase) || ownArchives.Contains(full);
        }
        var pairs = new List<(string from, string to)>();
        foreach (var (from, to, folder) in jobs)
        {
            ct.ThrowIfCancellationRequested();
            if (folder || Directory.Exists(from))
            {
                if (!Directory.Exists(from)) { res.Warnings.Add($"Missing folder skipped: {from}"); continue; }
                foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories).ToList())
                    if (!InRelease(file)) pairs.Add((file, Path.Combine(to, Path.GetRelativePath(from, file))));
            }
            else if (!File.Exists(from)) res.Warnings.Add($"Missing file skipped: {from}");
            else pairs.Add((from, to));
        }
        int done = 0, copies = 0;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (from, to) in pairs)
        {
            ct.ThrowIfCancellationRequested();
            if (seen.Add(to) && !File.Exists(to))
            {
                if (FileLinks.LinkOrCopy(from, to, true)) res.Linked++; else copies++;
                res.Files++;
            }
            if (++done % 25 == 0 || done == pairs.Count)
                progress?.Report(new GenerationProgress(done, pairs.Count, 0, Path.GetFileName(from), "Staging files"));
        }
        if (copies > 0 && res.Linked == 0 && copies > 50)
            res.Warnings.Add($"{copies} files were copied instead of hardlinked (the release folder is on another drive than the sources?). The build is correct, just slower and larger on disk.");

        if (package)
        {
            Directory.CreateDirectory(p.ReleaseFolder);
            var all = Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories).ToList();
            if (p.ArchiveFormat == "7z")
            {
                var exe = _settings.SevenZipPath;
                if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe)) throw new InvalidOperationException("Set 7z.exe in Settings to build .7z archives, or choose .zip.");
                var archive = Path.Combine(p.ReleaseFolder, archiveBase + ".7z");
                if (File.Exists(archive)) File.Delete(archive);
                progress?.Report(new GenerationProgress(0, all.Count, 0, null, "Packing with 7-Zip (no progress shown by 7-Zip)"));
                var r = await ExternalTools.RunAsync(exe, new[] { "a", "-t7z", "-mx=7", "-mmt=on", "-y", archive, Path.Combine(staging, "*") }, ct, TimeSpan.FromHours(6));
                if (r.ExitCode != 0 || !File.Exists(archive)) throw new InvalidOperationException("7-Zip failed: " + r.StdErr + r.StdOut);
                res.Archive = archive;
            }
            else
            {
                var archive = Path.Combine(p.ReleaseFolder, archiveBase + ".zip");
                var tmp = archive + ".part";
                if (File.Exists(tmp)) File.Delete(tmp);
                try
                {
                    await Task.Run(() =>
                    {
                        using var zip = ZipFile.Open(tmp, ZipArchiveMode.Create);
                        int n = 0;
                        foreach (var file in all)
                        {
                            ct.ThrowIfCancellationRequested();
                            var ext = Path.GetExtension(file).ToLowerInvariant();
                            // .fuz / audio / images barely shrink: store them, compress only text and plugins.
                            var level = ext is ".fuz" or ".xwm" or ".wav" or ".lip" or ".png" or ".jpg" or ".jpeg" or ".dds" or ".bsa" ? CompressionLevel.NoCompression : CompressionLevel.Fastest;
                            zip.CreateEntryFromFile(file, Path.GetRelativePath(staging, file).Replace('\\', '/'), level);
                            if (++n % 50 == 0 || n == all.Count)
                                progress?.Report(new GenerationProgress(n, all.Count, 0, Path.GetFileName(file), "Packing .zip"));
                        }
                    }, ct);
                }
                catch
                {
                    try { File.Delete(tmp); } catch { }
                    throw;
                }
                if (File.Exists(archive)) File.Delete(archive);
                File.Move(tmp, archive);
                res.Archive = archive;
            }
            p.LastArchive = res.Archive;
        }
        p.LastBuildUtc = DateTime.UtcNow;
        return res;
    }

    private static FomodProject Clone(FomodProject p) =>
        JsonSerializer.Deserialize<FomodProject>(JsonSerializer.Serialize(p))!;

    public static string SafeName(string s) =>
        string.Concat(s.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim().TrimEnd('.');
}
