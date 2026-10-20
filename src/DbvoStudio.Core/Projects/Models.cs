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
    /// <summary>7z.exe from 7-Zip, for .7z FOMOD archives (zip needs nothing).</summary>
    public string SevenZipPath { get; set; } = "";
    public WavToLipSettings WavToLip { get; set; } = new();
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
    /// <summary>The global DBVO pronunciation dictionary, used by every voice pack. Voice pack entries win for the same word.</summary>
    public List<Pronunciation> Pronunciations { get; set; } = new();
    /// <summary>Mod categories for sorting DBVO Patches (user-editable, in display order).</summary>
    public List<string> Categories { get; set; } = new(DefaultCategories);
    /// <summary>Reference sets ticked by default in new DBVO Patches.</summary>
    public List<string> DefaultReferenceIds { get; set; } = new();

    public static readonly string[] DefaultCategories =
        { "Main Game", "DLCs", "Large Quests", "Small Quests", "Followers", "Immersion", "NSFW", "Misc" };
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

/// <summary>WAV to LIP tool (Other Tools): Whisper transcription + FaceFXWrapper, as in the WAV_to_LIP Python app.</summary>
public sealed class WavToLipSettings
{
    /// <summary>"builtin" = Whisper.net inside this app; "faster-whisper" = the user's Python faster-whisper install.</summary>
    public string Engine { get; set; } = "builtin";
    public string Model { get; set; } = "large-v3";
    public string Language { get; set; } = "auto";
    public string Device { get; set; } = "cuda";
    public string Compute { get; set; } = "float16";
    public bool CpuFallback { get; set; } = true;
    /// <summary>Use the Legacy filename as the text instead of transcribing (instant, never mishears).</summary>
    /// <summary>No longer used: Whisper transcription is the only reliable text for lip sync (kept so old settings files load).</summary>
    public bool UseFilename { get; set; }
    /// <summary>faster-whisper: python.exe of the environment that has faster-whisper (e.g. ...\WAV_to_LIP_App\.venv\Scripts\python.exe).</summary>
    public string PythonPath { get; set; } = "";
    /// <summary>faster-whisper: folder with the CUDA DLLs (cuBLAS/cuDNN), put on PATH (e.g. ...\WAV_to_LIP_App\cuda_libs).</summary>
    public string CudaLibsFolder { get; set; } = "";
    /// <summary>Built-in: folder for the downloaded ggml Whisper models (empty = inside the library).</summary>
    public string ModelsFolder { get; set; } = "";
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

/// <summary>VoxCPM local server settings (same fields and defaults as DBVO Voice Pack Maker 2.7.0).</summary>
public sealed class VoxCpmSettings
{
    /// <summary>Reference voice name on the server (its speaker_wav).</summary>
    public string Voice { get; set; } = "";
    public double CfgValue { get; set; } = 2.1;
    public int InferenceTimesteps { get; set; } = 12;
    public bool Normalize { get; set; } = true;
    public bool RetryBadcase { get; set; } = true;
    public bool ReferenceMode { get; set; }
    public double ControlCfgValue { get; set; } = 2.7;
    public string Language { get; set; } = "en";
    public int Concurrency { get; set; } = 1;
}

/// <summary>OmniVoice local server settings (same fields and defaults as 2.7.0).</summary>
public sealed class OmniVoiceSettings
{
    public string Voice { get; set; } = "";
    public string Model { get; set; } = "k2-fsa/OmniVoice";
    public int NumStep { get; set; } = 32;
    public double Speed { get; set; } = 1.0;
    public double GuidanceScale { get; set; } = 3.0;
    public int Concurrency { get; set; } = 1;
}

/// <summary>ElevenLabs speech-to-speech (Voice Changer) settings, as in the Voice Changer tool.</summary>
public sealed class StsSettings
{
    public string VoiceId { get; set; } = "";
    public string VoiceName { get; set; } = "";
    public string ModelId { get; set; } = "eleven_multilingual_sts_v2";
    public double Stability { get; set; } = 0.5;
    public double Similarity { get; set; } = 0.75;
    public double Style { get; set; }
    public bool SpeakerBoost { get; set; }
    public long? Seed { get; set; }
    public bool RemoveBackgroundNoise { get; set; }
    public string OutputFormat { get; set; } = "mp3_44100_128";
    public int Concurrency { get; set; } = 5;
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

/// <summary>
/// Folder templates of a voice pack. New DBVO Patches get their own folders from these ({mod} = the mod name);
/// each patch can then change its folders.
/// </summary>
public sealed class Destinations
{
    /// <summary>Template for a patch's WAV + LIP folder, e.g. C:\Dev\Vampira - {mod}\RawWav.</summary>
    public string WavLipFolder { get; set; } = "";
    /// <summary>Template for a patch's final mod folder (receives the DBVO Fix plugin), e.g. C:\FOMOD\Vamp - {mod}.</summary>
    public string FinalRoot { get; set; } = "";
    /// <summary>Sub-folder under the final folder where .fuz files go.</summary>
    public string FuzTemplate { get; set; } = @"Sound\DBVO\{packId}";
    /// <summary>Template for a patch's DBVO Fix plugin + topic file folder (kept apart from the WAV + LIP files).</summary>
    public string FixFolder { get; set; } = "";
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
    public VoxCpmSettings VoxCpm { get; set; } = new();
    public OmniVoiceSettings OmniVoice { get; set; } = new();
    /// <summary>Voice Changer (speech-to-speech) settings of this voice pack.</summary>
    public StsSettings Sts { get; set; } = new();
    /// <summary>Accent colour for this voice pack in lists and headers, e.g. #a371f7.</summary>
    public string Color { get; set; } = "";
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
    /// <summary>Exported to the player-name topic file (Downloads).</summary>
    public bool PlayerNameExported { get; set; }

