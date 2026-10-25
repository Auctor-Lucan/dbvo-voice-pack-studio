using System.Text.Json.Serialization;
using DbvoStudio.Core.Naming;
using DbvoStudio.Core.Projects;
using DbvoStudio.Core.Revoice;

namespace DbvoStudio.Core.Books;

/// <summary>How sure an attribution is (the IVBOS specification's three levels).</summary>
public enum Certainty
{
    /// <summary>Nothing found.</summary>
    None,
    /// <summary>A guess from indirect evidence (a name in the text, a UESP note): needs a look before it is used.</summary>
    Inferred,
    /// <summary>Named, but not verified against game data (e.g. no NPC record with that name).</summary>
    Unverified,
    /// <summary>Verified: confirmed by the user, or by the book's text and an NPC record.</summary>
    Confirmed,
}

/// <summary>One BOOK record that plays a book's audio (several records can share one text, e.g. a skill book and its plain copy).</summary>
public sealed class BookRecordRef
{
    /// <summary>The plugin that defines the record (the .ini names it), e.g. "Skyrim.esm".</summary>
    public string Plugin { get; set; } = "";
    /// <summary>File-local FormID (no load-order index), e.g. 0x1AFB3.</summary>
    public uint FormId { get; set; }
    public string EditorId { get; set; } = "";
    [JsonIgnore] public string Hex => "0x" + FormId.ToString("X");
}

/// <summary>A different text of the same book used when another plugin is (or is not) installed, e.g. USSEP's corrected letters.</summary>
public sealed class BookVariant
{
    public string Id { get; set; } = NewId.Short();
    /// <summary>The plugin this depends on, e.g. "Unofficial Skyrim Special Edition Patch.esp".</summary>
    public string Plugin { get; set; } = "";
    /// <summary>Short name shown in the installer, e.g. "USSEP".</summary>
    public string Label { get; set; } = "";
    /// <summary>True: this text is used when the plugin is installed; false: when it is not.</summary>
    public bool WhenInstalled { get; set; } = true;
    public string Spoken { get; set; } = "";
}

/// <summary>A voiced book: one text, one audio file per voice that reads it.</summary>
public sealed class Book
{
    /// <summary>Stable key and audio file name (lower case, no extension): sound\fx\voicedbooksofskyrim\&lt;Stem&gt;.xwm.</summary>
    public string Stem { get; set; } = "";
    public List<BookRecordRef> Records { get; set; } = new();
    public string Title { get; set; } = "";
    /// <summary>The text as the game shows it (markup removed, paragraphs kept).</summary>
    public string InGame { get; set; } = "";
    /// <summary>The book's text with the game's markup, as read from the plugin (for alias checks).</summary>
    public string Raw { get; set; } = "";
    /// <summary>What the voice reads. Starts from <see cref="InGame"/> with the spoken-text rules applied.</summary>
    public string Spoken { get; set; } = "";
    public bool SpokenEdited { get; set; }
    /// <summary>book | note | letter | journal | tome | recipe | skill</summary>
    public string Kind { get; set; } = "book";
    public bool Excluded { get; set; }
    /// <summary>Why it is left out (empty text, recipe, spell tome, unreadable script, user).</summary>
    public string ExcludeReason { get; set; } = "";
    /// <summary>A spell tome or recipe that has real readable text: suggested for voicing.</summary>
    public bool SuggestInclude { get; set; }

    // ---- who wrote it
    public string Author { get; set; } = "";
    /// <summary>by-line | signature | uesp | name | user | set</summary>
    public string AuthorSource { get; set; } = "";
    public Certainty AuthorCertainty { get; set; }
    /// <summary>The NPC record the author resolves to ("Skyrim.esm|01A666"), if any.</summary>
    public string AuthorNpc { get; set; } = "";
    public string AuthorNpcName { get; set; } = "";
    /// <summary>The author NPC's voice type (EditorID), when it resolves to one.</summary>
    public string VoiceType { get; set; } = "";
    public Certainty VoiceTypeCertainty { get; set; }
    /// <summary>Several possible voice types (template or leveled list); the user picks one.</summary>
    public List<string> VoiceTypeCandidates { get; set; } = new();
    /// <summary>How the voice type was found, e.g. "NPC record (VTCK)", "template LCharBanditBossNordM".</summary>
    public string VoiceTypeVia { get; set; } = "";
    /// <summary>The user confirmed the author and voice.</summary>
    public bool AuthorConfirmed { get; set; }

