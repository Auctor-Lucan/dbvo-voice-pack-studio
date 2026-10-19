using System.Text.Json.Serialization;
using DbvoStudio.Core.Naming;
using DbvoStudio.Core.Plugins;
using DbvoStudio.Core.Rules;

namespace DbvoStudio.Core.Projects;

/// <summary>Global settings, saved the moment they change.</summary>
public sealed class AppSettings
{
    public int SchemaVersion { get; set; } = 1;
    public string ElevenLabsApiKey { get; set; } = "";
    public string FonixDataPath { get; set; } = "";
    public string XwmaEncodePath { get; set; } = "";
    /// <summary>Empty = the bundled copy next to the app.</summary>
    public string FfmpegPath { get; set; } = "";
    public string FaceFxWrapperPath { get; set; } = "";
    /// <summary>Folder holding Skyrim.esm, Update.esm and the DLC masters for parsing.</summary>
    public string BaseGameFolder { get; set; } = "";
    /// <summary>Folder holding localized strings (e.g. Data\Strings).</summary>
    public string StringsFolder { get; set; } = "";
    public string StringsLanguage { get; set; } = "English";
    public List<ReferenceSet> References { get; set; } = new();
    public AiProviderSettings AiProvider { get; set; } = new();
    public LocalEngineSettings LocalEngines { get; set; } = new();
    public int BackupsToKeep { get; set; } = 30;
    public string? LastVoicePackId { get; set; }
    public Dictionary<string, string> Ui { get; set; } = new();
}

/// <summary>A topic .txt of lines users already have (e.g. the base game pack). Matching lines are omitted.</summary>
public sealed class ReferenceSet
{
    public string Id { get; set; } = NewId.Short();
    public string Name { get; set; } = "";
    /// <summary>Copy of the topic file inside the library.</summary>
    public string StoredFile { get; set; } = "";
    public int LineCount { get; set; }
    public DateTime AddedUtc { get; set; } = DateTime.UtcNow;
}

public sealed class AiProviderSettings
{
    /// <summary>OpenAI-compatible base URL, e.g. https://api.openai.com/v1 or http://localhost:11434/v1 (Ollama).</summary>
    public string Endpoint { get; set; } = "";
    public string ApiKey { get; set; } = "";
    public string Model { get; set; } = "";
}

public sealed class LocalEngineSettings
{
    public string VoxCpmAddress { get; set; } = "http://localhost:7861";
    public string OmniVoiceAddress { get; set; } = "http://localhost:7862";
}

public sealed class ElevenSettings
{
    public string VoiceId { get; set; } = "";
    public string VoiceName { get; set; } = "";
    public string ModelId { get; set; } = "eleven_v4";
    public double Stability { get; set; } = 0.5;
    public double Similarity { get; set; } = 0.75;
    public double Style { get; set; } = 0;
    /// <summary>Off by default: speaker boost can cause clipping and harshness.</summary>
    public bool SpeakerBoost { get; set; }
    public double Speed { get; set; } = 1.0;
    public long? Seed { get; set; }
    /// <summary>mp3_44100_128 works on every plan; pcm_44100 is lossless where the plan allows it.</summary>
    public string OutputFormat { get; set; } = "mp3_44100_128";
    public string LanguageCode { get; set; } = "";
    /// <summary>auto | on | off</summary>
    public string TextNormalization { get; set; } = "auto";
    /// <summary>The plan's concurrent request limit.</summary>
    public int Concurrency { get; set; } = 5;
    public List<string> PronunciationDictionaryIds { get; set; } = new();

    public ElevenSettings Clone() => (ElevenSettings)MemberwiseClone();
}

public sealed class Pronunciation
{
    public string Word { get; set; } = "";
    /// <summary>"alias" = say it as; "phoneme" = IPA.</summary>
    public string Kind { get; set; } = "alias";
    public string Replacement { get; set; } = "";
    public bool WholeWord { get; set; } = true;
    public bool CaseSensitive { get; set; }
}

public sealed class Destinations
{
    /// <summary>Where WAV + LIP files go (and a copy of each DBVO Fix plugin).</summary>
    public string WavLipFolder { get; set; } = "";
    /// <summary>Root of the final mod / FOMOD folder (also receives the DBVO Fix plugins).</summary>
    public string FinalRoot { get; set; } = "";
    /// <summary>Folder template under the final root, e.g. "{mod}\Sound\DBVO\{packId}". The .fuz files land in the last folder.</summary>
    public string FuzTemplate { get; set; } = @"{mod}\Sound\DBVO\{packId}";
    public string ReleaseFolder { get; set; } = "";
}

