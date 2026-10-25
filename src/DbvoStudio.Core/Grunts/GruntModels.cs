using DbvoStudio.Core.Projects;

namespace DbvoStudio.Core.Grunts;

/// <summary>The player voice mods the Player Grunts Tool exports for.</summary>
public static class GruntTargets
{
    /// <summary>PC Head Tracking and Voice Type SE (with or without Multi Voice Packs support).</summary>
    public const string Pcht = "pcht";
    /// <summary>Smart Player Voice System.</summary>
    public const string Spvs = "spvs";

    public static string Name(string target) => target == Spvs ? "Smart Player Voice System" : "PC Head Tracking";
    public static string Short(string target) => target == Spvs ? "SPVS" : "PCHT";
}

/// <summary>
/// One voiced event of a target mod: a pool of interchangeable lines played at random (most events), or a set of fixed
/// slots that each need their own file (shouts). Definitions are data, not code: the built-in ones come with the app,
/// installed ones are read from the mod itself, custom ones are added by the user.
/// </summary>
public sealed class GruntEventDef
{
    public string Id { get; set; } = "";
    public string Target { get; set; } = GruntTargets.Pcht;
    public string Category { get; set; } = "";
    public string Label { get; set; } = "";
    public string Description { get; set; } = "";
    /// <summary>pool | slots</summary>
    public string Kind { get; set; } = "pool";
    /// <summary>
    /// Pool folder, relative to the target's root: PCHT "Combat\Attack" (under Sound\FX\PC Head Tracking[\prefix]),
    /// SPVS "Magic\Heal" (under Sound\SPVS_AdditionalVoice\CustomVoiceType\&lt;slot&gt;).
    /// </summary>
    public string Folder { get; set; } = "";
    /// <summary>PCHT: editor ID of the sound descriptor (SNDR) that lists the event's files.</summary>
    public string Sndr { get; set; } = "";
    /// <summary>PCHT: how many files the base plugin lists for the event (the pool can be longer or shorter).</summary>
    public int BaseSlots { get; set; }
    /// <summary>SPVS: the Audio Util category key the scripts ask for, e.g. MagicHeal.</summary>
    public string Key { get; set; } = "";
    /// <summary>
    /// SPVS vanilla-based method: fixed dialogue file names (no extension) under Sound\Voice\SmartPlayerVoiceSystem.esp\SPVS_Replace&lt;slot&gt;.
    /// The pool's lines are cycled into these names, the way SPVS's rename tool does.
    /// </summary>
    public List<string> VanillaNames { get; set; } = new();
    /// <summary>Fixed slots (Kind = slots).</summary>
    public List<GruntSlotDef> Slots { get; set; } = new();
    /// <summary>The PCHT folders whose lines suit this event (cross import between PCHT and SPVS projects).</summary>
    public List<string> PchtFolders { get; set; } = new();
    /// <summary>Spoken words (greetings, comments) rather than grunts: subtitles and text-to-speech make sense.</summary>
    public bool Spoken { get; set; }
    /// <summary>built-in | installed | custom</summary>
    public string Origin { get; set; } = "built-in";
    public string Notes { get; set; } = "";
}

/// <summary>A fixed file of a slots event: one line goes into every path listed.</summary>
public sealed class GruntSlotDef
{
    public string Id { get; set; } = "";
    public string Label { get; set; } = "";
    /// <summary>What the line says (shout words), shown as the line's text.</summary>
    public string Text { get; set; } = "";
    /// <summary>
    /// Data-relative paths without extension. Placeholders: {vt} = the voice type folder (PCHT _FemaleBYO, &lt;prefix&gt;_FemaleBYO),
    /// {slot} = the SPVS voice slot (FemaleCustom01). Example: Sound\Voice\PC Head Tracking - MCM.esp\{vt}\_VP_01a_0000A181_1
    /// </summary>
    public List<string> Files { get; set; } = new();
    /// <summary>Optional: only for packs of this gender (female | male), e.g. PCHT's _FemaleBYO folder.</summary>
    public string Gender { get; set; } = "";
    /// <summary>Cross-import key shared by PCHT and SPVS slots that play the same thing (shout form ID + phase).</summary>
    public string Link { get; set; } = "";
}

public sealed class GruntCatalog
{
    public const int CurrentVersion = 1;
    public int Version { get; set; } = CurrentVersion;
    public string Target { get; set; } = "";
    /// <summary>Where the definitions came from: built-in (shipped snapshot) or the installed mod's files.</summary>
    public string Source { get; set; } = "built-in";
    /// <summary>The mod version or the files the definitions were read from, for the UI.</summary>
    public string SourceInfo { get; set; } = "";
    public List<GruntEventDef> Events { get; set; } = new();
}

/// <summary>A user's custom event definitions, stored in the library and shared by all projects of the target.</summary>
public sealed class GruntCustomEvents
{
    public const string Format = "dbvo-studio-player-grunts-events";
    /// <summary>Marks the file as DBVO Studio custom events (also checked on import).</summary>
    public string Kind { get; set; } = Format;
    public int Version { get; set; } = GruntCatalog.CurrentVersion;
    public List<GruntEventDef> Events { get; set; } = new();
}