    [JsonIgnore] public string Key => $"{FormId:X8}:{(Field == LineField.TopicText ? "FULL" : "RNAM")}";

    /// <summary>Contains &lt;Alias=Player&gt; (any case): voiced through separate player-name patches, not here.</summary>
    [JsonIgnore] public bool IsPlayerName => ScriptRules.HasPlayerAlias(Final ?? Original);

    /// <summary>The user's approval to voice this line (topic file). Null = the default for the line.</summary>
    public bool? VoiceApproved { get; set; }
    /// <summary>The user's approval to write the edited text to the DBVO Fix plugin. Null = the default.</summary>
    public bool? FixApproved { get; set; }

    /// <summary>The script flagged the plugin's text (BROKEN LINE / TOO LONG WILL CTD), whether or not it has been fixed since.</summary>
    [JsonIgnore]
    public bool WasFlagged => (Kind == LineKind.Broken && !IsPlayerName) || Kind == LineKind.TooLong;

    /// <summary>The final text is only a stage direction in (parentheses) or [brackets], e.g. "(Laughs)" or "(Sip your drink)".</summary>
    [JsonIgnore] public bool IsStageDirection => Final != null && ScriptRules.IsStageDirection(Final);

    /// <summary>A stage direction nobody has decided on yet: voice it (e.g. a laugh) or keep it silent.</summary>
    [JsonIgnore] public bool NeedsVoiceDecision => IsStageDirection && VoiceApproved == null && Status != Phase1Status.Removed && !Excluded;

    /// <summary>A flagged line whose final text now passes the script (fixed by hand or by an imported DBVO Fix plugin), or a decided stage direction.</summary>
    [JsonIgnore]
    public bool IsResolved => WasFlagged && Final != null && Final != Original
                              && (ScriptRules.IsVoiceReady(Final) || (IsStageDirection && VoiceApproved != null));

    /// <summary>Still needs the user's decision.</summary>
    [JsonIgnore]
    public bool IsFlagged => (WasFlagged && !IsResolved) || NeedsVoiceDecision;

    private bool Active => Status != Phase1Status.Removed && !Excluded && !(HiddenByPrompts && !IncludeAnyway);

    /// <summary>What the line gets without an explicit approval: voiced when the final text is usable and not a stage direction.</summary>
    [JsonIgnore]
    public bool DefaultVoice => Final != null && Final.Length > 0 && !IsStageDirection && !ScriptRules.HasPlaceholder(LineNaming.SpokenPart(Final));

    /// <summary>Default for the DBVO Fix plugin: the edit goes in unless it is an untouched flagged line.</summary>
    [JsonIgnore]
    public bool DefaultFix => !(WasFlagged && !IsResolved && !UserEdited && !IsStageDirection);