/// <summary>A voice: its ElevenLabs settings, tags, pronunciations, naming mode and destinations.</summary>
public sealed class VoicePack
{
    public string Id { get; set; } = NewId.Short();
    public string Name { get; set; } = "";
    /// <summary>DBVO voice_pack_id: the folder name under Sound\DBVO.</summary>
    public string PackId { get; set; } = "";
    public string Author { get; set; } = "";
    public NamingMode Naming { get; set; } = NamingMode.Legacy;
    public string Engine { get; set; } = "elevenlabs";
    public ElevenSettings Eleven { get; set; } = new();
    public List<string> Tags { get; set; } = new() { "[whispers]", "[sighs]", "[laughs]", "[sarcastic]", "[curious]", "[excited]" };
    public List<Pronunciation> Pronunciations { get; set; } = new();
    public Destinations Destinations { get; set; } = new();
    public List<string> DefaultReferenceIds { get; set; } = new();
    public bool SettingsLocked { get; set; }
    public bool Loudnorm { get; set; }
    public double GainDb { get; set; }
    public bool AliasParsing { get; set; }
    public bool OverwriteWavLip { get; set; }
    public bool OverwriteFuz { get; set; }
    public double TrimTailMs { get; set; } = 15;
    public double FadeOutMs { get; set; } = 10;
}

public enum Phase1Status { Active, ChangedInMod, Removed }

/// <summary>One player line in Phase 1, with the user's decision.</summary>
public sealed class Phase1Line
{
    public uint FormId { get; set; }
    public LineField Field { get; set; }
    public string? EditorId { get; set; }
    public string Original { get; set; } = "";
    public string? Suggested { get; set; }
    /// <summary>The text that ends up in game (and in the topic file). Null = not usable yet.</summary>
    public string? Final { get; set; }
    public LineKind Kind { get; set; }
    public bool UserEdited { get; set; }
    public bool Reviewed { get; set; }
    public bool HiddenByPrompts { get; set; }
    public bool IncludeAnyway { get; set; }
    /// <summary>User-excluded from the topic file.</summary>
    public bool Excluded { get; set; }
    public Phase1Status Status { get; set; } = Phase1Status.Active;
    /// <summary>When the mod changed this line: the user's earlier final text.</summary>
    public string? PreviousFinal { get; set; }
    public string? PreviousOriginal { get; set; }
    /// <summary>Written-out alternatives for alias lines; each becomes its own topic line and file.</summary>
    public List<string> Variants { get; set; } = new();
    public int Order { get; set; }

    [JsonIgnore] public string Key => $"{FormId:X8}:{(Field == LineField.TopicText ? "FULL" : "RNAM")}";

    [JsonIgnore]
    public bool IsFlagged => Kind is LineKind.Broken or LineKind.TooLong;

    /// <summary>Text written to the DBVO Fix plugin when it differs from the original.</summary>
    [JsonIgnore]
    public bool NeedsOverride => Status != Phase1Status.Removed && !Excluded && Final != null && Final != Original
                                 && !(HiddenByPrompts && !IncludeAnyway);

    /// <summary>Lines this row contributes to the topic file.</summary>
    public IEnumerable<string> TopicLines()
    {
        if (Status == Phase1Status.Removed || Excluded) yield break;
        if (HiddenByPrompts && !IncludeAnyway) yield break;
        if (Final != null && !ScriptRules.HasPlaceholder(LineNaming.SpokenPart(Final)) && Final.Length > 0) yield return Final;
        foreach (var v in Variants.Where(v => !string.IsNullOrWhiteSpace(v))) yield return v.Trim();
    }
}

public sealed class ModPlugin
{
    public string FileName { get; set; } = "";
    /// <summary>Copy of the plugin inside the library.</summary>
    public string StoredFile { get; set; } = "";
    public List<string> Masters { get; set; } = new();
    public bool Localized { get; set; }
    public string FixPrefix { get; set; } = FixPluginWriter.DefaultPrefix;
    public List<Phase1Line> Lines { get; set; } = new();
    public DateTime ImportedUtc { get; set; } = DateTime.UtcNow;
    public List<string> Notices { get; set; } = new();

    [JsonIgnore] public string FixFileName => FixPluginWriter.FixFileName(FileName, FixPrefix);
    [JsonIgnore] public bool NeedsFixPlugin => Lines.Any(l => l.NeedsOverride);
}

/// <summary>A mod: Phase 1 work, shared by every voice pack.</summary>
public sealed class Mod
{
    public string Id { get; set; } = NewId.Short();
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
    public string NexusUrl { get; set; } = "";
    public List<ModPlugin> Plugins { get; set; } = new();
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
}

public enum Phase2Status { Pending, Generating, Generated, Failed, Omitted, Stale }