    /// <summary>"narrator" or "npc": read by the narrator(s), or by the author's voice type.</summary>
    public string Reader { get; set; } = "narrator";

    // ---- parts
    /// <summary>Character offsets in <see cref="Spoken"/> where a new part starts (the first part starts at 0 and is not listed).</summary>
    public List<int> Breaks { get; set; } = new();
    /// <summary>The user moved the part boundaries: keep them when the text changes (as far as they still fit).</summary>
    public bool ManualBreaks { get; set; }

    public List<BookVariant> Variants { get; set; } = new();
    /// <summary>Read by this voice only (a narrator "n:id" or an NPC voice type "vt:Type"); empty = the usual readers.</summary>
    public string VoiceOverride { get; set; } = "";
    /// <summary>Speaking speed for this book (ElevenLabs 0.7–1.2, OmniVoice 0.5–2); null = the voice's own.</summary>
    public double? Speed { get; set; }
    /// <summary>Pause at paragraph joins for this book (ms); null = the project's Audio setting.</summary>
    public int? ParagraphPauseMs { get; set; }
    /// <summary>The project's reverb when this book is joined; false when its parts have an effect (two reverbs sound muddy). Null = on.</summary>
    public bool? Reverb { get; set; }

    /// <summary>Built-in set: the text version this book came with, and a newer one if the set was updated since.</summary>
    public int SetVersion { get; set; }
    public string? UpdatedText { get; set; }
    public int UpdatedVersion { get; set; }

    public bool Marked { get; set; }
    public string Notes { get; set; } = "";
    /// <summary>No longer found in the plugins on the last scan (kept because it has audio or edits).</summary>
    public bool Gone { get; set; }

    [JsonIgnore] public BookRecordRef? First => Records.FirstOrDefault();
}

/// <summary>A narrator voice. Every narrated book gets one audio file per narrator; each narrator becomes its own archive.</summary>
public sealed class BookNarrator
{
    public string Id { get; set; } = NewId.Short();
    /// <summary>Shown in archive names: "&lt;prefix&gt; &lt;mod&gt; - &lt;Name&gt;".</summary>
    public string Name { get; set; } = "Narrator";
    public string Engine { get; set; } = "elevenlabs";
    public string? VoiceId { get; set; }
    public string? VoiceName { get; set; }
    public bool Female { get; set; }
    /// <summary>loud (the IVBOS release chain) or normal.</summary>
    public string Level { get; set; } = "loud";
}

/// <summary>The voice for books written by NPCs of one vanilla voice type.</summary>
public sealed class VoiceTypeVoice
{
    public string VoiceType { get; set; } = "";
    public string Engine { get; set; } = "elevenlabs";
    public string? VoiceId { get; set; }
    public string? VoiceName { get; set; }
    public bool Female { get; set; }
    public string Level { get; set; } = "loud";
    /// <summary>Leave these books to the narrator(s) instead.</summary>
    public bool UseNarrator { get; set; }
    /// <summary>Added by the user (Narrators &amp; authors → Add a voice type): listed and selectable for every book even before a book uses it.</summary>
    public bool Custom { get; set; }
}

/// <summary>One part of one book for one voice: the unit that is generated (a take per part).</summary>
public sealed class BookLine : Phase2Line
{
    public string Stem { get; set; } = "";
    /// <summary>"n:&lt;narratorId&gt;" or "vt:&lt;VoiceType&gt;".</summary>
    public string Voice { get; set; } = "";
    /// <summary>1-based part number.</summary>
    public int Part { get; set; }
    /// <summary>Empty for the main text, else the variant id.</summary>
    public string Variant { get; set; } = "";
    /// <summary>The part ends a paragraph (a pause follows at the join).</summary>
    public bool ParagraphEnd { get; set; }
    public int Start { get; set; }
    public int End { get; set; }

    public static string MakeId(string voice, string stem, string variant, int part) =>
        $"{voice}|{stem}{(variant.Length > 0 ? "@" + variant : "")}|{part}";
}

