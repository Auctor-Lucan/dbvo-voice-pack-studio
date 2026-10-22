using System.Text.Json.Serialization;
using DbvoStudio.Core.Projects;
using DbvoStudio.Core.Naming;

namespace DbvoStudio.Core.Revoice;

/// <summary>A plugin in the project's load order.</summary>
public sealed class RevoicePlugin
{
    public string Path { get; set; } = "";
    public string FileName { get; set; } = "";
    /// <summary>Lines from this plugin are revoiced (otherwise it is only read for masters, NPCs and conditions).</summary>
    public bool Target { get; set; }
    /// <summary>Added from the base game folder.</summary>
    public bool BaseGame { get; set; }
}

/// <summary>Which voice a speaker gets.</summary>
public sealed class SpeakerAssignment
{
    /// <summary>elevenlabs | voxcpm | omnivoice. With VoiceId set, the speaker uses this voice with the project's voice settings.</summary>
    public string? Engine { get; set; }
    /// <summary>ElevenLabs voice id, or the local server's reference voice name.</summary>
    public string? VoiceId { get; set; }
    public string? VoiceName { get; set; }
    /// <summary>Older projects: a whole voice pack (used when no VoiceId is set).</summary>
    public string? VoicePackId { get; set; }
    /// <summary>Leave this speaker's lines out of generation and export.</summary>
    public bool Skip { get; set; }
}

/// <summary>Optional: a new voice type for one NPC, set up the Aela Revoiced way (SkyPatcher + FormList Manipulator, no NPC override).</summary>
public sealed class NewVoiceTypePlan
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string EditorId { get; set; } = "";
    /// <summary>The NPC's original voice type (FormKey text).</summary>
    public string BaseVoiceType { get; set; } = "";
    /// <summary>NPC records that get the new voice type through SkyPatcher (all copies of the character).</summary>
    public List<string> Npcs { get; set; } = new();
    /// <summary>Form lists the new voice type is added to with FormList Manipulator (FormKey text).</summary>
    public List<string> Lists { get; set; } = new();
    /// <summary>Lines of the original voice type the NPC can say: "all" (safe superset) or "npc" (drop lines tied to other NPCs).</summary>
    public string LineSet { get; set; } = "npc";
    /// <summary>Extra SkyPatcher NPC entries typed by the user for plugins that are not loaded ("BadMoon.esp|6603B").</summary>
    public List<string> ExtraNpcs { get; set; } = new();
}

/// <summary>One voice file to make: a response of a line, for one voice type.</summary>
public sealed class RevoiceLine : Phase2Line
{
    public string Info { get; set; } = "";
    public int Response { get; set; }
    public string PluginFolder { get; set; } = "";
    public string VoiceType { get; set; } = "";
    public string VoiceTypeKey { get; set; } = "";
    public string SpeakerKey { get; set; } = "";
    public string SpeakerName { get; set; } = "";
    public string Npcs { get; set; } = "";
    public string Certainty { get; set; } = "";
    public string Via { get; set; } = "";
    public int Emotion { get; set; }
    public int EmotionValue { get; set; }
    public string Notes { get; set; } = "";
    public string Quest { get; set; } = "";
    public string Topic { get; set; } = "";
    public string Plugin { get; set; } = "";
    /// <summary>Where the game's current file for this line is (display text), if there is one.</summary>
    public string? Original { get; set; }
    /// <summary>Belongs to a new voice type plan (files go to the new voice type's folders).</summary>
    public string? PlanId { get; set; }
    /// <summary>Approved link to an orphaned file that is this line's current audio (merged or compacted plugins): source id + path inside it.</summary>
    public string? OriginalSourceId { get; set; }
    public string? OriginalInner { get; set; }
    /// <summary>How the link was made: "same quest and topic" or "Whisper 0.93".</summary>
    public string? OriginalMatch { get; set; }
    /// <summary>No longer found on the last scan (kept because it has takes).</summary>
    public bool Gone { get; set; }
    /// <summary>A rescan found another speaker for a line that already has takes: kept as it was until the user applies it.</summary>
    public string? NewSpeakerKey { get; set; }
    public string? NewSpeakerName { get; set; }
    /// <summary>The user chose to keep this line's speaker: rescans don't propose another one.</summary>
    public bool SpeakerPinned { get; set; }