    /// <summary>The line goes into the topic file (and is generated in Phase 2).</summary>
    [JsonIgnore] public bool Voiced => Active && Final != null && (VoiceApproved ?? DefaultVoice);

    /// <summary>Text written to the DBVO Fix plugin when it differs from the original (and the user didn't turn it off).</summary>
    [JsonIgnore]
    public bool NeedsOverride => Active && Final != null && Final != Original && (FixApproved ?? DefaultFix);

    /// <summary>Lines this row contributes to the topic file.</summary>
    public IEnumerable<string> TopicLines()
    {
        if (Status == Phase1Status.Removed || Excluded) yield break;
        if (HiddenByPrompts && !IncludeAnyway) yield break;
        if (Voiced) yield return Final!;
        foreach (var v in Variants.Where(v => !string.IsNullOrWhiteSpace(v))) yield return v.Trim();
    }
}

public sealed class ModPlugin
{
    public string FileName { get; set; } = "";
    /// <summary>A topic .txt instead of a plugin: lines are checked, but no DBVO Fix plugin can be made.</summary>
    public bool IsTopicFile { get; set; }
    /// <summary>Name of an imported earlier DBVO Fix plugin, if any.</summary>
    public string? ImportedFixPlugin { get; set; }
    /// <summary>Copy of the plugin inside the library.</summary>
    public string StoredFile { get; set; } = "";
    public List<string> Masters { get; set; } = new();
    public bool Localized { get; set; }
    public string FixPrefix { get; set; } = FixPluginWriter.DefaultPrefix;
    public List<Phase1Line> Lines { get; set; } = new();
    public DateTime ImportedUtc { get; set; } = DateTime.UtcNow;
    public List<string> Notices { get; set; } = new();

    [JsonIgnore] public string FixFileName => FixPluginWriter.FixFileName(FileName, FixPrefix);
    [JsonIgnore] public bool NeedsFixPlugin => !IsTopicFile && Lines.Any(l => l.NeedsOverride);
}

/// <summary>A mod: Phase 1 work, shared by every voice pack.</summary>
public sealed class Mod
{
    public string Id { get; set; } = NewId.Short();
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
    public string NexusUrl { get; set; } = "";
    /// <summary>Category for sorting DBVO Patches (e.g. Followers). Not used in folder paths.</summary>
    public string Category { get; set; } = "Misc";
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
public class Phase2Line
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
    /// <summary>The user's persistent mark (separate from the ticks used for selection).</summary>
    public bool Marked { get; set; }
    /// <summary>Voice Changer: the source WAV this line is converted from (absolute path), if found.</summary>
    public string? SourceWav { get; set; }
    /// <summary>File name (without extension) for takes when it is not derived from the line (NPC Revoice: the game's voice file name).</summary>
    public string? FileStem { get; set; }

    [JsonIgnore] public Take? SelectedTake => Takes.FirstOrDefault(t => t.Id == SelectedTakeId);
    [JsonIgnore] public bool IsOmitted => OmittedBy != null && !OverrideOmit;
    /// <summary>The words differ from the in-game line (tags, spacing and leading markers ignored): shown in blue.</summary>
    [JsonIgnore] public bool SpokenDiffersFromFinal => !string.Equals(SpokenText.StripTags(Spoken), SpokenText.StripTags(SpokenText.DefaultFor(FinalLine)), StringComparison.Ordinal);
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
    /// <summary>Folder for the DBVO Fix plugins and topic file (empty = not written there).</summary>
    public string FixFolder { get; set; } = "";
    public bool OverwriteWavLip { get; set; }
    public bool OverwriteFuz { get; set; }
    /// <summary>"tts" = text-to-speech; "vc" = the Voice Changer toggle is on (speech-to-speech from another voice pack's WAV / .fuz files).</summary>
    public string Kind { get; set; } = "tts";
    /// <summary>With the Voice Changer on: lines without a source file are generated with the voice pack's text-to-speech engine.</summary>
    public bool TtsForMissing { get; set; }
    [JsonIgnore] public bool VoiceChanger => Kind == "vc";
    /// <summary>Voice Changer: folders holding the source voice pack's WAV files (searched with sub-folders).</summary>
    public List<string> SourceFolders { get; set; } = new();
    /// <summary>When .fuz files were last built, and how many.</summary>
    public DateTime? LastFuzUtc { get; set; }
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