/// <summary>What the joined audio of one book for one voice was built from: rebuilt only when something here changes.</summary>
public sealed class BookJoin
{
    public string Voice { get; set; } = "";
    public string Stem { get; set; } = "";
    public string Variant { get; set; } = "";
    /// <summary>Take id per part, in order.</summary>
    public List<string> Takes { get; set; } = new();
    /// <summary>Fingerprint of the parts' texts, the takes' audio and the audio chain settings.</summary>
    public string Fingerprint { get; set; } = "";
    /// <summary>Relative to the project folder: joined\&lt;voice&gt;\&lt;stem&gt;.wav and .xwm.</summary>
    public string Wav { get; set; } = "";
    public string Xwm { get; set; } = "";
    public double Seconds { get; set; }
    public double Lufs { get; set; }
    public double PeakDb { get; set; }
    public DateTime BuiltUtc { get; set; } = DateTime.UtcNow;
}

public sealed class BooksScanReport
{
    public DateTime ScannedUtc { get; set; } = DateTime.UtcNow;
    public int Records { get; set; }
    public int Books { get; set; }
    public int Excluded { get; set; }
    public int Merged { get; set; }
    public int Npc { get; set; }
    public List<string> MissingMasters { get; set; } = new();
    public List<string> MissingStrings { get; set; } = new();
    public List<string> Notes { get; set; } = new();
}

public sealed class BooksProject
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..10];
    public string Name { get; set; } = "";
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
    /// <summary>"plugins" (read from the plugins) or the id of a built-in set ("skyrim-dlc").</summary>
    public string Source { get; set; } = "plugins";
    public int SetVersion { get; set; }
    public List<RevoicePlugin> Plugins { get; set; } = new();
    public List<RevoiceSource> Sources { get; set; } = new();
    public bool UseBaseGame { get; set; } = true;
    public string Language { get; set; } = "English";
    /// <summary>Short mod name used in archive and .ini names, e.g. "VIGILANT".</summary>
    public string ModName { get; set; } = "";
    /// <summary>First word of every archive name, e.g. "IVBOS".</summary>
    public string ArchivePrefix { get; set; } = "VBOS";
    /// <summary>Name of the .ini without "_VBOS.ini", e.g. "Vigilant" → Vigilant_VBOS.ini.</summary>
    public string IniName { get; set; } = "";
    public List<BookNarrator> Narrators { get; set; } = new();
    public Dictionary<string, VoiceTypeVoice> VoiceTypes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Engine settings shared by every voice (model, stability, speed, pronunciations…).</summary>
    public VoicePack Voice { get; set; } = new() { Name = "Voiced books", Naming = NamingMode.Legacy };
    /// <summary>Books written by NPCs get the NPC's voice type voice (off: "narrated books only").</summary>
    public bool NpcVoices { get; set; } = true;
    /// <summary>Write the VBoS .ini (off when another mod's .ini already maps these books).</summary>
    public bool WriteIni { get; set; } = true;
    /// <summary>bsa (archive + dummy plugin per narrator) or loose (Sound\fx\... folders).</summary>
    public string Packaging { get; set; } = "bsa";
    public string OutputFolder { get; set; } = "";
    /// <summary>Wanted part length in characters; parts break at paragraphs, then sentences.</summary>
    public int PartTarget { get; set; } = 2000;
    public int PartMin { get; set; } = 1500;
    public int PartMax { get; set; } = 2500;
    /// <summary>Send the neighbouring parts' text with each request so the voice flows across joins (not available on eleven_v3).</summary>
    public bool SendContext { get; set; } = true;
    public BookAudioSettings Audio { get; set; } = new();
    public List<Book> Books { get; set; } = new();
    public List<BookLine> Lines { get; set; } = new();
    public Dictionary<string, BookJoin> Joins { get; set; } = new();
    public BooksScanReport? LastScan { get; set; }
    public DateTime? LastExportUtc { get; set; }
    /// <summary>Files written by the last export (relative to the output folder), so stale ones can be cleaned up.</summary>
    public List<string> ExportedFiles { get; set; } = new();
    /// <summary>Spoken-text rules applied automatically to new books.</summary>
    public bool AutoSpokenRules { get; set; } = true;

    public static string NarratorKey(string id) => "n:" + id;
    public static string VoiceTypeKey(string vt) => "vt:" + vt;
}