    [JsonIgnore] public string RelativePath => System.IO.Path.Combine("Sound", "Voice", PluginFolder, VoiceType, (FileStem ?? "") + ".fuz");
}

public sealed class RevoiceScanReport
{
    public int Infos { get; set; }
    public int Responses { get; set; }
    public int Lines { get; set; }
    public int WithOriginal { get; set; }
    public int UnknownSpeaker { get; set; }
    public int VoiceFiles { get; set; }
    /// <summary>Voice files in the mod's own sources that match no line (merged, compacted or renamed plugins).</summary>
    public int Orphans { get; set; }
    public List<string> MissingMasters { get; set; } = new();
    public List<string> MissingStrings { get; set; } = new();
    public List<string> Errors { get; set; } = new();
    public List<string> MasterCosts { get; set; } = new();
    /// <summary>Lines with takes whose speaker the rescan would change (kept until the user decides).</summary>
    public int SpeakerChanges { get; set; }
    /// <summary>Lines whose in-game text changed since their take: marked "changed since take".</summary>
    public int TextChanged { get; set; }
    public DateTime ScannedUtc { get; set; }
}

/// <summary>An orphaned voice file (its line was not found, e.g. after a merge or compaction) and a proposed line for it.</summary>
public sealed class OrphanMatch
{
    public string SourceId { get; set; } = "";
    public string Inner { get; set; } = "";
    public string Display { get; set; } = "";
    public string VoiceType { get; set; } = "";
    public string? LineId { get; set; }
    public string? LineText { get; set; }
    public double Score { get; set; }
    public string Method { get; set; } = "";
    public string? Transcript { get; set; }
}

public sealed class RevoiceProject
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..10];
    public string Name { get; set; } = "";
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
    public List<RevoicePlugin> Plugins { get; set; } = new();
    public List<RevoiceSource> Sources { get; set; } = new();
    public bool UseBaseGame { get; set; } = true;
    public string Language { get; set; } = "English";
    /// <summary>Loose files are written here as Sound\Voice\..., plus the new voice type plugin and its SkyPatcher / FLM files.</summary>
    public string OutputFolder { get; set; } = "";
    public Dictionary<string, SpeakerAssignment> Speakers { get; set; } = new();
    /// <summary>Engine settings shared by every speaker (model, stability, speed, tags, pronunciations…); each speaker only picks the engine and voice.</summary>
    public VoicePack Voice { get; set; } = new() { Name = "NPC Revoice", Naming = NamingMode.Legacy };
    public List<NewVoiceTypePlan> NewVoiceTypes { get; set; } = new();
    /// <summary>Name of the ESL plugin holding new voice types.</summary>
    public string VoiceTypePlugin { get; set; } = "";
    /// <summary>SkyPatcher / FLM refer to forms by EditorID (as Aela Revoiced does) instead of Plugin|FormID.</summary>
    public bool ReferenceByEditorId { get; set; } = true;
    /// <summary>Use the game's original audio as the Voice Changer source instead of text-to-speech.</summary>
    public bool VoiceChanger { get; set; }
    /// <summary>No longer used (the Voice Changer no longer asks first); kept so older project files load unchanged.</summary>
    public bool VoiceChangerWarningAccepted { get; set; }
    public bool EmotionTags { get; set; } = true;
    public List<RevoiceLine> Lines { get; set; } = new();
    public RevoiceScanReport? LastScan { get; set; }
    /// <summary>Proposed links for orphaned files, waiting for approval.</summary>
    public List<OrphanMatch> OrphanProposals { get; set; } = new();
    public DateTime? LastExportUtc { get; set; }
    /// <summary>.fuz files this tool has written to the output folder (relative paths), so files that no longer match a line can be cleaned up.</summary>
    public List<string> ExportedFiles { get; set; } = new();
    /// <summary>The take each exported file holds (relative path, lower case → take id): a file is rebuilt when its line has another take.</summary>
    public Dictionary<string, string> ExportedTakes { get; set; } = new();
}