public sealed class TakeSettings
{
    public string Engine { get; set; } = "elevenlabs";
    public string VoiceId { get; set; } = "";
    public string ModelId { get; set; } = "";
    public double Stability { get; set; }
    public double Similarity { get; set; }
    public double Style { get; set; }
    public bool SpeakerBoost { get; set; }
    public double Speed { get; set; }
    public long? Seed { get; set; }
    public string OutputFormat { get; set; } = "";
}

public sealed class Take
{
    public string Id { get; set; } = NewId.Short();
    public int Slot { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public string SpokenText { get; set; } = "";
    public TakeSettings Settings { get; set; } = new();
    /// <summary>Relative to the patch folder: takes\&lt;lineId&gt;\03\&lt;stem&gt;.wav</summary>
    public string WavPath { get; set; } = "";
    public string LipPath { get; set; } = "";
    public double DurationSeconds { get; set; }
}

public sealed class LineSource
{
    public string ModId { get; set; } = "";
    public string Plugin { get; set; } = "";
    public uint FormId { get; set; }
    public LineField Field { get; set; }
}

/// <summary>One line in Phase 2.</summary>
public sealed class Phase2Line
{
    /// <summary>Short stable ID; names the line's take folder.</summary>
    public string LineId { get; set; } = "";
    /// <summary>Naming key (legacy stem upper-cased, or hash).</summary>
    public string Key { get; set; } = "";
    /// <summary>The in-game line: decides the filename. Read-only in Phase 2.</summary>
    public string FinalLine { get; set; } = "";
    /// <summary>The line before Phase 1 changed it.</summary>
    public string OriginalLine { get; set; } = "";
    /// <summary>What the voice says. Free text, tags allowed.</summary>
    public string Spoken { get; set; } = "";
    public bool SpokenEdited { get; set; }
    public List<LineSource> Sources { get; set; } = new();
    public Phase2Status Status { get; set; } = Phase2Status.Pending;
    public string? OmittedBy { get; set; }
    /// <summary>Generate even though a reference already has it.</summary>
    public bool OverrideOmit { get; set; }
    public List<Take> Takes { get; set; } = new();
    public string? SelectedTakeId { get; set; }
    public string? LastError { get; set; }
    /// <summary>Pending AI auto-tag suggestion shown inline until accepted or undone.</summary>
    public string? TagSuggestion { get; set; }

    [JsonIgnore] public Take? SelectedTake => Takes.FirstOrDefault(t => t.Id == SelectedTakeId);
    [JsonIgnore] public bool IsOmitted => OmittedBy != null && !OverrideOmit;
    [JsonIgnore] public bool SpokenDiffersFromFinal => !string.Equals(SpokenText.StripTags(Spoken), LineNaming.SpokenPart(FinalLine), StringComparison.Ordinal);
    [JsonIgnore] public bool EditedInPhase1 => !string.Equals(FinalLine, OriginalLine, StringComparison.Ordinal);
}

/// <summary>A DBVO Patch: one voice pack x one or more mods.</summary>
public sealed class DbvoPatch
{
    public string Id { get; set; } = NewId.Short();
    public string Name { get; set; } = "";
    public string VoicePackId { get; set; } = "";
    public List<string> ModIds { get; set; } = new();
    /// <summary>Reference sets whose lines are omitted here.</summary>
    public List<string> ReferenceIds { get; set; } = new();
    /// <summary>Per-patch override of the WAV+LIP folder (empty = the voice pack's).</summary>
    public string WavLipFolder { get; set; } = "";
    public string FinalRoot { get; set; } = "";
    public string FuzTemplate { get; set; } = "";
    public List<Phase2Line> Lines { get; set; } = new();
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
}

internal static class SpokenTextExtensions
{
    public static string StripTags(this string s) => SpokenText.StripTags(s);
}

public static class NewId
{
    private const string Alphabet = "abcdefghijkmnpqrstuvwxyz23456789";

    public static string Short(int length = 8)
    {
        Span<byte> b = stackalloc byte[length];
        System.Security.Cryptography.RandomNumberGenerator.Fill(b);
        Span<char> c = stackalloc char[length];
        for (int i = 0; i < length; i++) c[i] = Alphabet[b[i] % Alphabet.Length];
        return new string(c);
    }

    /// <summary>Deterministic short ID from a key (for take folders).</summary>
    public static string FromKey(string key, int length = 8)
    {
        ulong h = XxHash64.Hash(System.Text.Encoding.UTF8.GetBytes(key));
        Span<char> c = stackalloc char[length];
        for (int i = 0; i < length; i++)
        {
            c[i] = Alphabet[(int)(h % (ulong)Alphabet.Length)];
            h /= (ulong)Alphabet.Length;
        }
        return new string(c);
    }
}
