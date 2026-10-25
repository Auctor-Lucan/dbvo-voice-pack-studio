using System.Security.Cryptography;
using System.Text;
using DbvoStudio.Core.Archives;
using DbvoStudio.Core.ElevenLabs;
using DbvoStudio.Core.Plugins;
using DbvoStudio.Core.Projects;
using DbvoStudio.Core.Reference;
using DbvoStudio.Core.Revoice;

namespace DbvoStudio.Core.Books;

/// <summary>A BOOK record as read from the plugins (the winning version).</summary>
public sealed class RawBook
{
    public FormKey Key { get; init; }
    /// <summary>The defining plugin as spelled in the load order.</summary>
    public string Plugin { get; init; } = "";
    public string EditorId { get; init; } = "";
    public string Title { get; init; } = "";
    public string Raw { get; init; } = "";
    public byte Flags { get; init; }
    public List<string> Keywords { get; init; } = new();
    /// <summary>The plugin whose version won.</summary>
    public string Winner { get; init; } = "";
    public bool TeachesSpell => (Flags & 4) != 0;
    public bool TeachesSkill => (Flags & 1) != 0;
}

/// <summary>Loaded plugins for one Voiced Books project (BOOK, NPC and voice type records).</summary>
public sealed class BookSession
{
    public required DialogueIndex Index { get; init; }
    public required Dictionary<FormKey, RawBook> Books { get; init; }
    /// <summary>EditorIDs of NPC and leveled-NPC records (to name templates).</summary>
    public required Dictionary<FormKey, string> EditorIds { get; init; }
    public required string Signature { get; init; }
    public List<string> MissingStrings { get; init; } = new();
}

/// <summary>Voiced Books Tool: read books from plugins, attribute authors, split into parts, and keep the generation lines.</summary>
public sealed class BooksService
{
    public static readonly HashSet<string> Signatures = new() { "BOOK", "NPC_", "VTYP", "LVLN", "KYWD" };

    private readonly Library _lib;
    private readonly AppSettings _settings;
    public BooksService(Library lib, AppSettings settings) { _lib = lib; _settings = settings; }

    /// <summary>Add (or refresh) the base game plugins and string archives from the base game folder.</summary>
    public void ApplyBaseGame(BooksProject p)
    {
        p.Plugins.RemoveAll(x => x.BaseGame);
        p.Sources.RemoveAll(x => x.BaseGame);
        if (!p.UseBaseGame) return;
        var data = RevoiceService.DataFolder(_settings.BaseGameFolder);
        if (data == null) return;
        int i = 0;
        foreach (var m in RevoiceService.BaseMasters)
        {
            var f = Path.Combine(data, m);
            // The built-in set covers the base game: its plugins are targets there; otherwise they are only masters.
            if (File.Exists(f)) p.Plugins.Insert(i++, new RevoicePlugin { Path = f, FileName = m, BaseGame = true, Target = p.Source != "plugins" });
        }
        var iface = Path.Combine(data, "Skyrim - Interface.bsa");
        if (File.Exists(iface)) p.Sources.Add(new RevoiceSource { Kind = "bsa", Path = iface, Priority = -100, BaseGame = true });
    }

    public static string SessionSignature(BooksProject p) =>
        string.Join("|", p.Plugins.Select(x => x.Path + "@" + (File.Exists(x.Path) ? File.GetLastWriteTimeUtc(x.Path).Ticks : 0))) + "#" +
        string.Join("|", p.Sources.Select(s => s.Kind + ":" + s.Path)) + "#" + p.Language;

