using DbvoStudio.Core.Projects;

namespace DbvoStudio.Core.Fomod;

/// <summary>A file or folder an option installs (ModConfig 5.0 fileSystemItem).</summary>
public sealed class FomodFile
{
    /// <summary>"file" or "folder".</summary>
    public string Kind { get; set; } = "folder";
    /// <summary>Relative to the FOMOD root (or absolute: copied into the package when building).</summary>
    public string Source { get; set; } = "";
    public string Destination { get; set; } = "";
    public int Priority { get; set; }
    public bool? AlwaysInstall { get; set; }
    public bool? InstallIfUsable { get; set; }
    /// <summary>Filled in from a DBVO Patch (its Sound folder or a DBVO Fix plugin): replaced when the option is refreshed.</summary>
    public bool Linked { get; set; }
}

/// <summary>
/// How an option is offered. The simple modes cover what DBVO packs need; anything else imported from another
/// tool is kept as Advanced and written back unchanged.
/// </summary>
public static class DetectMode
{
    /// <summary>Always installed (type Required).</summary>
    public const string Required = "Required";
    /// <summary>Pre-ticked when the mod's plugins are active, unavailable (NotUsable) when they are not.</summary>
    public const string Detect = "Detect";
    /// <summary>Always pre-ticked (type Recommended).</summary>
    public const string Recommended = "Recommended";
    /// <summary>Not ticked; the user decides (type Optional).</summary>
    public const string Optional = "Optional";
    /// <summary>No detection, for CC or untestable content (written as Optional).</summary>
    public const string Manual = "Manual";
    /// <summary>Kept exactly as imported (any typeDescriptor).</summary>
    public const string Advanced = "Advanced";
}

/// <summary>One option (a "plugin" in FOMOD terms) inside a group.</summary>
public sealed class FomodOption
{
    public string Id { get; set; } = NewId.Short();
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    /// <summary>Relative image path inside the package (e.g. Images\Header.png), or absolute (copied when building).</summary>
    public string Image { get; set; } = "";
    public List<FomodFile> Files { get; set; } = new();
    public string Mode { get; set; } = DetectMode.Detect;
    /// <summary>Plugins detected for <see cref="DetectMode.Detect"/>.</summary>
    public List<string> Plugins { get; set; } = new();
    /// <summary>Detect: true = all plugins must be active; false = any one is enough.</summary>
    public bool RequireAll { get; set; }
    /// <summary>Detect: also mark the option NotUsable when the plugins are missing (the usual DBVO setup).</summary>
    public bool NotUsableWhenMissing { get; set; } = true;
    /// <summary>Raw &lt;typeDescriptor&gt; XML for <see cref="DetectMode.Advanced"/>.</summary>
    public string? TypeDescriptorXml { get; set; }
    /// <summary>Raw &lt;conditionFlags&gt; XML (flags set by this option), kept as imported.</summary>
    public string? ConditionFlagsXml { get; set; }
    /// <summary>True when the imported plugin listed conditionFlags before files (both orders are valid).</summary>
    public bool FlagsFirst { get; set; }
    /// <summary>When linked: the DBVO Patch and mod whose .fuz files and DBVO Fix plugins this option carries.</summary>
    public string? PatchId { get; set; }
    public string? ModId { get; set; }
    /// <summary>Carries the DBVO voice pack JSON (exactly one option should).</summary>
    public bool CarriesJson { get; set; }
}

public sealed class FomodGroup
{
    public string Id { get; set; } = NewId.Short();
    public string Name { get; set; } = "";
    /// <summary>SelectAny, SelectAll, SelectExactlyOne, SelectAtMostOne, SelectAtLeastOne.</summary>
    public string Type { get; set; } = "SelectAny";
    public string Order { get; set; } = "Explicit";
    public List<FomodOption> Options { get; set; } = new();
}

public sealed class FomodStep
{
    public string Id { get; set; } = NewId.Short();
    public string Name { get; set; } = "";
    /// <summary>Raw &lt;visible&gt; XML (step visibility conditions), kept as imported.</summary>
    public string? VisibleXml { get; set; }
    public string GroupsOrder { get; set; } = "Explicit";
    public List<FomodGroup> Groups { get; set; } = new();
}

/// <summary>fomod\info.xml</summary>
public sealed class FomodInfo
{
    public string Name { get; set; } = "";
    public string Author { get; set; } = "";
    public string Version { get; set; } = "";
    public string Website { get; set; } = "";
    public string Description { get; set; } = "";
    public List<string> Groups { get; set; } = new() { "Audio - Voice" };
    /// <summary>Other info.xml elements (e.g. Id), kept as imported.</summary>
    public List<string> ExtraXml { get; set; } = new();
}

/// <summary>The DBVO voice pack JSON the FOMOD installs.</summary>
public sealed class DbvoJsonSettings
{
    public bool Enabled { get; set; } = true;
    public string VoicePackName { get; set; } = "";
    public string VoicePackId { get; set; } = "";
}

/// <summary>A FOMOD installer project (Phase 3).</summary>
public sealed class FomodProject
{
    public string Id { get; set; } = NewId.Short();
    public string Name { get; set; } = "";
    public string VoicePackId { get; set; } = "";
    /// <summary>Folder the relative sources are resolved from (the folder that holds the patch folders, Images, …).</summary>
    public string RootFolder { get; set; } = "";
    /// <summary>Where the packaged archive goes.</summary>
    public string ReleaseFolder { get; set; } = "";
    /// <summary>"zip" (built in) or "7z" (uses 7-Zip from Settings).</summary>
    public string ArchiveFormat { get; set; } = "zip";
    /// <summary>"utf-16" (FOMOD Creation Tool style) or "utf-8".</summary>
    public string Encoding { get; set; } = "utf-16";
    public FomodInfo Info { get; set; } = new();
    public string ModuleName { get; set; } = "";
    public string ModuleImage { get; set; } = "";
    /// <summary>Raw &lt;moduleName&gt; attributes such as position and colour, kept as imported.</summary>
    public Dictionary<string, string> ModuleNameAttributes { get; set; } = new();
    /// <summary>Raw &lt;moduleDependencies&gt; XML, kept as imported.</summary>
    public string? ModuleDependenciesXml { get; set; }
    public List<FomodFile> RequiredInstallFiles { get; set; } = new();
    public string StepsOrder { get; set; } = "Explicit";
    public List<FomodStep> Steps { get; set; } = new();
    /// <summary>Raw &lt;conditionalFileInstalls&gt; XML, kept as imported.</summary>
    public string? ConditionalFileInstallsXml { get; set; }
    public DbvoJsonSettings Json { get; set; } = new();
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime? LastBuildUtc { get; set; }
    public string? LastArchive { get; set; }

    public IEnumerable<FomodOption> AllOptions() => Steps.SelectMany(s => s.Groups).SelectMany(g => g.Options);
}