/// <summary>A Player Grunts project: the audio assigned to one target mod's events, and how to export it.</summary>
public sealed class GruntProject
{
    public string Id { get; set; } = NewId.Short();
    public string Name { get; set; } = "";
    public string Target { get; set; } = GruntTargets.Pcht;
    /// <summary>The DBVO voice pack whose voice the Voice Changer and text-to-speech use (optional).</summary>
    public string VoicePackId { get; set; } = "";
    /// <summary>female | male | both (PCHT MPCHT packs can serve both menus).</summary>
    public string Gender { get; set; } = "female";
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
    public Dictionary<string, GruntEventState> Events { get; set; } = new();
    public PchtExportSettings Pcht { get; set; } = new();
    public SpvsExportSettings Spvs { get; set; } = new();
    public GruntAudioSettings Audio { get; set; } = new();
    public string OutputFolder { get; set; } = "";
    public DateTime? LastExportUtc { get; set; }
    /// <summary>Data-relative paths the last export wrote (so a new export can clean up only its own old files).</summary>
    public List<string> ExportedFiles { get; set; } = new();
    public string LastReport { get; set; } = "";
    /// <summary>Text-to-speech work in progress, by event|slot|line: the texts and every try until one is saved to the event.</summary>
    public Dictionary<string, GruntDraft> Drafts { get; set; } = new();
}

/// <summary>The text-to-speech workspace of one event (or shout part, or line being redone): kept until saved or discarded.</summary>
public sealed class GruntDraft
{
    public string EventId { get; set; } = "";
    public string Slot { get; set; } = "";
    /// <summary>Set when the workspace makes new tries for an existing line (Save replaces its audio).</summary>
    public string ReplaceLineId { get; set; } = "";
    public string Subtitle { get; set; } = "";
    public string Spoken { get; set; } = "";
    public List<GruntTry> Tries { get; set; } = new();
    public string SelectedId { get; set; } = "";
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
}

public sealed class GruntTry
{
    public string Id { get; set; } = NewId.Short();
    /// <summary>Relative to the project folder.</summary>
    public string File { get; set; } = "";
    public string Spoken { get; set; } = "";
    public double Seconds { get; set; }
    /// <summary>Engine, model, stability, speed, seed: shown with the try.</summary>
    public string Settings { get; set; } = "";
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}

public sealed class GruntEventState
{
    public List<GruntLine> Lines { get; set; } = new();
    /// <summary>Left out of the export on purpose (shown as "skipped", not as missing).</summary>
    public bool Skipped { get; set; }
    public string Note { get; set; } = "";
}

/// <summary>One audio file assigned to an event.</summary>
public sealed class GruntLine
{
    public string Id { get; set; } = NewId.Short();
    /// <summary>
    /// The audio: an absolute path to the user's own file (imported files are referenced, not copied), or a path relative to
    /// the project folder for recordings and generated takes ("audio\..."), which the project owns.
    /// </summary>
    public string Source { get; set; } = "";
    /// <summary>import | recording | generated</summary>
    public string Origin { get; set; } = "import";
    /// <summary>Subtitle (SPVS .toml); shout words for slots.</summary>
    public string Text { get; set; } = "";
    /// <summary>Text-to-speech lines: what the voice was given, with [tags] and formatting.</summary>
    public string Spoken { get; set; } = "";
    /// <summary>SPVS script-based events: the file name to export as (no extension). Empty = numbered.</summary>
    public string FileName { get; set; } = "";
    /// <summary>Slots events: the slot this line fills.</summary>
    public string Slot { get; set; } = "";
    public double DurationSeconds { get; set; }
    public int SampleRate { get; set; }
    public int Channels { get; set; }
    public double? Lufs { get; set; }
    public double? PeakDb { get; set; }
    public DateTime AddedUtc { get; set; } = DateTime.UtcNow;
}

public sealed class PchtExportSettings
{
    /// <summary>Multi Voice Packs support (MPCHT): a PCHTV_&lt;prefix&gt;.esp voice set players pick per character.</summary>
    public bool Multi { get; set; }
    /// <summary>MPCHT prefix: letters and digits only (also the sound folder and the voice type names).</summary>
    public string Prefix { get; set; } = "";
    /// <summary>MPCHT: the name shown in the PC Head Tracking MCM.</summary>
    public string FullName { get; set; } = "";
    /// <summary>Standard PCHT: the name of the plugin that lists the extra lines (overrides the base sound descriptors).</summary>
    public string PluginName { get; set; } = "";
    /// <summary>Write the FormList Manipulator .ini an MPCHT voice set needs.</summary>
    public bool WriteFlmIni { get; set; } = true;
    /// <summary>Shouts: also write the Dawnguard and Dragonborn shouts (PC Head Tracking - Patch.esp).</summary>
    public bool PatchShouts { get; set; } = true;
}

public sealed class SpvsExportSettings
{
    /// <summary>FemaleCustom01..05 or MaleCustom01..05.</summary>
    public string Slot { get; set; } = "FemaleCustom01";
    /// <summary>Script-based (Audio Util) folders: every event, any file names and counts, subtitles.</summary>
    public bool ScriptBased { get; set; } = true;
    /// <summary>Vanilla-based (dialogue) fixed file names for attacks, hits, breathing and death.</summary>
    public bool VanillaBased { get; set; } = true;
    /// <summary>Write a .toml subtitle file next to each line that has text.</summary>
    public bool Subtitles { get; set; } = true;
    /// <summary>Subtitle language code: en, ja, zh, fr, de, it, pl, es, ru, or a lowercase language name.</summary>
    public string SubtitleLanguage { get; set; } = "en";
}

public sealed class GruntAudioSettings
{
    /// <summary>wav | xwm | fuz</summary>
    public string Format { get; set; } = "wav";
    /// <summary>Bring every file to the same loudness (EBU R128 integrated) on export.</summary>
    public bool Normalize { get; set; } = true;
    public double TargetLufs { get; set; } = -18;
    /// <summary>Peak ceiling after the gain (dBTP).</summary>
    public double PeakDb { get; set; } = -1.5;
    /// <summary>Extra gain after normalizing, e.g. to sit grunts under dialogue.</summary>
    public double GainDb { get; set; }
    public int SampleRate { get; set; } = 44100;
}