    /// <summary>Read the plugins (slow for Skyrim.esm: reuse the session while nothing changed).</summary>
    public BookSession Open(BooksProject p, IProgress<string>? progress = null)
    {
        var bsas = new List<BsaArchive>();
        try
        {
            foreach (var s in p.Sources.Where(s => s.Kind == "bsa" && File.Exists(s.Path)))
            {
                try { bsas.Add(BsaArchive.Open(s.Path)); } catch { }
            }
            var plugins = p.Plugins.Where(x => File.Exists(x.Path)).ToList();
            var headers = plugins.ToDictionary(x => x, x => (IReadOnlyList<string>)RevoiceService.ReadMasters(x.Path));
            var files = new List<PluginFile>();
            foreach (var pl in RevoiceService.Ordered(plugins, x => headers[x]))
            {
                progress?.Report("Reading " + pl.FileName);
                files.Add(PluginFile.Read(pl.Path, Signatures));
            }
            var missing = new List<string>();
            StringsTable? Strings(PluginFile f)
            {
                var folder = Path.Combine(Path.GetDirectoryName(f.Path)!, "Strings");
                if (StringsTable.Exists(folder, f.BaseName, p.Language)) return StringsTable.Load(folder, f.BaseName, p.Language);
                if (!string.IsNullOrWhiteSpace(_settings.StringsFolder) && StringsTable.Exists(_settings.StringsFolder, f.BaseName, p.Language))
                    return StringsTable.Load(_settings.StringsFolder, f.BaseName, p.Language);
                foreach (var b in bsas) if (StringsTable.FromArchive(b, f.BaseName, p.Language) is { } t) return t;
                // Strings inside the plugin's own archive (Mod.bsa next to Mod.esp).
                var own = Path.Combine(Path.GetDirectoryName(f.Path)!, f.BaseName + ".bsa");
                if (File.Exists(own))
                {
                    try { using var ob = BsaArchive.Open(own); if (StringsTable.FromArchive(ob, f.BaseName, p.Language) is { } t2) return t2; } catch { }
                }
                return null;
            }
            progress?.Report("Indexing NPCs and books");
            var idx = DialogueIndex.Build(files, Strings);
            var books = new Dictionary<FormKey, RawBook>();
            var edids = new Dictionary<FormKey, string>();
            var keywords = new Dictionary<FormKey, string>();
            foreach (var f in files)
            {
                foreach (var r in f.Records)
                {
                    var key = FormKey.Resolve(f, r.FormId);
                    if (r.Signature is "NPC_" or "LVLN") { if (r.EditorId is { Length: > 0 } e) edids[key] = e; continue; }
                    if (r.Signature == "KYWD") { if (r.EditorId is { Length: > 0 } e) keywords[key] = e; continue; }
                    if (r.Signature != "BOOK") continue;
                    if (r.IsDeleted) { books.Remove(key); continue; }
                    var data = r.Get("DATA")?.Data;
                    var kw = r.Get("KWDA")?.Data;
                    var kws = new List<string>();
                    if (kw != null)
                        for (int i = 0; i + 4 <= kw.Length; i += 4)
                        {
                            var k = FormKey.Resolve(f, BitConverter.ToUInt32(kw, i));
                            kws.Add(keywords.TryGetValue(k, out var kn) ? kn : k.ToString());
                        }
                    string Text(string sig) => r.Get(sig)?.Data is { } d ? f.DecodeLString(d) ?? "" : "";
                    books[key] = new RawBook
                    {
                        Key = key,
                        Plugin = idx.Spelling.TryGetValue(key.Plugin, out var sp) ? sp : f.Masters.Concat(new[] { f.FileName }).FirstOrDefault(m => m.Equals(key.Plugin, StringComparison.OrdinalIgnoreCase)) ?? key.Plugin,
                        EditorId = r.EditorId ?? "",
                        Title = Text("FULL"),
                        Raw = Text("DESC"),
                        Flags = data is { Length: > 0 } ? data[0] : (byte)0,
                        Keywords = kws,
                        Winner = f.FileName,
                    };
                }
            }
            return new BookSession { Index = idx, Books = books, EditorIds = edids, Signature = SessionSignature(p), MissingStrings = idx.MissingStrings.ToList() };
        }
        finally
        {
            foreach (var b in bsas) b.Dispose();
        }
    }

    /// <summary>Kind and default exclusion of a record, from its flags, keywords and text.</summary>
    public static (string Kind, string Exclude, bool Suggest) Classify(RawBook r, string inGame)
    {
        bool recipe = r.Keywords.Any(k => k.Equals("VendorItemRecipe", StringComparison.OrdinalIgnoreCase))
            || r.EditorId.Contains("Recipe", StringComparison.OrdinalIgnoreCase) || r.Title.StartsWith("Recipe", StringComparison.OrdinalIgnoreCase)
            || r.Title.Contains("Forge Recipe", StringComparison.OrdinalIgnoreCase);
        bool tome = r.TeachesSpell || r.Keywords.Any(k => k.Equals("VendorItemSpellTome", StringComparison.OrdinalIgnoreCase));
        int words = BookText.WordCount(inGame);
        // Words beyond the title: a tome or recipe that is more than its name and a list of ingredients.
        int extra = Math.Max(0, words - BookText.WordCount(r.Title));
        string kind = tome ? "tome" : recipe ? "recipe" : r.TeachesSkill ? "skill" : Guess(r);
        if (words == 0) return (kind, "No text", false);
        if (tome) return extra >= 8 ? (kind, "Spell tome", true) : (kind, "Spell tome", false);
        if (recipe) return extra >= 25 ? (kind, "Recipe", true) : (kind, "Recipe", false);
        if (BookText.UnreadableShare(r.Raw) > 0.5) return (kind, "Written in a script the player can't read", false);
        if (r.EditorId.StartsWith("QA", StringComparison.OrdinalIgnoreCase) || r.EditorId.StartsWith("Dummy", StringComparison.OrdinalIgnoreCase)
            || r.EditorId.Contains("Debug", StringComparison.OrdinalIgnoreCase) || r.EditorId.Contains("Test", StringComparison.Ordinal))
            return (kind, "Test or debug record", false);
        return (kind, "", false);
    }

    private static string Guess(RawBook r)
    {
        var t = (r.Title + " " + r.EditorId).ToLowerInvariant();
        if (t.Contains("journal") || t.Contains("diary") || t.Contains("log")) return "journal";
        if (t.Contains("letter") || t.Contains("missive")) return "letter";
        if (t.Contains("note") || t.Contains("orders") || t.Contains("contract") || t.Contains("bounty") || t.Contains("deed")) return "note";
        return "book";
    }

    private static string TextKey(string inGame) => Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(BookText.OneLine(inGame).ToLowerInvariant())))[..16];

    private static string StemOf(string editorId) => string.Concat(editorId.ToLowerInvariant().Where(c => char.IsLetterOrDigit(c) || c == '_'));

    /// <summary>
    /// Rebuild the book list from the plugins (project source "plugins"): books defined or changed by a target plugin.
    /// Records with exactly the same text share one audio file. Edits, authors, parts and takes of books that still exist are kept.
    /// </summary>
    public BooksScanReport Scan(BooksProject p, BookSession s)
    {
        var rep = new BooksScanReport { MissingMasters = s.Index.MissingMasters.OrderBy(x => x).ToList(), MissingStrings = s.MissingStrings.ToList() };
        var targets = new HashSet<string>(p.Plugins.Where(x => x.Target).Select(x => x.FileName), StringComparer.OrdinalIgnoreCase);
        var raws = s.Books.Values.Where(b => targets.Contains(b.Plugin) || targets.Contains(b.Winner))
            .OrderBy(b => s.Index.LoadOrder.FindIndex(x => x.Equals(b.Plugin, StringComparison.OrdinalIgnoreCase))).ThenBy(b => b.Key.Id).ToList();
        rep.Records = raws.Count;
        var byRecord = new Dictionary<(string, uint), Book>();
        foreach (var b in p.Books) foreach (var r in b.Records) byRecord[(r.Plugin.ToLowerInvariant(), r.FormId)] = b;
        var fresh = new List<Book>();
        var byText = new Dictionary<string, Book>();
        foreach (var r in raws)
        {
            var inGame = BookText.Clean(r.Raw);
            var (kind, exclude, suggest) = Classify(r, inGame);
            var reference = new BookRecordRef { Plugin = r.Plugin, FormId = r.Key.Id, EditorId = r.EditorId };
            string tk = inGame.Length > 0 ? TextKey(inGame) : "empty:" + r.Key;
            if (inGame.Length > 0 && byText.TryGetValue(tk, out var same))
            {
                if (!same.Records.Any(x => x.FormId == reference.FormId && x.Plugin.Equals(reference.Plugin, StringComparison.OrdinalIgnoreCase))) same.Records.Add(reference);
                rep.Merged++;
                continue;
            }
            byRecord.TryGetValue((r.Plugin.ToLowerInvariant(), r.Key.Id), out var old);
            var book = old != null && !fresh.Contains(old) ? old : new Book { Stem = StemOf(r.EditorId) };
            if (book.Stem.Length == 0) book.Stem = "book" + r.Key.Id.ToString("x6");
            book.Records = new() { reference };
            book.Gone = false;
            bool textChanged = book.InGame != inGame || book.Title != r.Title;
            book.Title = r.Title;
            book.InGame = inGame;
            book.Raw = r.Raw;
            book.Kind = kind;
            if (old == null)
            {
                book.Excluded = exclude.Length > 0 && !suggest;
                book.ExcludeReason = exclude;
                book.SuggestInclude = suggest;
            }
            else if (book.ExcludeReason != "user") { book.ExcludeReason = exclude; book.SuggestInclude = suggest; if (exclude.Length == 0) book.Excluded = false; }
            if (!book.SpokenEdited && (textChanged || book.Spoken.Length == 0))
            {
                book.Spoken = p.AutoSpokenRules ? BookText.DefaultSpoken(r.Title, inGame, out _, out _) : (r.Title.Length > 0 ? r.Title + BookText.TitleSep.TrimEnd() + "\n" : "") + inGame;
                if (!book.ManualBreaks) book.Breaks.Clear();
            }
            byText[tk] = book;
            fresh.Add(book);
        }
        // Unique stems (two plugins can use the same EditorID).
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var b in fresh)
        {
            var stem = b.Stem; int n = 2;
            while (!seen.Add(stem)) stem = b.Stem + "_" + n++;
            b.Stem = stem;
        }
        // Keep books that disappeared but have audio or edits (marked gone), drop the rest.
        var keep = p.Books.Where(b => !fresh.Contains(b) && (b.SpokenEdited || p.Lines.Any(l => l.Stem == b.Stem && l.Takes.Count > 0))).ToList();
        foreach (var b in keep) b.Gone = true;
        p.Books = fresh.Concat(keep).ToList();
        Attribute(p, s, p.Books.Where(b => !b.AuthorConfirmed && !b.Gone));
        rep.Books = fresh.Count;
        rep.Excluded = fresh.Count(b => b.Excluded);
        rep.Npc = fresh.Count(b => b.Reader == "npc");
        p.LastScan = rep;
        return rep;
    }

    /// <summary>Find authors and voices for books the user hasn't confirmed (see <see cref="BookAuthors"/>).</summary>
    public static void Attribute(BooksProject p, BookSession s, IEnumerable<Book> books)
    {
        var finder = new BookAuthors(s.Index, s.EditorIds, UespReference.Shared);
        foreach (var b in books)
        {
            if (b.AuthorConfirmed || b.AuthorSource == "set" || b.AuthorSource == "user") continue;
            var f = finder.Find(b);
            b.Author = f.Name;
            b.AuthorSource = f.Source;
            b.AuthorCertainty = f.Certainty;
            b.AuthorNpc = f.Npc != null ? f.Npc.Key.ToString() : "";
            b.AuthorNpcName = f.Npc?.Display ?? "";
            b.VoiceType = f.VoiceType;
            b.VoiceTypeCandidates = f.Candidates;
            b.VoiceTypeVia = f.Via;
            b.VoiceTypeCertainty = f.VoiceTypeCertainty;
            // The rule: an author NPC with a vanilla voice type is read in that voice; everyone else by the narrator.
            b.Reader = ReadsAsNpc(b, f) ? "npc" : "narrator";
        }
    }

    /// <summary>
    /// The voice rule: a letter, note or journal whose sender or owner is an NPC with one vanilla voice type is read in
    /// that voice. Books (volumes) credited to an author stay with the narrator, as do creatures, custom voice types and
    /// authors without an NPC record.
    /// </summary>
    public static bool ReadsAsNpc(Book b, AuthorFinding f)
    {
        if (f.Npc == null || f.VoiceType.Length == 0 || !f.Vanilla) return false;
        if (f.VoiceType.StartsWith("Cr", StringComparison.Ordinal)) return false;
        if (f.Source is "signature" or "title") return true;
        return b.Kind is "note" or "letter" or "journal";
    }

    /// <summary>Voices (line voice keys) that read a book, given the project's narrators and voice settings.</summary>
    public static List<string> VoicesFor(BooksProject p, Book b)
    {
        if (b.Excluded || b.Gone) return new();
        // A voice picked for this book only: one of the narrators, or any NPC voice type set up in the Voices tab.
        if (b.VoiceOverride.StartsWith("n:") && p.Narrators.Any(n => BooksProject.NarratorKey(n.Id) == b.VoiceOverride)) return new() { b.VoiceOverride };
        if (b.VoiceOverride.StartsWith("vt:") && p.VoiceTypes.ContainsKey(b.VoiceOverride[3..])) return new() { b.VoiceOverride };
        if (b.Reader == "npc" && b.VoiceType.Length > 0)
        {
            if (!p.NpcVoices) return new();
            if (!(p.VoiceTypes.TryGetValue(b.VoiceType, out var vt) && vt.UseNarrator)) return new() { BooksProject.VoiceTypeKey(b.VoiceType) };
        }
        return p.Narrators.Select(n => BooksProject.NarratorKey(n.Id)).ToList();
    }

    /// <summary>The longest text one request may have for this project's engine.</summary>
    public static int Ceiling(BooksProject p) => p.Voice.Engine switch
    {
        "voxcpm" or "omnivoice" => Math.Max(p.PartMax, 3000),
        _ => ModelCapabilities.MaxCharacters(p.Voice.Eleven.ModelId),
    };

    /// <summary>The parts of a book's main text (stored breaks when the user moved them, else automatic).</summary>
    public static List<TextPart> PartsOf(BooksProject p, Book b) =>
        b.ManualBreaks && b.Breaks.Count > 0 ? BookSplitter.FromBreaks(b.Spoken, b.Breaks) : BookSplitter.Split(b.Spoken, p.PartTarget, p.PartMax, Ceiling(p));

    /// <summary>
    /// Bring the generation lines in line with the books: one line per voice × book × part (× variant). Lines that keep
    /// their id keep their takes; their text is updated (a changed text marks the take "changed since take"). Lines no
    /// longer needed keep their takes but leave the list (orphans) until the user deletes them.
    /// </summary>
    public static void RebuildLines(BooksProject p)
    {
        var old = p.Lines.ToDictionary(l => l.LineId);
        var fresh = new List<BookLine>();
        foreach (var b in p.Books)
        {
            var voices = VoicesFor(p, b);
            if (voices.Count == 0) continue;
            var texts = new List<(string Variant, string Text, List<TextPart> Parts)> { ("", b.Spoken, PartsOf(p, b)) };
            foreach (var v in b.Variants.Where(v => v.Spoken.Trim().Length > 0))
                texts.Add((v.Id, v.Spoken, BookSplitter.Split(v.Spoken, p.PartTarget, p.PartMax, Ceiling(p))));
            foreach (var voice in voices)
                foreach (var (variant, text, parts) in texts)
                    for (int i = 0; i < parts.Count; i++)
                    {
                        var part = parts[i];
                        var id = BookLine.MakeId(voice, b.Stem, variant, i + 1);
                        var l = old.TryGetValue(id, out var o) ? o : new BookLine { LineId = id, Stem = b.Stem, Voice = voice, Part = i + 1, Variant = variant };
                        var spoken = text.Substring(part.Start, part.Length).Trim();
                        l.Start = part.Start; l.End = part.End; l.ParagraphEnd = part.ParagraphEnd;
                        l.FinalLine = spoken;
                        l.OriginalLine = spoken;
                        if (l.Spoken != spoken) { l.Spoken = spoken; l.RefreshStale(); }
                        l.Excluded = false;
                        l.FileStem = $"{b.Stem}_p{i + 1:00}";
                        fresh.Add(l);
                    }
        }
        // Orphans: lines with takes that are no longer needed are kept (excluded) so the work isn't lost.
        var ids = fresh.Select(l => l.LineId).ToHashSet();
        foreach (var l in p.Lines.Where(l => !ids.Contains(l.LineId) && l.Takes.Count > 0)) { l.Excluded = true; fresh.Add(l); }
        p.Lines = fresh;
    }

    /// <summary>Lines of one book for one voice (and variant), in part order.</summary>
    public static List<BookLine> LinesOf(BooksProject p, string voice, string stem, string variant = "") =>
        p.Lines.Where(l => !l.Excluded && l.Voice == voice && l.Stem == stem && l.Variant == variant).OrderBy(l => l.Part).ToList();

    /// <summary>"ready" (every part has a current take), "partial", "stale" (a take no longer matches its text) or "missing".</summary>
    public static string StatusOf(IReadOnlyList<BookLine> parts)
    {
        if (parts.Count == 0) return "none";
        int done = parts.Count(l => l.SelectedTake != null && l.Status == Phase2Status.Generated);
        if (parts.Any(l => l.Status == Phase2Status.Stale)) return "stale";
        if (done == parts.Count) return "ready";
        return parts.Any(l => l.SelectedTake != null) ? "partial" : "missing";
    }

    /// <summary>A display name for a voice key.</summary>
    public static string VoiceName(BooksProject p, string voice)
    {
        if (voice.StartsWith("n:")) return p.Narrators.FirstOrDefault(n => "n:" + n.Id == voice)?.Name ?? "Narrator";
        return voice.StartsWith("vt:") ? voice[3..] : voice;
    }

    /// <summary>The engine settings for a voice: the project's shared settings with this voice's engine and voice id.</summary>
    public static VoicePack? VoicePackFor(BooksProject p, string voice)
    {
        string? engine, id, name;
        if (voice.StartsWith("n:"))
        {
            var n = p.Narrators.FirstOrDefault(x => "n:" + x.Id == voice);
            if (n == null) return null;
            (engine, id, name) = (n.Engine, n.VoiceId, n.VoiceName);
        }
        else
        {
            if (!p.VoiceTypes.TryGetValue(voice[3..], out var vt)) return null;
            (engine, id, name) = (vt.Engine, vt.VoiceId, vt.VoiceName);
        }
        if (string.IsNullOrWhiteSpace(id)) return null;
        var v = System.Text.Json.JsonSerializer.Deserialize<VoicePack>(System.Text.Json.JsonSerializer.Serialize(p.Voice, Library.Json), Library.Json)!;
        engine ??= "elevenlabs";
        v.Id = "vb-" + engine + "-" + id;
        v.Name = name ?? id;
        v.Engine = engine;
        v.SettingsLocked = false;
        switch (engine)
        {
            case "voxcpm": v.VoxCpm.Voice = id; break;
            case "omnivoice": v.OmniVoice.Voice = id; break;
            default: v.Eleven.VoiceId = id; v.Eleven.VoiceName = v.Name; break;
        }
        return v;
    }

    /// <summary>The loudness level and gender of a voice (for the audio chain preset).</summary>
    public static BookAudioSettings AudioFor(BooksProject p, string voice, Book? book = null)
    {
        string level = "loud"; bool female = false;
        if (voice.StartsWith("n:") && p.Narrators.FirstOrDefault(x => "n:" + x.Id == voice) is { } n) { level = n.Level; female = n.Female; }
        else if (voice.StartsWith("vt:") && p.VoiceTypes.TryGetValue(voice[3..], out var vt)) { level = vt.Level; female = vt.Female; }
        var s = p.Audio.Clone();
        if (string.Equals(level, "normal", StringComparison.OrdinalIgnoreCase))
        {
            var preset = BookAudioSettings.Preset("normal", female);
            s.LoudnessLufs = preset.LoudnessLufs; s.LimiterThresholdDb = preset.LimiterThresholdDb; s.LimiterOutputDb = preset.LimiterOutputDb; s.ExtraGainDb = preset.ExtraGainDb;
        }
        if (book?.ParagraphPauseMs is { } pp) s.ParagraphPauseMs = Math.Clamp(pp, 0, 5000);
        return s;
    }

    /// <summary>Neighbouring text sent with a request (ElevenLabs request stitching): the end of the previous part and the start of the next.</summary>
    public static (string? Previous, string? Next) ContextFor(BooksProject p, BookLine l)
    {
        if (!p.SendContext || p.Voice.Engine is "voxcpm" or "omnivoice" || p.Voice.Eleven.ModelId.Contains("v3", StringComparison.OrdinalIgnoreCase)) return (null, null);
        var parts = LinesOf(p, l.Voice, l.Stem, l.Variant);
        int i = parts.FindIndex(x => x.LineId == l.LineId);
        string? prev = i > 0 ? Tail(parts[i - 1].Spoken, 1000) : null;
        string? next = i >= 0 && i + 1 < parts.Count ? Head(parts[i + 1].Spoken, 1000) : null;
        return (prev, next);
    }

    private static string Tail(string s, int n) => s.Length <= n ? s : s[^n..];
    private static string Head(string s, int n) => s.Length <= n ? s : s[..n];
}
